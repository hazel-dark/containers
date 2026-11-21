using System.Buffers.Binary;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class SiloLifecycleTests
{
    private const int AccessDenied = unchecked((int)0xc0000022);

    private static SiloDescription AllProviders =>
        new SiloDescription
    {
        Job = new JobConfiguration
        {
            Cpu = new CpuConfiguration(3, 5000),
            MemoryLimit = 0x0102030405060708,
            KillOnLastHandleClose = true,
            SystemRoot = @"C:\Windows",
            EnableThreadImpersonation = true
        },
        ObjectNamespace = new ObjectNamespaceConfiguration(),
        FileSystem = new FileSystemConfiguration(),
        Registry = new RegistryConfiguration(),
        NetworkCompartment = "test",
        Mounts = [],
        NamedPipes = []
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguresProvidersInNativeOrderForBothModes(bool server)
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, server, api);
        string[] common = ["objects", "files", "registry", "network", "mounts", "pipes"];
        string[] expected = [
            "create", "id", "set:15", "set:9", "set:9", "set:35", "system-root", "set:47",
            server ? "register" : "assign:-7", ..common
        ];
        Assert.Equal(server ? [..expected, "validate-image"] : expected, api.Calls);
        Assert.Equal("0700000003000000", Convert.ToHexString(api.JobWrites[0].Bytes));
        Assert.Equal(0x200000u, BinaryPrimitives.ReadUInt32LittleEndian(api.JobWrites[1].Bytes.AsSpan(0x10)));
        Assert.Equal(0x0102030405060708ul, BinaryPrimitives.ReadUInt64LittleEndian(api.JobWrites[1].Bytes.AsSpan(0x90)));
        Assert.Equal(0x2000u, BinaryPrimitives.ReadUInt32LittleEndian(api.JobWrites[2].Bytes.AsSpan(0x10)));
        Assert.Empty(api.JobWrites[3].Bytes);
        Assert.Equal(new byte[] { 1 }, api.JobWrites[4].Bytes);

        silo.Cleanup();
        silo.Dispose();
        Assert.Equal(["id", "cleanup-network", "cleanup-registry", "cleanup-files", "close"], api.Calls.TakeLast(5));
    }

    [Fact]
    public void EmptyNonServerSkipsRegistrationAndAssignmentButCleanupQueriesNativeProviders()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(new SiloDescription(), false, api);
        Assert.Equal(["create", "id", "set:9", "set:35"], api.Calls);
        Assert.Equal(0x400000u, BinaryPrimitives.ReadUInt32LittleEndian(api.JobWrites[0].Bytes.AsSpan(0x10)));
        silo.Dispose();
        Assert.Equal(["id", "cleanup-network", "cleanup-registry", "cleanup-files", "close"], api.Calls.TakeLast(5));
    }

    [Fact]
    public void BootSendsSmOnlyAfterSuccessfulNativeInitialization()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, true, api);
        using var deleteEvent = new SafeWaitHandle(0x3456, ownsHandle: false);

        api.FailAt = "set:40";
        Assert.Throws<InjectedFailure>(() => silo.BootServer(deleteEvent));
        Assert.DoesNotContain("boot", api.Calls);
        Assert.Equal(["id", "set:40"], api.Calls.TakeLast(2));
        api.FailAt = null;
        silo.BootServer(deleteEvent);
        Assert.Equal(["id", "set:40", "id", "boot"], api.Calls.TakeLast(4));
#if SILO2025
        Assert.Equal("5634000000000000F465000000000000", Convert.ToHexString(api.JobWrites[^1].Bytes));
#else
        Assert.Equal("56340000000000000000000000000000", Convert.ToHexString(api.JobWrites[^1].Bytes));
#endif
    }

    [Fact]
    public void BootFailurePreservesThePrimaryErrorAndMakesNoExtraNativeJobWrite()
    {
        var api = new RecordingApi { FailAt = "boot" };
        using var silo = WindowsSilo.Create(AllProviders, true, api);
        using var deleteEvent = new SafeWaitHandle(0x3456, ownsHandle: false);
        var error = Assert.Throws<InjectedFailure>(() => silo.BootServer(deleteEvent));
        Assert.Equal("boot", error.Message);
        Assert.Equal(["id", "set:40", "id", "boot"], api.Calls.TakeLast(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootStopsAtEitherFailingIdentityQuery(bool failAfterInitialization)
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, true, api);
        using var deleteEvent = new SafeWaitHandle(0x3456, ownsHandle: false);
        api.Calls.Clear();
        api.JobWrites.Clear();
        if (failAfterInitialization)
            api.IdStatuses.Enqueue(0);
        api.IdStatuses.Enqueue(AccessDenied);

        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => silo.BootServer(deleteEvent)).NtStatus);
        if (failAfterInitialization)
        {
            Assert.Equal(["id", "set:40", "id"], api.Calls);
            Assert.Equal(0x28, Assert.Single(api.JobWrites).Class);
        }
        else
        {
            Assert.Equal(["id"], api.Calls);
            Assert.Empty(api.JobWrites);
        }
    }

    [Fact]
    public void CreateIdentityFailurePrecedesJobWritesAndReleasesOwnership()
    {
        var api = new RecordingApi { FailAt = "set:15" };
        api.IdStatuses.Enqueue(AccessDenied);
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => WindowsSilo.Create(AllProviders, true, api)).NtStatus);
        Assert.Equal(["create", "id", "close"], api.Calls);
        Assert.Empty(api.JobWrites);

        using var job = new CountingHandle(0x1234);
        api.Calls.Clear();
        api.IdJobs.Clear();
        api.IdStatuses.Enqueue(AccessDenied);
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => WindowsSilo.ConfigureJob(job, AllProviders, true, api)).NtStatus);
        Assert.Equal(["id"], api.Calls);
        Assert.Equal((nint)0x1234, Assert.Single(api.IdJobs));
        Assert.Equal(0, job.Closes);
        job.Dispose();
        Assert.Equal(1, job.Closes);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("shutdown")]
    [InlineData("flush")]
    [InlineData("keys")]
    [InlineData("cleanup")]
    public void IdentityQueryFailurePrecedesTheOperation(string operation)
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, false, api);
        api.Calls.Clear();
        api.IdStatuses.Enqueue(AccessDenied);
        Action call = operation switch
        {
            "launch" => () => silo.LaunchApplication("example.exe").Dispose(),
            "shutdown" => silo.ShutdownApplication,
            "flush" => () => silo.SetRegistryFlushEnabled(true),
            "keys" => () => silo.AddRuntimeVirtualKeys([]),
            "cleanup" => silo.Cleanup,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(call).NtStatus);
        Assert.Equal(["id"], api.Calls);
    }

    [Fact]
    public void ExplicitCleanupStopsAtFailureAndCanBeRetried()
    {
        var api = new RecordingApi { FailAt = "cleanup-registry" };
        using var silo = WindowsSilo.Create(AllProviders, true, api);
        Assert.Throws<InjectedFailure>(silo.Cleanup);
        Assert.Equal(["cleanup-network", "cleanup-registry"], api.Calls.TakeLast(2));
        Assert.DoesNotContain("cleanup-files", api.Calls);
        api.FailAt = null;
        silo.Cleanup();
        silo.Dispose();
        Assert.Equal(["cleanup-network", "cleanup-registry", "cleanup-files", "close"], api.Calls.TakeLast(4));
    }

    [Fact]
    public void DisposeClosesTheOwnedJobWhenTheCleanupIdentityQueryFails()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, false, api);
        api.Calls.Clear();
        api.IdStatuses.Enqueue(AccessDenied);
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(silo.Dispose).NtStatus);
        silo.Dispose();
        Assert.Equal(["id", "close"], api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachCleanupUsesItsOwnVolumeAndKeepsTheJobOpen(bool server)
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, server, api);
        foreach (var volume in new string?[] { null, "", @"A:\", @"B:\" })
        {
            api.Calls.Clear();
            silo.Cleanup(volume);
            var files = volume is null ? "cleanup-files" : $"cleanup-files:{volume}";
            Assert.Equal(["id", "cleanup-network", "cleanup-registry", files], api.Calls);
        }
        Assert.Equal(Guid.Empty, silo.Identifier);
        api.Calls.Clear();
        silo.Dispose();
        Assert.Equal(["close"], api.Calls);
    }

    [Fact]
    public void FailedRepeatAfterSuccessDoesNotTriggerExtraCleanupDuringDispose()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(AllProviders, false, api);
        silo.Cleanup();
        api.FailAt = @"cleanup-files:B:\";
        Assert.Throws<InjectedFailure>(() => silo.Cleanup(@"B:\"));
        api.Calls.Clear();
        silo.Dispose();
        Assert.Equal(["close"], api.Calls);
    }

    [Fact]
    public void CallerSpecifiedWciVolumeIsPassedToCleanupEvenWithoutFilesystemSetup()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(new SiloDescription(), false, api);
        silo.Cleanup(@"X:\");
        Assert.Equal(["cleanup-network", "cleanup-registry", @"cleanup-files:X:\"], api.Calls.TakeLast(3));
    }

    [Theory]
    [InlineData("register", "register,close")]
    [InlineData("files", "files,rollback-files,close")]
    [InlineData("registry", "registry,rollback-files,close")]
    [InlineData("network", "network,cleanup-registry,rollback-files,close")]
    [InlineData("pipes", "pipes,cleanup-network,cleanup-registry,rollback-files,close")]
    [InlineData("validate-image", "validate-image,cleanup-network,cleanup-registry,rollback-files,close")]
    public void FailedCreateUnwindsArmedProvidersInReverseSetupOrder(string failAt, string expectedTail)
    {
        var api = new RecordingApi { FailAt = failAt };
        var error = Assert.Throws<InjectedFailure>(() => WindowsSilo.Create(AllProviders, true, api));
        var expected = expectedTail.Split(',');
        Assert.Equal(expected, api.Calls.TakeLast(expected.Length));
        Assert.Equal(failAt, error.Message);
    }

    [Theory]
    [InlineData(false, null, "mounts")]
    [InlineData(false, "", "mounts")]
    [InlineData(false, "test", "pipes")]
    [InlineData(true, null, "pipes")]
    [InlineData(true, "", "pipes")]
    [InlineData(true, "test", "mounts")]
    public void CreateArmsNetworkRollbackByPresenceAfterSetupOrSkip(bool server, string? network, string failAt)
    {
        var api = new RecordingApi { FailAt = failAt };
        Assert.Throws<InjectedFailure>(() => WindowsSilo.Create(AllProviders with { NetworkCompartment = network }, server, api));
        Assert.Equal(!string.IsNullOrEmpty(network), api.Calls.Contains("network"));
        string[] unwind = network is null
            ? [failAt, "cleanup-registry", "rollback-files", "close"]
            : [failAt, "cleanup-network", "cleanup-registry", "rollback-files", "close"];
        Assert.Equal(unwind, api.Calls.TakeLast(unwind.Length));
    }

    [Fact]
    public void RollbackFailureIsAttachedToTheOriginalFailure()
    {
        var api = new RecordingApi { FailAt = "validate-image", AlsoFailAt = "cleanup-registry" };
        var error = Assert.Throws<InjectedFailure>(() => WindowsSilo.Create(AllProviders, true, api));
        Assert.Equal("validate-image", error.Message);
        Assert.IsType<AggregateException>(error.Data["SiloCleanupFailure"]);
        Assert.Equal(["cleanup-network", "cleanup-registry", "rollback-files", "close"], api.Calls.TakeLast(4));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposeWithoutProviderCleanupReleasesOnlyTheJobReference(bool borrowed)
    {
        var api = new RecordingApi();
        using var caller = new CountingHandle(0x1234);
        using var silo = borrowed
            ? WindowsSilo.ConfigureJob(caller, new SiloDescription(), false, api)
            : WindowsSilo.Create(new SiloDescription(), false, api);
        api.Calls.Clear();

        silo.DisposeWithoutProviderCleanup();
        silo.DisposeWithoutProviderCleanup();
        silo.Dispose();

        Assert.Throws<ObjectDisposedException>(() => silo.Identifier);
        Assert.Equal(borrowed ? Array.Empty<string>() : ["close"], api.Calls);
        Assert.Equal(0, caller.Closes);
        caller.Dispose();
        Assert.Equal(1, caller.Closes);
    }

    [Fact]
    public void BorrowedJobIsKeptAliveWithoutTransferringOwnership()
    {
        var api = new RecordingApi();
        using var caller = new CountingHandle(0x1234);
        using var silo = WindowsSilo.ConfigureJob(caller, new SiloDescription(), false, api);
        caller.Dispose();
        Assert.Equal(0, caller.Closes);
        silo.Dispose();
        Assert.Equal(1, caller.Closes);
        Assert.DoesNotContain("create", api.Calls);
        Assert.DoesNotContain("close", api.Calls);
        Assert.All(api.JobWrites, call => Assert.Equal(0x1234, call.Job));
    }

    [Fact]
    public void FailedCreateReleasesTheBorrowedReferenceOnly()
    {
        var api = new RecordingApi { FailAt = "register" };
        using var job = new CountingHandle(0x1234);
        Assert.Throws<InjectedFailure>(() => WindowsSilo.ConfigureJob(job, new SiloDescription(), true, api));
        Assert.Equal(0, job.Closes);
        Assert.DoesNotContain("close", api.Calls);
        job.Dispose();
        Assert.Equal(1, job.Closes);
    }

    [Fact]
    public void QuiescenceAndShutdownPreserveSpecialStatuses()
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(new SiloDescription(), false, api);
        Assert.True(silo.IsQuiescent);
        api.QueryStatus = 1;
        api.AssignedProcesses = 1;
        Assert.False(silo.IsQuiescent);
        api.AssignedProcesses = 0;
        Assert.True(silo.IsQuiescent);
        api.QueryStatus = unchecked((int)0x80000005);
        Assert.False(silo.IsQuiescent);
        api.QueryStatus = unchecked((int)0xc000000d);
        Assert.Equal(api.QueryStatus, Assert.Throws<SiloException>(() => silo.IsQuiescent).NtStatus);
        api.ShutdownStatus = 0xff;
        Assert.Equal(unchecked((int)0x800704d5), Assert.Throws<SiloException>(silo.ShutdownApplication).HResult);
        api.ShutdownStatus = 1;
        silo.ShutdownApplication();
        api.ShutdownStatus = AccessDenied;
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(silo.ShutdownApplication).NtStatus);
        Assert.Equal(["id", "set:41"], api.Calls.TakeLast(2));
        Assert.Equal("02000000", Convert.ToHexString(api.JobWrites[^1].Bytes));
    }

    [Theory]
    [InlineData(0u, 5000u, "0500000088130000", null)]
    [InlineData(3u, 0u, "0300000003000000", 0ul)]
    [InlineData(0u, 0u, "0000000000000000", 0ul)]
    public void JobLimitWritesDistinguishAbsentMemoryAndPreserveCpuOptions(uint weight, uint max, string cpu, ulong? memory)
    {
        var api = new RecordingApi();
        using var silo = WindowsSilo.Create(new SiloDescription { Job = new JobConfiguration { Cpu = new CpuConfiguration(weight, max), MemoryLimit = memory } }, false, api);
        Assert.Equal(cpu, Convert.ToHexString(api.JobWrites[0].Bytes));
        int[] expectedClasses = memory is null ? [15, 9, 0x23] : [15, 9, 9, 0x23];
        Assert.Equal(expectedClasses, api.JobWrites.Select(write => write.Class));
        if (memory is not null)
        {
            var memoryPayload = new byte[0x98];
            BinaryPrimitives.WriteUInt32LittleEndian(memoryPayload.AsSpan(0x10), 0x200000u);
            Assert.Equal(memoryPayload, api.JobWrites[1].Bytes);
        }
        var lifecyclePayload = new byte[0x98];
        BinaryPrimitives.WriteUInt32LittleEndian(lifecyclePayload.AsSpan(0x10), 0x400000u);
        Assert.Equal(lifecyclePayload, api.JobWrites[^2].Bytes);
    }

    [Fact]
    public async Task BorrowedJobSurvivesCallerDisposalWhileBootIsInProgress()
    {
        using var booted = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var api = new RecordingApi
        {
            DuringBoot = () =>
            {
                booted.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        using var caller = new CountingHandle(0x1234);
        using var silo = WindowsSilo.ConfigureJob(caller, new SiloDescription(), false, api);
        using var deleteEvent = new SafeWaitHandle(0x3456, ownsHandle: false);
        var boot = Task.Run(() => silo.BootServer(deleteEvent));
        try
        {
            Assert.True(booted.Wait(TimeSpan.FromSeconds(10)));
            caller.Dispose();
            Assert.Equal(0, caller.Closes);
        }
        finally { resume.Set(); }
        await boot;
        Assert.All(api.IdJobs, job => Assert.Equal(0x1234, job));
        silo.Dispose();
        Assert.Equal(1, caller.Closes);
    }

    private sealed class CountingHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal int Closes { get; private set; }

        internal CountingHandle(nint handle) : base(true) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            Closes++;
            return true;
        }
    }

    private sealed class InjectedFailure(string operation) : Exception(operation);

    internal class RecordingApi : ISiloApi, IFileSystemRegistration
    {
        internal const nint CreatedJob = 0x720;
        internal List<string> Calls { get; } = [];
        internal List<(nint Job, int Class, byte[] Bytes)> JobWrites { get; } = [];
        internal string? FailAt { get; set; }
        internal string? AlsoFailAt { get; init; }
        internal Action? DuringBoot { get; init; }
        internal Queue<int> IdStatuses { get; } = new Queue<int>();
        internal List<nint> IdJobs { get; } = [];
        internal int QueryStatus { get; set; }
        internal uint AssignedProcesses { get; set; }
        internal int ShutdownStatus { get; set; }

        private void Record(string operation)
        {
            Calls.Add(operation);
            if (operation == FailAt || operation == AlsoFailAt)
                throw new InjectedFailure(operation);
        }

        public nint CreateJob() { Record("create"); return CreatedJob; }
        public void Close(nint handle) => Record("close");
        public int SetJob(nint job, int informationClass, Span<byte> data)
        {
            Record($"set:{informationClass}");
            JobWrites.Add((job, informationClass, data.ToArray()));
            var status = informationClass == 0x29 ? ShutdownStatus : 0;
            SiloException.CheckNt(status, "SetJob");
            return status;
        }

        public virtual int QueryJob(nint job, int informationClass, Span<byte> data)
        {
            Assert.Equal(3, informationClass);
            Assert.Equal("00000000010000000000000000000000", Convert.ToHexString(data));
            BinaryPrimitives.WriteUInt32LittleEndian(data, AssignedProcesses);
            BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 0);
            return QueryStatus;
        }

        public void RegisterServer(nint job) => Record("register");
        public void SetSystemRoot(nint job, string root) => Record("system-root");
        public void AssignProcess(nint job, nint process) => Record($"assign:{process}");
        public void Boot(nint job)
        {
            Record("boot");
            DuringBoot?.Invoke();
        }
        public void SetupObjects(nint job, ObjectNamespaceConfiguration configuration) => Record("objects");
        public IFileSystemRegistration CreateFileSystemRegistration(nint job) => this;
        public void Setup(FileSystemConfiguration configuration) => Record("files");
        public void Rollback() => Record("rollback-files");
        public void CleanupFileSystem(nint job, string? volumePath) => Record(volumePath is null ? "cleanup-files" : $"cleanup-files:{volumePath}");
        public void SetupRegistry(nint job, RegistryConfiguration configuration) => Record("registry");
        public void SetupNetwork(nint job, string name) => Record("network");
        public void SetupMounts(nint job, IReadOnlyList<DriveMapping> mounts) => Record("mounts");
        public void SetupPipes(nint job, IReadOnlyList<PipeMapping> pipes) => Record("pipes");
        public void CleanupNetwork(nint job) => Record("cleanup-network");
        public void CleanupRegistry(nint job) => Record("cleanup-registry");
#if SILO2025
        public virtual uint ValidateBootImage(nint job) => 26100;
#endif
        public void ConfigureServerImage(nint job, DownlevelConfiguration? configuration) => Record("validate-image");
        public Guid GetId(nint job)
        {
            IdJobs.Add(job);
            Record("id");
            if (IdStatuses.TryDequeue(out var status))
                SiloException.CheckNt(status, "QueryContainerId");
            return Guid.Empty;
        }
        public string GetObjectRoot(nint job) => throw new NotSupportedException();
        public string GetRegistryRoot(nint job) => throw new NotSupportedException();
        public nint GetRegistryHandle(nint job, uint type) => throw new NotSupportedException();
        public nint Launch(nint job, string command, uint flags) => throw new NotSupportedException();
        public void ModifyRegistryFlags(nint job, uint add, uint remove) => throw new NotSupportedException();
        public void AddRuntimeVirtualKeys(nint job, IReadOnlyList<RuntimeVirtualKey> keys) => throw new NotSupportedException();
        public void Terminate(nint job, int status) => throw new NotSupportedException();
        public SiloTerminationNotification RegisterForTerminationNotification(nint job) => throw new NotSupportedException();
    }
}
