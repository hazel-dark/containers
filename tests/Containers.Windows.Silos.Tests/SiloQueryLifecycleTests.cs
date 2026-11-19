using System.Buffers.Binary;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class SiloQueryLifecycleTests
{
    private const int Job = 0x1234;
    private const int Key = 0x5678;
    private const int AccessDenied = unchecked((int)0xc0000022);
    private const int BufferTooSmall = unchecked((int)0xc0000023);
    private static readonly Guid Id = new Guid("1234abcd-789a-bcde-f123-abcdef102abc");

    [Fact]
    public void IdentityAndObjectRootUseNativeQueryBufferAndErrorContracts()
    {
        var calls = new RecordingCalls();
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, new NativeApi(calls));

        calls.Trace.Clear();
        Assert.Equal(Id, silo.Identifier);
        Assert.Equal(@"\Silos\7", silo.ObjectRoot);
        Assert.Equal(@"\Registry\WC\Silo1234abcd-789a-bcde-f123-abcdef102abcSuffix", silo.GetRegistryRootPath("Suffix"));
        Assert.Equal(["query:30:36", "query:37:52", "query:37:4096", "query:37:8192", "query:30:36"], calls.Trace);

        calls.IdStatus = AccessDenied;
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => silo.Identifier).NtStatus);
        calls.IdStatus = 0;
        calls.ObjectRootStatus = AccessDenied;
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => silo.ObjectRoot).NtStatus);
        Assert.Equal(["query:30:36", "query:37:52", "query:37:4096", "query:37:8192"], calls.Trace.TakeLast(4));
    }

    [Fact]
    public void QuiescenceReadsAssignedCountAndPassesNullReturnLength()
    {
        var calls = new RecordingCalls();
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, new NativeApi(calls));

        calls.Trace.Clear();
        calls.AssignedProcesses = 2;
        Assert.False(silo.IsQuiescent);
        calls.AssignedProcesses = 0;
        Assert.True(silo.IsQuiescent);
        Assert.Equal(["query:3:16", "query:3:16"], calls.Trace);
    }

    [Fact]
    public void RegistryCallsQueryBeforeIoAndUseTheNativePayloads()
    {
        var calls = new RecordingCalls();
        var api = new NativeApi(calls);
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, api);

        calls.Trace.Clear();
        silo.SetRegistryFlushEnabled(true);
        silo.SetRegistryFlushEnabled(false);
        silo.AddRuntimeVirtualKeys([]);
        Assert.Equal((nint)Key, api.GetRegistryHandle(Job, 0));
        Assert.Equal([
            "query:30:36", "registry-open", "io:220010:34120000000000000000000001000000",
            "query:30:36", "registry-open", "io:220010:34120000000000000100000000000000",
            "query:30:36", "registry-open", "io:220014:341200000000000000000000",
            "registry-open", "io:22001c:34120000000000000000000000000000"
        ], calls.Trace);
        Assert.All(calls.Devices, device => Assert.True(device.IsClosed));
    }

    [WindowsOnlyFact]
    public void ComKeySafeHandleTransfersToCaller()
    {
        var calls = new RecordingCalls();
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, new NativeApi(calls));

        using var key = silo.OpenComRegistryRoot();
        try
        {
            Assert.Equal((nint)Key, key.DangerousGetHandle());
            Assert.All(calls.Devices, device => Assert.True(device.IsClosed));
            silo.Dispose();
            Assert.False(key.IsClosed);
        }
        finally
        {
            // The test device supplies a synthetic HKEY.
            key.SetHandleAsInvalid();
        }
    }

    [Fact]
    public void NulTerminatedEmptyVolumeSkipsTheSiloTypeQuery()
    {
        var calls = new RecordingCalls();
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, new NativeApi(calls));

        calls.Trace.Clear();
        silo.Cleanup("\0ignored");
        Assert.Equal(["query:30:36", "registry-open", "io:220018:3412000000000000"], calls.Trace);
    }

    [Fact]
    public void RegistryIoFailureDoesNotPreventFilesystemCleanup()
    {
        var calls = new RecordingCalls { RegistryCleanupError = 5 };
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), false, new NativeApi(calls));

        calls.Trace.Clear();
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => silo.Cleanup(@"X:\")).NtStatus);
        Assert.Equal(["query:30:36", "registry-open", "io:220018:3412000000000000", $"query:38:{NativeJobConstants.ServerSiloBasicInformationSize}"], calls.Trace);
        Assert.All(calls.Devices, device => Assert.True(device.IsClosed));
    }

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "SafeRegistryHandle is only available on Windows.";
        }
    }

    private sealed unsafe class RecordingCalls : NativeApi.PlatformProvider
    {
        internal List<string> Trace { get; } = [];
        internal List<SafeFileHandle> Devices { get; } = [];
        internal int IdStatus { get; set; }
        internal int ObjectRootStatus { get; set; }
        internal uint AssignedProcesses { get; set; }
        internal int RegistryCleanupError { get; init; }

        internal override int SetJob(nint job, int informationClass, void* data, uint length)
        {
            Assert.Equal(Job, job);
            return 0;
        }

        internal override int QueryJob(nint job, int informationClass, void* data, uint length, uint* returned)
        {
            Assert.Equal(Job, job);
            Trace.Add($"query:{informationClass}:{length}");
            if (informationClass == 0x1e)
            {
                Assert.Equal(0x24u, length);
                Assert.True(returned == null);
                Assert.True(Id.TryWriteBytes(new Span<byte>(data, (int)length)));
                return IdStatus;
            }
            if (informationClass == 3)
            {
                Assert.Equal(0x10u, length);
                Assert.True(returned == null);
                var payload = new Span<byte>(data, (int)length);
                Assert.Equal("00000000010000000000000000000000", Convert.ToHexString(payload));
                BinaryPrimitives.WriteUInt32LittleEndian(payload, AssignedProcesses);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], 0);
                return 1;
            }
            if (informationClass == 0x26)
            {
#if SILO2025
                Assert.Equal(40u, length);
#else
                Assert.Equal(32u, length);
#endif
                Assert.True(returned == null);
                return AccessDenied;
            }

            Assert.Equal(0x25, informationClass);
            Assert.True(returned != null);
            *returned = 0x1400;
            if (length < 0x2000)
                return BufferTooSmall;
            const string root = @"\Silos\7";
            var buffer = (char*)((byte*)data + 0x10);
            root.AsSpan().CopyTo(new Span<char>(buffer, root.Length));
            *(UnicodeString*)data = new UnicodeString(buffer, root.Length);
            return ObjectRootStatus;
        }

        internal override uint GetCompartment(nint job) => 1;

        internal override SafeFileHandle OpenRegistryDriver()
        {
            Trace.Add("registry-open");
            var device = new SafeFileHandle(0x789, ownsHandle: false);
            Devices.Add(device);
            return device;
        }

        internal override int DeviceControl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
        {
            Assert.False(handle.IsClosed);
            Trace.Add($"io:{code:x}:{Convert.ToHexString(input)}");
            if (code == 0x22001c)
            {
                Assert.Equal(8, output.Length);
                BinaryPrimitives.WriteInt64LittleEndian(output, Key);
            }
            else
                Assert.True(output.IsEmpty);
            return code == 0x220018 ? RegistryCleanupError : 0;
        }
    }
}
