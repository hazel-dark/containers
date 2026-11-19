using System.Runtime.InteropServices;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed partial class WindowsJobQueryOracleTests
{
    private sealed class Server2025FactAttribute : FactAttribute
    {
        public Server2025FactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build != 26100 ||
                RuntimeInformation.ProcessArchitecture != Architecture.X64)
                Skip = "Requires Server 2025 x64 with the Containers feature.";
        }
    }

    private sealed class UnionVolumeFactAttribute : FactAttribute
    {
        public UnionVolumeFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build != 26100 ||
                RuntimeInformation.ProcessArchitecture != Architecture.X64 || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WC_SILO_TEST_SCRATCH")))
                Skip = "Requires Server 2025 x64 with Containers and WC_SILO_TEST_SCRATCH on the test volume.";
        }
    }

    [UnionVolumeFact]
    public void FailedBindAfterMigrationRestoresTombstonesAndAllowsRetry()
    {
        var root = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("WC_SILO_TEST_SCRATCH")!,
            "wc-union-" + Guid.NewGuid().ToString("N"))).FullName;
        var scratch = Path.Combine(root, Guid.NewGuid().ToString("B"));
        var layer = Directory.CreateDirectory(Path.Combine(root, "layer")).FullName;
        Directory.CreateDirectory(Path.Combine(scratch, "Windows"));
        Directory.CreateDirectory(Path.Combine(scratch, "WcSandboxState"));
        File.WriteAllText(Path.Combine(layer, "marker"), "visible");
        File.WriteAllText(Path.Combine(layer, "removed"), "deleted in union");
        var config = Path.Combine(scratch, "Windows", "container_tombstones.json");
        WciLayer[] layers = [new WciLayer(Guid.NewGuid(), layer)];
        using var job = new SafeFileHandle(NativeApi.Instance.CreateJob(), ownsHandle: true);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription());
        try
        {
            SiloFileSystemNamespace.Setup(scratch, layers);
            Assert.True(File.Exists(Path.Combine(scratch, "removed")));
            File.Delete(Path.Combine(scratch, "removed"));
            SiloFileSystemNamespace.Cleanup(scratch);
            Assert.True(File.Exists(config));
            var missingBatch = new BindBatch(Path.Combine(root, "absent-batch"));
            var error = Assert.Throws<SiloException>(() => SiloFileSystemNamespace.Setup(scratch, layers, [missingBatch], job));
            Assert.Equal(2, error.Win32Error);
            Assert.True(File.Exists(config));
            SiloFileSystemNamespace.Setup(scratch, layers);
            Assert.False(File.Exists(config));
            Assert.False(File.Exists(Path.Combine(scratch, "removed")));
            Assert.Equal("visible", File.ReadAllText(Path.Combine(scratch, "marker")));
        }
        finally
        {
            SiloFileSystemNamespace.Cleanup(scratch);
            Directory.Delete(root, recursive: true);
        }
    }

    [Server2025Fact]
    public void JobQueriesMatchInboxBeforeAndAfterAChildExits()
    {
        using var job = new SafeFileHandle(NativeApi.Instance.CreateJob(), ownsHandle: true);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription());
        AssertFirstBoot(expected: true);
        var sessionResult = WcGetContainerServiceSessionId(job, out var nativeSession);
        if (sessionResult < 0)
            Assert.Equal(sessionResult, Assert.Throws<SiloException>(() => silo.ServiceSessionId).HResult);
        else
            Assert.Equal(nativeSession, silo.ServiceSessionId);

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using (var process = silo.LaunchApplication($"\"{cmd}\" /d /c exit 0"))
            Assert.Equal(0u, WaitForSingleObject(process, 30_000));
        Assert.True(SpinWait.SpinUntil(() => silo.IsQuiescent, TimeSpan.FromSeconds(10)), "The job still reports an assigned process.");
        AssertFirstBoot(expected: false);

        void AssertFirstBoot(bool expected)
        {
            Assert.Equal(0, WcIsFirstBoot(job, out var first));
            Assert.Equal(expected, first != 0);
            Assert.Equal(expected, silo.IsFirstBoot);
        }
    }

    [LibraryImport("container.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WcIsFirstBoot(SafeFileHandle job, out byte first);

    [LibraryImport("container.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WcGetContainerServiceSessionId(SafeFileHandle job, out uint session);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
