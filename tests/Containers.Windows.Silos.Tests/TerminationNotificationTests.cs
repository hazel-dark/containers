using System.Collections.Concurrent;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class TerminationNotificationTests
{
    private const int Job = 0x4321;
    private const int Alias = 0x4322;
    private const int OtherJob = 0x4323;
    private const int AccessDenied = unchecked((int)0xc0000022);
    private const int InvalidParameter = unchecked((int)0xc000000d);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WaitDrainsCallbacksBeforeReleaseAndUsesTheFifthNativeArgument()
    {
        var api = new RecordingNotifications();
        using var registration = api.Register(Job);
        var notification = api.AttachedTo(Job);
        api.Send(notification, 4, 0, 0);
        api.Send(notification, 1, unchecked((nint)0x0000ffffc00002eb), unchecked((int)0x80000031));
        api.Send(notification, unchecked((nuint)0x100000004), 0, unchecked((int)0x80000031));
        api.DuringWait = () => api.Send(notification, 1, unchecked((nint)0x0000ffff80000031), unchecked((int)0xc00002eb));
        registration.WaitForCallbacks();
        registration.WaitForCallbacks();
        registration.Dispose();

        Assert.Equal([SiloTerminationReason.NormalExit, SiloTerminationReason.UnexpectedExit, SiloTerminationReason.Reboot], await ReadAll(registration));
        Assert.Equal(["id", "duplicate", "set:16", "allocate", "id", "mode", "id", "mode", "id", "mode",
            "wait", "id", "mode", "release", "close"], api.Calls);
        Assert.Equal(Job, api.Queries.First());
        Assert.All(api.Queries.Skip(1), alias => Assert.NotEqual(Job, alias));
    }

    [Fact]
    public async Task NonServerDispatchIncludesCodeFour()
    {
        var api = new RecordingNotifications { Server = false };
        using var registration = api.Register(Job);
        var notification = api.AttachedTo(Job);
        api.Send(notification, 4, unchecked((nint)0x80000031), 0);
        registration.WaitForCallbacks();
        Assert.Equal([SiloTerminationReason.NormalExit], await ReadAll(registration));
    }

    [Fact]
    public void RegistrationIdentityFailurePrecedesTheDuplicateAndAnyWrite()
    {
        var api = new RecordingNotifications();
        api.IdentityStatus = AccessDenied;
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => api.Register(Job)).NtStatus);
        Assert.Equal(["id"], api.Calls);
    }

    [Fact]
    public async Task CallbackIdentityFailureStopsModeQueryAndCompletesTheReader()
    {
        var api = new RecordingNotifications();
        using var registration = api.Register(Job);
        api.IdentityStatus = AccessDenied;
        api.Send(api.AttachedTo(Job), 1, 0, 0);

        var error = await Assert.ThrowsAsync<SiloException>(() => ReadAll(registration));
        Assert.Equal(AccessDenied, error.NtStatus);
        Assert.DoesNotContain("mode", api.Calls);
        Assert.DoesNotContain("wait", api.Calls);
    }

#if SILO2025
    [Fact]
    public async Task LaterCallbackDeliversReason3AfterAFailedModeQuery()
    {
        var api = new RecordingNotifications();
        using var registration = api.Register(Job);
        var packet = api.AttachedTo(Job);
        api.ModeStatus = AccessDenied;
        api.Send(packet, 13, 0, 0);
        api.ModeStatus = 0;
        api.Send(packet, 13, unchecked((nint)0xc000021a), 0);
        registration.WaitForCallbacks();
        Assert.Equal([SiloTerminationReason.Terminated], await ReadAll(registration));
    }
#endif

    [Fact]
    public async Task CancelingAReaderLeavesTheJobAttachedForOtherReaders()
    {
        var api = new RecordingNotifications();
        using var registration = api.Register(Job);
        using var cancellation = new CancellationTokenSource();
        await using var canceled = registration.ReadAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = canceled.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain("wait", api.Calls);

        api.Send(api.AttachedTo(Job), 1, 0, 0);
        registration.WaitForCallbacks();
        Assert.Equal([SiloTerminationReason.UnexpectedExit], await ReadAll(registration));
    }

    [Fact]
    public void InvalidParameterClearsExternalCompletionPortAndRetriesOnce()
    {
        var api = new RecordingNotifications(InvalidParameter, 0);
        using (api.Register(Job)) { }
        Assert.Equal(["id", "duplicate", "set:16", "allocate", "set:7", "allocate", "wait", "release", "close"], api.Calls);
    }

    [Theory]
    [InlineData("duplicate", "id,duplicate")]
    [InlineData("set:16", "id,duplicate,set:16,close")]
    [InlineData("allocate", "id,duplicate,set:16,allocate,close")]
    [InlineData("set:7", "id,duplicate,set:16,allocate,set:7,close")]
    [InlineData("retry", "id,duplicate,set:16,allocate,set:7,allocate,close")]
    public void FailedRegistrationUnwindsOwnedResourcesAndAllowsRetry(string failure, string expected)
    {
        var api = failure switch
        {
            "set:7" => new RecordingNotifications(InvalidParameter) { FailAt = "set:7" },
            "retry" => new RecordingNotifications(InvalidParameter, AccessDenied),
            "allocate" => new RecordingNotifications(AccessDenied),
            _ => new RecordingNotifications { FailAt = failure }
        };
        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => api.Register(Job)).NtStatus);
        Assert.Equal(expected.Split(','), api.Calls);

        api.FailAt = null;
        using var retry = api.Register(Alias);
        Assert.NotEqual(0, api.AttachedTo(Job));
    }

    [Fact]
    public async Task AliasCannotReplaceActiveRegistrationOrClearItsPort()
    {
        var api = new RecordingNotifications();
        using var first = api.Register(Job);
        var notification = api.AttachedTo(Job);
        using var other = api.Register(OtherJob);
        Assert.Throws<InvalidOperationException>(() => api.Register(Alias));

        Assert.Equal(notification, api.AttachedTo(Alias));
        Assert.Equal(2, api.Calls.Count(call => call == "allocate"));
        Assert.DoesNotContain("set:7", api.Calls);
        api.Send(notification, 1, 0, 0);
        first.WaitForCallbacks();
        Assert.Equal([SiloTerminationReason.UnexpectedExit], await ReadAll(first));
    }

    [Fact]
    public void FailedReplacementAfterDrainDoesNotLeakOwnershipAndOldReleaseLeavesSuccessorAttached()
    {
        var api = new RecordingNotifications(0, InvalidParameter, AccessDenied, 0);
        using var first = api.Register(Job);
        first.WaitForCallbacks();

        Assert.Equal(AccessDenied, Assert.Throws<SiloException>(() => api.Register(Alias)).NtStatus);
        Assert.Equal(["set:16", "allocate", "set:7", "allocate", "close"], api.Calls.TakeLast(5));
        Assert.DoesNotContain("release", api.Calls);

        using var successor = api.Register(Alias);
        var successorHandle = api.AttachedTo(Job);
        first.Dispose();
        Assert.Equal(successorHandle, api.AttachedTo(Job));
        Assert.Equal(1, api.Calls.Count(call => call == "wait"));
        Assert.Equal(1, api.Calls.Count(call => call == "release"));
    }

    [Fact]
    public async Task CompetingAliasCannotAttachDuringNativeAllocation()
    {
        using var allocating = new ManualResetEventSlim();
        using var resumeAllocation = new ManualResetEventSlim();
        var api = new RecordingNotifications
        {
            DuringAllocation = () =>
            {
                allocating.Set();
                Assert.True(resumeAllocation.Wait(Timeout));
            }
        };

        var attaching = Task.Run(() => api.Register(Job));
        try
        {
            Assert.True(allocating.Wait(Timeout));
            var overlap = await Task.Run(() => Record.Exception(() => api.Register(Alias))).WaitAsync(Timeout);
            Assert.IsType<InvalidOperationException>(overlap);
            Assert.Equal(1, api.Calls.Count(call => call == "allocate"));
        }
        finally
        {
            resumeAllocation.Set();
            (await attaching.WaitAsync(Timeout)).Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCallbackDrainSerializesWaitDisposeAndRejectsAliasUntilDrainFinishes(bool disposeFirst)
    {
        using var drainEntered = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var resumeCallback = new ManualResetEventSlim();
        using var callbackDone = new ManualResetEventSlim();
        using var contenderEntered = new ManualResetEventSlim();
        var api = new RecordingNotifications();
        using var first = api.Register(Job);
        var notification = api.AttachedTo(Job);
        api.DuringWait = () =>
        {
            drainEntered.Set();
            Assert.True(callbackDone.Wait(Timeout));
        };
        api.DuringIdentity = job =>
        {
            if (job == Job || job == Alias)
                return;
            callbackEntered.Set();
            Assert.True(resumeCallback.Wait(Timeout));
        };

        var drain = RunConcurrently(disposeFirst ? first.Dispose : first.WaitForCallbacks);
        var callback = Task.CompletedTask;
        Task? contender = null;
        Exception? contenderFailure = null;
        try
        {
            Assert.True(drainEntered.Wait(Timeout));
            callback = RunConcurrently(() =>
            {
                try { api.Send(notification, 1, unchecked((nint)0x80000031), 0); }
                finally { callbackDone.Set(); }
            });
            Assert.True(callbackEntered.Wait(Timeout));
            contender = RunConcurrently(() =>
            {
                contenderEntered.Set();
                Action competingCall = disposeFirst ? first.WaitForCallbacks : first.Dispose;
                contenderFailure = Record.Exception(competingCall);
            });
            Assert.True(contenderEntered.Wait(Timeout));

            var overlap = await Task.Run(() => Record.Exception(() => api.Register(Alias))).WaitAsync(Timeout);
            Assert.IsType<InvalidOperationException>(overlap);
            Assert.False(drain.IsCompleted);
            Assert.False(contender.IsCompleted);
            Assert.DoesNotContain("release", api.Calls);
            Assert.DoesNotContain("close", api.Calls);
            Assert.DoesNotContain("set:7", api.Calls);
        }
        finally
        {
            resumeCallback.Set();
            await callback.WaitAsync(Timeout);
            callbackDone.Set();
            await drain.WaitAsync(Timeout);
            if (contender is not null)
                await contender.WaitAsync(Timeout);
        }

        if (disposeFirst)
            Assert.IsType<ObjectDisposedException>(contenderFailure);
        else
            Assert.Null(contenderFailure);
        Assert.Equal([SiloTerminationReason.Reboot], await ReadAll(first));
        Assert.Equal(["mode", "release", "close"], api.Calls.TakeLast(3));
        Assert.Equal(1, api.Calls.Count(call => call == "wait"));
        Assert.Equal(1, api.Calls.Count(call => call == "release"));
        Assert.Equal(1, api.Calls.Count(call => call == "close"));

        api.DuringWait = null;
        using var successor = api.Register(Alias);
        Assert.NotEqual(notification, api.AttachedTo(Job));
    }

    private static Task RunConcurrently(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task<List<SiloTerminationReason>> ReadAll(SiloTerminationNotification registration)
    {
        using var timeLimit = new CancellationTokenSource(Timeout);
        List<SiloTerminationReason> reasons = [];
        await foreach (var reason in registration.ReadAsync(timeLimit.Token))
            reasons.Add(reason);
        return reasons;
    }

    private sealed unsafe class RecordingNotifications(params int[] statuses) : IJobNotificationApi
    {
        private readonly JobNotificationRegistrations registrations = new JobNotificationRegistrations();
        private readonly ConcurrentQueue<int> allocationStatuses = new ConcurrentQueue<int>(statuses);
        private readonly ConcurrentDictionary<nint, nint> duplicates = new ConcurrentDictionary<IntPtr, IntPtr>();
        private readonly ConcurrentDictionary<nint, nint> ports = new ConcurrentDictionary<IntPtr, IntPtr>();
        private readonly ConcurrentDictionary<nint, Packet> notifications = new ConcurrentDictionary<IntPtr, Packet>();
        private int nextDuplicate = 0x5000;
        private int nextNotification = 0x6000;

        internal ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();
        internal ConcurrentQueue<nint> Queries { get; } = new ConcurrentQueue<IntPtr>();
        internal Action? DuringWait { get; set; }
        internal Action? DuringAllocation { get; init; }
        internal Action<nint>? DuringIdentity { get; set; }
        internal string? FailAt { get; set; }
        internal int ModeStatus { get; set; }
        internal int IdentityStatus { get; set; }
        internal bool Server { get; init; } = true;

        internal SiloTerminationNotification Register(nint job)
        {
            using var handle = new JobSafeHandle(job, ownsHandle: false);
            return new SiloTerminationNotification(handle, this, registrations);
        }
        internal nint AttachedTo(nint job) => ports[ObjectFor(job)];

        private nint ObjectFor(nint job) => job switch
        {
            Job or Alias => Job,
            OtherJob => OtherJob,
            _ => duplicates[job]
        };

        private void Record(string call)
        {
            Calls.Enqueue(call);
            if (call == FailAt)
                SiloException.CheckNt(AccessDenied, call);
        }

        public Guid GetId(JobSafeHandle job)
        {
            Queries.Enqueue(job.DangerousGetHandle());
            Record("id");
            DuringIdentity?.Invoke(job.DangerousGetHandle());
            SiloException.CheckNt(IdentityStatus, "QueryContainerId");
            return Guid.Empty;
        }

        public bool CompareJobs(JobSafeHandle first, JobSafeHandle second) => ObjectFor(first.DangerousGetHandle()) == ObjectFor(second.DangerousGetHandle());

        public JobSafeHandle DuplicateJob(JobSafeHandle job)
        {
            Record("duplicate");
            nint duplicate = Interlocked.Increment(ref nextDuplicate);
            Assert.True(duplicates.TryAdd(duplicate, ObjectFor(job.DangerousGetHandle())));
            return new RecordingJobHandle(duplicate, Close);
        }

        public int SetJob(JobSafeHandle job, int informationClass, Span<byte> data)
        {
            Assert.True(duplicates.ContainsKey(job.DangerousGetHandle()));
            if (informationClass == 0x10)
                Assert.Equal("10200000", Convert.ToHexString(data));
            else
            {
                Assert.Equal(7, informationClass);
                Assert.Equal(new byte[16], data.ToArray());
            }
            Record($"set:{informationClass}");
            if (informationClass == 7)
                ports.TryRemove(ObjectFor(job.DangerousGetHandle()), out _);
            return 0;
        }

        public int AllocateJobNotification(out nint notification, JobSafeHandle job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context, nint environment)
        {
            Assert.Equal(0, environment);
            Record("allocate");
            DuringAllocation?.Invoke();
            allocationStatuses.TryDequeue(out var status);
            notification = 0;
            if (status >= 0)
            {
                var result = (nint)Interlocked.Increment(ref nextNotification);
                if (!ports.TryAdd(ObjectFor(job.DangerousGetHandle()), result))
                    return InvalidParameter;
                Assert.True(notifications.TryAdd(result, new Packet(job.DangerousGetHandle(), callback, context)));
                notification = result;
            }
            return status;
        }

        public void WaitForJobNotification(nint notification)
        {
            Record("wait");
            Detach(notifications[notification]);
            DuringWait?.Invoke();
        }

        public void ReleaseJobNotification(nint notification)
        {
            Record("release");
            Assert.True(notifications.TryRemove(notification, out var packet));
            Detach(packet);
        }

        private void Detach(Packet packet)
        {
            // Each native registration detaches the job globally until its own
            // saved job is cleared, even if someone replaced its association.
            if (packet.Job != 0)
            {
                ports.TryRemove(ObjectFor(packet.Job), out _);
                packet.Job = 0;
            }
        }

        public bool IsServer(JobSafeHandle job)
        {
            Assert.True(duplicates.ContainsKey(job.DangerousGetHandle()));
            Record("mode");
            SiloException.CheckNt(ModeStatus, "Query server mode");
            return Server;
        }

        private void Close(nint handle)
        {
            Assert.True(duplicates.TryRemove(handle, out _));
            Record("close");
        }

        internal void Send(nint notification, nuint code, nint value, int status)
        {
            var packet = notifications[notification];
            packet.Callback(0, packet.Context, notification, code, value, status);
        }

        private sealed class Packet(nint job, delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> callback, nint context)
        {
            internal nint Job { get; set; } = job;
            internal readonly delegate* unmanaged<nint, nint, nint, nuint, nint, int, void> Callback = callback;
            internal readonly nint Context = context;
        }
    }
}
