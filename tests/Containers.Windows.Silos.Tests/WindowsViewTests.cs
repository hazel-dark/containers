using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Containers.Windows.Silos.DescriptionModel;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed partial class WindowsViewTests
{
    private const string ScratchVariable = "WC_SILO_TEST_SCRATCH";
    private const string NamespaceVariable = "WC_SILO_TEST_NAMESPACE_XML";

    // Candidate for session 0, where the Win32 named-object directory is \BaseNamedObjects.
    // Wc 10.0.20348.1: root setup at 0x180024348, directories at 0x180024008, links at 0x180024908.
    // Shadow the host root so dependencies resolve. Give the mutex directory its own objects.
    // The local-scope Global link routes the Win32 prefix within the silo.
    // https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools/blob/852b1586b8c4527f8be3f486801e4830220c76d0/NtCoreLib/NtDirectory.cs
    private const string DefaultNamespaceXml = """
        <container><namespace><ob shadow="true">
          <objdir name="BaseNamedObjects" clonesd="\BaseNamedObjects" shadow="false">
            <symlink name="Global" path="\BaseNamedObjects" scope="local" />
            <symlink name="Local" path="\BaseNamedObjects" scope="local" />
          </objdir>
        </ob></namespace></container>
        """;

    private sealed class WindowsViewFactAttribute : FactAttribute
    {
        public WindowsViewFactAttribute(bool checkNamespace)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, SiloPlatform.MinimumBuild) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                Skip = $"Requires a Windows x64 test host at build {SiloPlatform.MinimumBuild} or later.";
            else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ScratchVariable)))
                Skip = "Set WC_SILO_TEST_SCRATCH to an existing directory on the Windows test volume.";
            else if (checkNamespace && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NamespaceVariable)) &&
                Process.GetCurrentProcess().SessionId != 0)
                Skip = "The inline private Global candidate requires session 0. Supply WC_SILO_TEST_NAMESPACE_XML for another session.";
        }
    }

    [WindowsViewFact(false)]
    public void BindMappingOverridesOnlyTheChildView() => CheckViews(checkNamespace: false);

    [WindowsViewFact(true)]
    public void ChildUsesPrivateGlobalMutexAndOverriddenFile() => CheckViews(checkNamespace: true);

    [WindowsViewFact(false)]
    public void RepeatedCleanupPreservesLiveJobFileBinding() =>
        CheckViews(checkNamespace: false, cleanupWhileChildIsRunning: true);

    private static void CheckViews(bool checkNamespace, bool cleanupWhileChildIsRunning = false)
    {
        Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, SiloPlatform.MinimumBuild) && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "Requires an explicitly opted-in Windows Server 2022 x64 host; Linux builds are not Windows execution evidence.");
        var scratch = Environment.GetEnvironmentVariable(ScratchVariable)!;
        Assert.True(Directory.Exists(scratch), scratch);
        var namespaceOverride = Environment.GetEnvironmentVariable(NamespaceVariable);
        if (checkNamespace && string.IsNullOrEmpty(namespaceOverride))
            Assert.Equal(0, Process.GetCurrentProcess().SessionId);
        var root = Path.Combine(scratch, "wc-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var view = Directory.CreateDirectory(Path.Combine(root, "view")).FullName;
            var backing = Directory.CreateDirectory(Path.Combine(root, "backing")).FullName;
            var file = Path.Combine(view, "probe.txt");
            var target = Path.Combine(backing, "probe.txt");
            var neighbor = Path.Combine(view, "neighbor.txt");
            var hostMarker = "host-" + Guid.NewGuid().ToString("N");
            var childMarker = "child-" + Guid.NewGuid().ToString("N");
            var neighborMarker = "neighbor-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(file, hostMarker);
            File.WriteAllText(target, childMarker);
            File.WriteAllText(neighbor, neighborMarker);
            // A directory-wide mapping would expose this decoy instead of the host neighbor.
            var decoy = Path.Combine(backing, "neighbor.txt");
            File.WriteAllText(decoy, "wrong-directory-view");
            var config = checkNamespace
                ? SiloDescriptionXml.Parse(string.IsNullOrEmpty(namespaceOverride) ? DefaultNamespaceXml : File.ReadAllText(namespaceOverride))
                : new SiloDescription();
            var fs = config.FileSystem ?? new FileSystemConfiguration();
            // Exactly one literal FILE-to-FILE mapping is added; no directory or wildcard mapping.
            config = config with { FileSystem = fs with { Bindings = [.. fs.Bindings, new BindMapping(file, target)] } };
            var mutexName = @"Global\wc-view-" + Guid.NewGuid().ToString("N") + "-host";
            var childMutexName = @"Global\wc-view-" + Guid.NewGuid().ToString("N") + "-child";
            using var hostMutex = CreateNewMutex(mutexName);
            AssertMutexOpens(mutexName);
            AssertMutexMissing(childMutexName);
            var ready = Path.Combine(root, "ready.txt");
            var release = Path.Combine(root, "release.txt");
            var failure = Path.Combine(root, "failure.txt");
            using var silo = WindowsSilo.Create(config);
            var script = "$ErrorActionPreference='Stop'; $env:TMP=" + Quote(root) + "; $env:TEMP=" + Quote(root) +
                "; $env:TMPDIR=" + Quote(root) + "; try { Add-Type -TypeDefinition " + Quote(ChildProbeSource()) +
                "; [WcViewProbe]::Run(" + string.Join(",", new[] { file, neighbor, childMarker, neighborMarker,
                    mutexName, childMutexName, ready, release }.Select(Quote)) + "," + (checkNamespace ? "$true" : "$false") +
                "); exit 0 } catch { [IO.File]::WriteAllText(" + Quote(failure) + ",$_.Exception.ToString()); exit 1 }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            using var child = silo.LaunchApplication($"\"{shell}\" -NoProfile -NonInteractive -EncodedCommand {encoded}");
            try
            {
                var timer = Stopwatch.StartNew();
                var readinessWait = WAIT_EVENT.WAIT_TIMEOUT;
                var readinessError = 0;
                while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(60))
                {
                    // Process wait bounds the poll without spinning; readiness is published by rename.
                    readinessWait = WaitForSingleObject(child, 50);
                    readinessError = Marshal.GetLastPInvokeError();
                    if (readinessWait != WAIT_EVENT.WAIT_TIMEOUT)
                        break;
                }
                var diagnostics = File.Exists(failure) ? File.ReadAllText(failure) :
                    $"Child did not publish readiness. Wait={readinessWait}; Win32={readinessError}.";
                Assert.True(File.Exists(ready), diagnostics);
                Assert.Equal(childMarker, File.ReadAllText(ready));
                Assert.Equal(WAIT_EVENT.WAIT_TIMEOUT, WaitForSingleObject(child, 0));
                if (checkNamespace)
                {
                    // The child holds both its private same-name host mutex and a child-only
                    // mutex. Missing alone is insufficient: create/open host counterparts too.
                    AssertMutexMissing(childMutexName);
                    using var counterpart = CreateNewMutex(childMutexName);
                    AssertMutexOpens(childMutexName);
                    AssertMutexOpens(mutexName);
                    Assert.Equal(WAIT_EVENT.WAIT_TIMEOUT, WaitForSingleObject(child, 0));
                }
                else
                    AssertMutexOpens(childMutexName); // Host/child visibility control without a private namespace.
                Assert.Equal(hostMarker, File.ReadAllText(file));
                Assert.Equal(childMarker, File.ReadAllText(target));
                Assert.Equal(neighborMarker, File.ReadAllText(neighbor));
                Assert.Equal("wrong-directory-view", File.ReadAllText(decoy));
                if (cleanupWhileChildIsRunning)
                {
                    // On release the child reopens the visible path and checks its backing marker.
                    var id = silo.Identifier;
                    silo.Cleanup(null);
                    silo.Cleanup("");
                    Assert.Equal(id, silo.Identifier);
                }
                File.WriteAllText(release, hostMarker);
                var wait = WaitForSingleObject(child, 30_000);
                var waitError = Marshal.GetLastPInvokeError();
                diagnostics = File.Exists(failure) ? File.ReadAllText(failure) : "No child exception was recorded.";
                Assert.True(wait == WAIT_EVENT.WAIT_OBJECT_0, $"Child wait: {wait}; Win32={waitError}. {diagnostics}");
                var gotExitCode = GetExitCodeProcess(child, out var exitCode);
                var exitError = Marshal.GetLastPInvokeError();
                Assert.True(gotExitCode, $"GetExitCodeProcess failed: Win32={exitError}.");
                Assert.True(exitCode == 0, $"Child exit={exitCode}. {diagnostics}");
            }
            finally
            {
                // Do not remove mapped files while a timed-out/failed child can still use them.
                if (WaitForSingleObject(child, 0) != WAIT_EVENT.WAIT_OBJECT_0)
                {
                    silo.Terminate();
                    Assert.Equal(WAIT_EVENT.WAIT_OBJECT_0, WaitForSingleObject(child, 30_000));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SafeWaitHandle CreateNewMutex(string name)
    {
        var handle = CreateMutexW(0, false, name);
        var error = Marshal.GetLastPInvokeError();
        try
        {
            Assert.False(handle.IsInvalid, $"CreateMutexW({name}) failed: {(WIN32_ERROR)error} ({error}); not isolation success.");
            Assert.True(error != (int)WIN32_ERROR.ERROR_ALREADY_EXISTS, $"CreateMutexW({name}) opened an existing mutex.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void AssertMutexOpens(string name)
    {
        using var handle = OpenMutexW(SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE, false, name);
        var error = Marshal.GetLastPInvokeError();
        Assert.False(handle.IsInvalid, $"OpenMutexW({name}) failed: {(WIN32_ERROR)error} ({error}); not isolation success.");
    }

    private static void AssertMutexMissing(string name)
    {
        using var handle = OpenMutexW(SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE, false, name);
        var error = Marshal.GetLastPInvokeError();
        Assert.True(handle.IsInvalid, $"OpenMutexW({name}) unexpectedly opened a mutex across the namespace boundary.");
        Assert.True(error == (int)WIN32_ERROR.ERROR_FILE_NOT_FOUND,
            $"OpenMutexW({name}) returned {(WIN32_ERROR)error} ({error}), not ERROR_FILE_NOT_FOUND; access denied or a broken route is not isolation success.");
    }

    // Test-only Win32 glue compiled by Windows PowerShell 5.1. Numeric constants come
    // from CsWin32 SDK metadata 71.0.45-preview (wait/error/access values are not inferred).
    // https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-openmutexw
    // https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createmutexw
    private static string ChildProbeSource() => $$"""
        using System;
        using System.Diagnostics;
        using System.IO;
        using System.Runtime.InteropServices;
        using System.Threading;
        using Microsoft.Win32.SafeHandles;
        public static class WcViewProbe
        {
            const uint Synchronize = {{(uint)SYNCHRONIZATION_ACCESS_RIGHTS.SYNCHRONIZATION_SYNCHRONIZE}};
            const int FileNotFound = {{(int)WIN32_ERROR.ERROR_FILE_NOT_FOUND}};
            const int AlreadyExists = {{(int)WIN32_ERROR.ERROR_ALREADY_EXISTS}};
            const int AccessDenied = {{(int)WIN32_ERROR.ERROR_ACCESS_DENIED}};
            const int PathNotFound = {{(int)WIN32_ERROR.ERROR_PATH_NOT_FOUND}};
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
            static extern SafeWaitHandle CreateMutexW(IntPtr attributes, bool owner, string name);
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
            static extern SafeWaitHandle OpenMutexW(uint access, bool inherit, string name);
            [DllImport("kernel32.dll", ExactSpelling=true)]
            static extern void SetLastError(uint error);
            static string Error(int error)
            {
                string name = error == FileNotFound ? "ERROR_FILE_NOT_FOUND" : error == AlreadyExists ? "ERROR_ALREADY_EXISTS" :
                    error == AccessDenied ? "ERROR_ACCESS_DENIED" : error == PathNotFound ? "ERROR_PATH_NOT_FOUND" : "Win32";
                return name + " (" + error + ")";
            }
            static SafeWaitHandle CreateNew(string name)
            {
                // Windows PowerShell 5.1 uses .NET Framework, whose P/Invoke does not
                // clear last-error before calls. Do not mistake a stale 183 for a collision.
                SetLastError(0);
                SafeWaitHandle handle = CreateMutexW(IntPtr.Zero, false, name);
                int error = Marshal.GetLastWin32Error();
                if (handle.IsInvalid || error == AlreadyExists)
                {
                    handle.Dispose();
                    throw new Exception("CreateMutexW(" + name + ") failed or opened existing: " + Error(error));
                }
                return handle;
            }
            static void Open(string name, bool missing)
            {
                using (SafeWaitHandle handle = OpenMutexW(Synchronize, false, name))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (missing ? !handle.IsInvalid || error != FileNotFound : handle.IsInvalid)
                        throw new Exception("OpenMutexW(" + name + "), expected " + (missing ? "ERROR_FILE_NOT_FOUND" : "success") +
                            ", invalid=" + handle.IsInvalid + ", " + Error(error) + "; access denied/broken route is not isolation success.");
                }
            }
            public static void Run(string file, string neighbor, string childMarker, string neighborMarker,
                string hostMutex, string childMutex, string ready, string release, bool checkNamespace)
            {
                if (File.ReadAllText(file) != childMarker) throw new Exception("Mapped file did not contain child marker.");
                if (File.ReadAllText(neighbor) != neighborMarker) throw new Exception("Neighbor changed: mapping exceeded the single file.");
                // A fresh child must distinguish the expected open result before testing creation.
                Open(hostMutex, checkNamespace);
                Open(childMutex, true);
                using (SafeWaitHandle sameName = checkNamespace ? CreateNew(hostMutex) : null)
                using (SafeWaitHandle own = CreateNew(childMutex))
                {
                    Open(hostMutex, false);
                    Open(childMutex, false);
                    File.WriteAllText(ready + ".tmp", childMarker);
                    File.Move(ready + ".tmp", ready);
                    Stopwatch timer = Stopwatch.StartNew();
                    while (!File.Exists(release))
                    {
                        if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new Exception("Host did not release child.");
                        Thread.Sleep(50);
                    }
                    Open(hostMutex, false);
                    Open(childMutex, false);
                    if (File.ReadAllText(file) != childMarker || File.ReadAllText(neighbor) != neighborMarker)
                        throw new Exception("Child file view changed during host observations.");
                }
            }
        }
        """;

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial WAIT_EVENT WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeWaitHandle CreateMutexW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeWaitHandle OpenMutexW(SYNCHRONIZATION_ACCESS_RIGHTS access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}
