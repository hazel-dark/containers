using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

/// <summary>Controls a Windows silo and holds a reference to its job for the controller lifetime.</summary>
[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
public sealed partial class WindowsSilo : IDisposable
{
    private readonly Lock _gate = new Lock();
    private readonly ISiloApi _api;
    private readonly JobSafeHandle _jobLease;
    private IFileSystemRegistration? _fileSystemRollback;
    private bool _networkCleanupArmed;
    private bool _registry;
    private bool _hasCompletedCleanup;
    private bool _providersConfigured;

    private WindowsSilo(ISiloApi api, JobSafeHandle jobLease)
    {
        _api = api;
        _jobLease = jobLease;
    }

    /// <summary>Creates and configures a silo. Server creation registers the job with DAM or SMSS.
    /// Call <see cref="BootServer"/> with a deletion event to initialize and boot its OS.</summary>
    public static WindowsSilo Create(SiloDescription description, bool server = false)
    {
        RequireWindows();
        return Create(description, server, NativeApi.Instance);
    }

    /// <summary>Configures a caller-supplied Windows job as a silo. The controller holds a reference
    /// for its lifetime. Disposing the controller releases that reference and the caller retains
    /// ownership of the job handle.</summary>
    public static WindowsSilo ConfigureJob(JobSafeHandle job, SiloDescription description, bool server = false)
    {
        RequireWindows();
        return ConfigureJob(job, description, server, NativeApi.Instance);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, SiloPlatform.MinimumBuild) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"This library requires Windows build {SiloPlatform.MinimumBuild} or later and an x64 process.");
    }

    internal static WindowsSilo Create(SiloDescription description, bool server, ISiloApi api)
    {
        ArgumentNullException.ThrowIfNull(description);
        return ConfigureCore(description, server, api, api.CreateJob());
    }

    internal static WindowsSilo ConfigureJob(JobSafeHandle job, SiloDescription description, bool server, ISiloApi api)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(description);
        return ConfigureCore(description, server, api, JobSafeHandle.Borrow(job));
    }

    private static WindowsSilo ConfigureCore(SiloDescription description, bool server, ISiloApi api, JobSafeHandle jobLease)
    {
        WindowsSilo? silo = null;
        try
        {
            silo = new WindowsSilo(api, jobLease);
            silo.Configure(description, server);
            return silo;
        }
        catch (Exception setupFailure)
        {
            try
            {
                if (silo is null)
                    jobLease.Dispose();
                else
                    silo.RollbackCreate();
            }
            catch (Exception cleanupFailure)
            {
                setupFailure.Data["SiloCleanupFailure"] = cleanupFailure;
            }

            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_jobLease.IsClosed)
                return;

            try
            {
                if (!_hasCompletedCleanup)
                    Cleanup();
            }
            finally
            {
                _jobLease.Dispose();
            }
        }
    }

    /// <summary>Releases this controller's job reference without provider cleanup.</summary>
    /// <remarks>Only use when the caller owns the job lifetime and has not configured
    /// providers requiring explicit cleanup. In particular, a live job retains its
    /// Bind Filter mappings until the job is destroyed.</remarks>
    public void DisposeWithoutProviderCleanup()
    {
        lock (_gate)
        {
            if (_jobLease.IsClosed)
                return;

            _hasCompletedCleanup = true;
            _jobLease.Dispose();
        }
    }

    private void Configure(SiloDescription description, bool server)
    {

        _ = _api.GetId(_jobLease);
        if (description.Job?.Cpu is { } cpu)
        {
            JOBOBJECT_CPU_RATE_CONTROL_INFORMATION rate = default;
            if (cpu.MaximumRate != 0)
            {
                rate.ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP;
                rate.CpuRate = cpu.MaximumRate;
            }

            if (cpu.Weight != 0)
            {
                rate.ControlFlags |= JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_WEIGHT_BASED;
                rate.Weight = cpu.Weight;
            }

            _api.SetJob(_jobLease, (int)JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation, rate.AsBytes());
        }

        if (description.Job?.MemoryLimit is { } limit)
        {
            ExtendedLimitInformationV2 memory = default;
            memory.Limits.BasicLimitInformation.LimitFlags = NativeJobConstants.WcJobTotalMemoryLimit;
            memory.JobTotalMemoryLimit = checked((nuint)limit);
            _api.SetJob(_jobLease, (int)JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, memory.AsBytes());
        }

        ExtendedLimitInformationV2 extended = default;
        extended.Limits.BasicLimitInformation.LimitFlags = description.Job?.KillOnLastHandleClose == true
            ? JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            : NativeJobConstants.JobObjectLimitSiloReady;

        _api.SetJob(_jobLease, (int)JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, extended.AsBytes());
        _api.SetJob(_jobLease, (int)JOBOBJECTINFOCLASS.JobObjectCreateSilo, []);

        if (description.Job?.SystemRoot is { } root)
            _api.SetSystemRoot(_jobLease, root);

        if (description.Job?.EnableThreadImpersonation == true)
            _api.SetJob(_jobLease, NativeJobConstants.JobObjectThreadImpersonationInformation, [1]);

        if (server)
            _api.RegisterServer(_jobLease);
        else if (description.ObjectNamespace is not null)
            _api.AssignProcess(_jobLease, NativeJobConstants.WcObjectNamespaceAssignmentProcess);

        if (description.ObjectNamespace is { } objects)
            _api.SetupObjects(_jobLease, objects);

        if (description.FileSystem is { } files)
        {
            _fileSystemRollback = _api.CreateFileSystemRegistration(_jobLease);
            _fileSystemRollback.Setup(files);
        }

        if (description.Registry is { } keys)
        {
            _api.SetupRegistry(_jobLease, keys);
            _registry = true;
        }

        if (description.NetworkCompartment is { } network)
        {
            if (network.Length != 0)
                _api.SetupNetwork(_jobLease, network);

            _networkCleanupArmed = true;
        }

        if (description.Mounts is { } mounts)
            _api.SetupMounts(_jobLease, mounts);

        if (description.NamedPipes is { } pipes)
            _api.SetupPipes(_jobLease, pipes);

        _providersConfigured = true;
        if (server)
            _api.ConfigureServerImage(_jobLease, description.Downlevel);

        _fileSystemRollback = null;
    }

    /// <summary>Sets the server-silo delete event and asks SMSS to boot the registered container.</summary>
    public void BootServer(SafeWaitHandle deleteEvent)
    {
        ArgumentNullException.ThrowIfNull(deleteEvent);
        var borrowed = false;
        try
        {
            deleteEvent.DangerousAddRef(ref borrowed);
            if (deleteEvent.IsInvalid)
                throw new ArgumentException("A valid delete event handle is required.", nameof(deleteEvent));

            Use(handle =>
            {
                _ = _api.GetId(handle);

                SetServerInitialize(handle, deleteEvent.DangerousGetHandle());

                // Wc opens a separate activity for the SMSS call after initialization.
                _ = _api.GetId(handle);
                _api.Boot(handle);
            });
        }
        finally
        {
            if (borrowed)
                deleteEvent.DangerousRelease();
        }
    }

    /// <summary>Starts a process using the Wc job-list process attribute and silo session rules.
    /// The caller owns the returned process handle.</summary>
    public SafeProcessHandle LaunchApplication(string commandLine, uint creationFlags = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandLine);
        if (commandLine.Contains('\0'))
            throw new ArgumentException("The command line contains NUL.", nameof(commandLine));

        return Use(handle =>
        {
            _ = _api.GetId(handle);

            var process = new SafeProcessHandle();
            Marshal.InitHandle(process, _api.Launch(handle, commandLine, creationFlags));
            return process;
        });
    }

    public Guid Identifier => Use(_api.GetId);
    public string ObjectRoot => Use(_api.GetObjectRoot);
    public string RegistryRoot => GetRegistryRootPath("");

    /// <summary>Returns the registry root followed by the supplied managed suffix verbatim.</summary>
    public string GetRegistryRootPath(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return Use(handle => _api.GetRegistryRoot(handle) + suffix);
    }

    /// <summary>Opens the silo's COM registry root. The caller owns the returned key handle.</summary>
    public SafeRegistryHandle OpenComRegistryRoot() => Use(handle =>
    {
        var key = new SafeRegistryHandle();
        Marshal.InitHandle(key, _api.GetRegistryHandle(handle, NativeProviderConstants.WcVRegComRoot));
        return key;
    });

    public bool IsQuiescent => Use(handle =>
    {
        var processes = new JOBOBJECT_BASIC_PROCESS_ID_LIST { NumberOfProcessIdsInList = 1 };
        var status = _api.QueryJob(handle, (int)JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, processes.AsBytes());
        if (status == NTSTATUS.STATUS_BUFFER_OVERFLOW)
            return false;

        SiloException.CheckNt(status, "QueryJobProcessIdList");
        return processes.NumberOfAssignedProcesses == 0;
    });

    public void SetRegistryFlushEnabled(bool enabled) => Use(handle =>
    {
        const uint disableFlush = NativeProviderConstants.WcVRegDisableFlush;

        _ = _api.GetId(handle);
        _api.ModifyRegistryFlags(handle, enabled ? 0u : disableFlush, enabled ? disableFlush : 0u);
    });

    public void AddRuntimeVirtualKeys(IReadOnlyList<RuntimeVirtualKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Use(handle =>
        {
            _ = _api.GetId(handle);
            _api.AddRuntimeVirtualKeys(handle, keys);
        });
    }

    public void ShutdownApplication() => Use(handle =>
    {
        _ = _api.GetId(handle);
        if (_api.SetJob(handle, NativeJobConstants.JobObjectServerSiloRunningState, [NativeJobConstants.WcAppSiloShutdownState, 0, 0, 0]) == NativeJobConstants.WcAppSiloShutdownNotStarted)
            SiloException.CheckHResult(SiloException.FromWin32((int)WIN32_ERROR.ERROR_RETRY), "Shutdown application silo");
    });

    /// <summary>Registers for termination events. The caller owns the returned subscription.</summary>
    /// <remarks>
    /// A job can have one active managed subscription across controllers using this library instance.
    /// Wait for its callbacks or dispose it before registering again. Coordinate completion port
    /// ownership with direct or external users of the job. The native retry can clear an existing
    /// external completion port association even if the retry fails.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A managed subscription is attaching, attached,
    /// or draining callbacks for this job.</exception>
    public SiloTerminationNotification RegisterForTerminationNotification() => Use(_api.RegisterForTerminationNotification);

    public void Terminate(int status = 0) => Use(handle => _api.Terminate(handle, status));

    private T Use<T>(Func<JobSafeHandle, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_jobLease.IsClosed, this);

            var result = action(_jobLease);
            GC.KeepAlive(_jobLease);
            return result;
        }
    }

    private void Use(Action<JobSafeHandle> action) => Use(handle =>
    {
        action(handle);
        return true;
    });

    /// <summary>Runs Wc provider cleanup without detaching a WCIFS volume. The job handle remains open.</summary>
    public void Cleanup() => Cleanup(null);

    /// <summary>Runs Wc provider cleanup again on each call while the job handle is open.
    /// Supply a nonempty volume mount point to request WCIFS detachment.</summary>
    public void Cleanup(string? volumePath)
    {
        lock (_gate)
        {
            if (_jobLease.IsClosed)
                return;

            _ = _api.GetId(_jobLease);
            if (volumePath is not null)
            {
                var terminator = volumePath.IndexOf('\0');
                if (terminator >= 0)
                    volumePath = volumePath[..terminator];
            }

            _api.CleanupNetwork(_jobLease);
            _api.CleanupRegistry(_jobLease);
            _api.CleanupFileSystem(_jobLease, volumePath);
            _hasCompletedCleanup = true;

            GC.KeepAlive(_jobLease);
        }
    }

    private void RollbackCreate()
    {
        try
        {
            List<Exception> failures = [];

            // ReSharper disable once AccessToDisposedClosure
            if (_networkCleanupArmed || _providersConfigured)
                CleanupProvider(() => _api.CleanupNetwork(_jobLease), failures);

            // ReSharper disable once AccessToDisposedClosure
            if (_registry || _providersConfigured)
                CleanupProvider(() => _api.CleanupRegistry(_jobLease), failures);

            if (_fileSystemRollback is { } registration)
                CleanupProvider(registration.Rollback, failures);

            if (failures.Count != 0)
                throw new AggregateException("Silo creation rollback failed.", failures);
        }
        finally
        {
            _jobLease.Dispose();
        }
    }

    private static void CleanupProvider(Action cleanup, List<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
    }
}