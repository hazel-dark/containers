using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Containers.Windows.Silos;

/// <summary>Owns a threadpool job notification registration and a duplicate of its silo job handle.</summary>
/// <remarks>The subscription can outlive its controller. Dispose the subscription when last-handle
/// job termination is required. Each reader competes for delivered reasons, and cancellation ends
/// that reader's enumeration.</remarks>
[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
public sealed unsafe class SiloTerminationNotification : IDisposable
{
    private readonly Lock _gate = new Lock();
    private readonly CallbackState _state;
    private readonly Registration _registration;

    internal SiloTerminationNotification(JobSafeHandle job, IJobNotificationApi api, JobNotificationRegistrations registrations)
    {
        _state = new CallbackState(api);
        _registration = new Registration(_state, registrations);
        _registration.Attach(job);
    }

    /// <summary>Receives the reasons dispatched by the native callback until the subscription is detached.</summary>
    public IAsyncEnumerable<SiloTerminationReason> ReadAsync(CancellationToken cancellationToken = default) =>
        _state.Reasons.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Detaches the completion port association and waits for currently queued callbacks.</summary>
    /// <remarks>After the wait a new subscription may attach. Dispose still releases this subscription's
    /// native registration and duplicate job handle.</remarks>
    public void WaitForCallbacks()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_registration.IsClosed, this);

            _registration.Drain();
            GC.KeepAlive(_registration);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _registration.Dispose();
        }
    }

    internal static SiloTerminationReason? Classify(bool server, nuint completionCode, int status)
    {
        if (!server)
            return SiloTerminationReason.NormalExit;

        if (completionCode == PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO)
            return null;

        return status switch
        {
            _ when status == NTSTATUS.STATUS_SYSTEM_SHUTDOWN => SiloTerminationReason.NormalExit,
            _ when status == NTSTATUS.STATUS_DEVICE_POWER_CYCLE_REQUIRED => SiloTerminationReason.Reboot,
            _ => SiloPlatform.OtherServerTerminationReason(status),
        };
    }

    private sealed class CallbackState
    {
        internal JobSafeHandle Job = null!;
        internal readonly IJobNotificationApi Api;
        internal readonly Channel<SiloTerminationReason> Reasons = Channel.CreateUnbounded<SiloTerminationReason>();

        public CallbackState(IJobNotificationApi api)
        {
            Api = api;
        }
    }

    private sealed class Registration : SafeHandle
    {
        private readonly CallbackState _state;
        private readonly JobNotificationRegistrations _registrations;
        private GCHandle _context;
        private bool _detached;

        internal Registration(CallbackState state, JobNotificationRegistrations registrations) : base(0, ownsHandle: true)
        {
            _state = state;
            _registrations = registrations;
        }

        internal void Attach(JobSafeHandle job)
        {
            var api = _state.Api;
            _ = api.GetId(job);
            _state.Job = _registrations.Reserve(job, api);

            try
            {
                var completionFilter = (1u << (int)PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO) | (1u << (int)PInvoke.JOB_OBJECT_MSG_SILO_TERMINATED);
                api.SetJob(_state.Job, (int)JOBOBJECTINFOCLASS.JobObjectCompletionFilter, completionFilter.AsBytes());
                _context = GCHandle.Alloc(_state);

                var status = api.AllocateJobNotification(out var notification, _state.Job, &OnNativeNotification, GCHandle.ToIntPtr(_context), 0);
                if (status == NTSTATUS.STATUS_INVALID_PARAMETER)
                {
                    JOBOBJECT_ASSOCIATE_COMPLETION_PORT port = default;
                    api.SetJob(_state.Job, (int)JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, port.AsBytes());
                    status = api.AllocateJobNotification(out notification, _state.Job, &OnNativeNotification, GCHandle.ToIntPtr(_context), 0);
                }

                SiloException.CheckNt(status, "TpAllocJobNotification");
                SetHandle(notification);
            }
            catch
            {
                if (_context.IsAllocated)
                    _context.Free();

                _registrations.Release(_state.Job);
                _state.Job.Dispose();
                throw;
            }
        }

        public override bool IsInvalid => handle == 0;

        internal void Drain()
        {
            if (_detached)
                return;

            _state.Api.WaitForJobNotification(handle);
            _detached = true;
            // TpWait clears the TP object's saved job and drains callbacks. Its later
            // TpRelease cannot clear the association of a new managed registration.
            _registrations.Release(_state.Job);
            _state.Reasons.Writer.TryComplete();
        }

        protected override bool ReleaseHandle()
        {
            // On 20348, TpWait first disassociates the job then drains callbacks.
            // Wait before Release to retain the managed callback context through the drain.
            Drain();
            _state.Api.ReleaseJobNotification(handle);
            _context.Free();
            _state.Job.Dispose();
            return true;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnNativeNotification(nint instance, nint context, nint notification, nuint completionCode, nint completionValue, int completionStatus)
    {
        var state = (CallbackState)GCHandle.FromIntPtr(context).Target!;
        try
        {
            _ = state.Api.GetId(state.Job);
            if (SiloPlatform.GetNotificationReason(state.Api, state.Job, completionCode, unchecked((int)completionValue)) is { } reason)
                state.Reasons.Writer.TryWrite(reason);
        }
        catch (Exception error)
        {
            state.Reasons.Writer.TryComplete(error);
        }
    }
}

// Track duplicate jobs without rooting the subscriptions that drain and release them.
[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
internal sealed class JobNotificationRegistrations
{
    private readonly List<JobSafeHandle> jobs = [];

    internal JobSafeHandle Reserve(JobSafeHandle job, IJobNotificationApi api)
    {
        lock (jobs)
        {
            foreach (var attached in jobs)
            {
                if (api.CompareJobs(job, attached))
                    throw new InvalidOperationException("The job already has a managed termination notification registration.");
            }

            jobs.EnsureCapacity(jobs.Count + 1);

            var duplicate = api.DuplicateJob(job);
            jobs.Add(duplicate);
            return duplicate;
        }
    }

    internal void Release(JobSafeHandle job)
    {
        lock (jobs)
            jobs.Remove(job);
    }
}
