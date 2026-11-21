# How Windows silos work in this repository

Suppose two builds both take a mutex named `Global\BuildLock` and write `C:\BuildView\result.bin`. We want them to run concurrently on one host. Each build needs a different mutex object and a different backing directory, even though the executable opens identical names. The direct silo library implements these two choices independently. The same library can also configure and boot a Windows server silo from a prepared image.

## Choose the mode and assembly

A Windows **job object** groups processes and carries limits and lifetime policy. Windows can turn a job into a **silo** and associate an object namespace, filesystem view, and other configuration with it. Both modes here use the host kernel.

| | Non-server (application) silo | Server silo |
|---|---|---|
| Typical use here | Run a host executable with selected overrides | Run processes in a prepared Windows container OS image |
| Inputs | A description of the views or limits the application needs | A description plus the corresponding filesystem layers, scratch storage, registry hives and image configuration |
| Startup | `Create(description)`, then `LaunchApplication` | `Create(description, server: true)`, then `BootServer` before launching into the server service session |
| Identity at launch | Uses the calling process's primary security context. A LocalSystem service launches a LocalSystem child. | Duplicates the calling process's token and sets its session ID to the silo's assigned service session. |

Reference **one project for the host OS** in your .NET 10 x64 application. Server 2022 uses `Containers.Windows.Silos.csproj` (build family 20348). Server 2025 uses `Containers.Windows.Silos2025.csproj` (26100). Both expose `Containers.Windows.Silos` for the controller and `Containers.Windows.Silos.DescriptionModel` for the configuration records. The assemblies share source and carry different native server contracts. [2022 project](../src/Containers.Windows.Silos/Containers.Windows.Silos.csproj) · [2025 project and version differences](../src/Containers.Windows.Silos2025/README.md)

### Where HCS fits

In the ordinary process-isolated Windows container path, the Host Compute Service (`vmcompute.exe`) prepares a container definition from a template and runtime settings. At the Wc boundary it serializes the definition to XML, calls `container.dll!WcCreateDescriptionFromXml`, and passes the parsed description and job to `WcCreateContainer`. This is why the shipped `wsc.def` alone doesn't describe all the paths and hive stacks in a running container. The C# library starts **at that Wc boundary**. Your code provides the description and prepared resources, and the library calls the underlying NT, Win32, and driver interfaces directly. Pulling an image, preparing layers, provisioning a virtual network endpoint, and coordinating a container workload belong to the higher-level runtime. [HCS definition investigation](progress.md#hcs-description-xml-on-windows-server-2022) · [Direct library scope and native contracts](ports/windows-silos.md)

On the server-silo path, HCS also waits for the `CExecSvcStarted` event and connects to the container execution service over local RPC before forwarding workload process requests. The direct library exposes `BootServer` and `LaunchApplication` separately. A host that needs application readiness must choose a signal appropriate to its workload. The non-server example below starts the executable directly. [Managed process launcher](../src/Containers.Windows.Silos/NativeApi.Process.cs)

## Run two builds with separate file and mutex views

The following example is for a **LocalSystem service in session 0**. The host must have Bind Filter available on the working volume. The verified guests had the Containers feature installed. The service token must be able to create permanent Object Manager objects and read the security descriptor being cloned, including its SACL. The test harness enabled `SeSecurityPrivilege` on its LocalSystem token. Run the code as a host process and pass a Windows command line for an executable configured to use the visible directory.

```csharp
using Containers.Windows.Silos;
using Containers.Windows.Silos.DescriptionModel;

static async Task RunBuildAsync(string commandLine, string visibleDirectory,
    string backingDirectory, CancellationToken cancellationToken)
{
    Directory.CreateDirectory(visibleDirectory);
    Directory.CreateDirectory(backingDirectory);

    var description = SiloDescriptionXml.Parse("""
        <container><namespace><ob shadow="true">
          <objdir name="BaseNamedObjects" clonesd="\BaseNamedObjects" shadow="false">
            <symlink name="Global" path="\BaseNamedObjects" scope="local" />
            <symlink name="Local" path="\BaseNamedObjects" scope="local" />
          </objdir>
        </ob></namespace></container>
        """) with
    {
        Job = new JobConfiguration { KillOnLastHandleClose = true },
        FileSystem = new FileSystemConfiguration
        {
            Bindings = [new BindMapping(visibleDirectory, backingDirectory)]
        }
    };

    using var silo = WindowsSilo.Create(description);
    using var child = silo.LaunchApplication(commandLine);
    while (!silo.IsQuiescent)
        await Task.Delay(100, cancellationToken);
}
```

Call the helper twice with the same command line and visible path. Give each call its own backing directory. For example, from an async method in the service:

```csharp
await Task.WhenAll(
    RunBuildAsync(commandLine, @"C:\BuildView", @"C:\BuildRuns\A", cancellationToken),
    RunBuildAsync(commandLine, @"C:\BuildView", @"C:\BuildRuns\B", cancellationToken));
```

| When the workload opens… | Build A sees | Build B sees | A host process sees |
|---|---|---|---|
| `Global\BuildLock` | A's mutex | B's mutex | The host's mutex of that name, if present |
| `C:\BuildView\result.bin` | `C:\BuildRuns\A\result.bin` | `C:\BuildRuns\B\result.bin` | `C:\BuildView\result.bin` |

- **Mutex lookup.** The silo root shadows the host root, so lookups can fall through to host objects. The example creates a private `BaseNamedObjects` directory with host fallback disabled. Its `Global` and `Local` links both resolve inside that silo. `clonesd` copies the host directory's security descriptor. This config changes the view of other named synchronization objects and shared sections too. Interactive sessions have different named-object directories. [Object Manager setup](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Session-0 test configuration](../tests/Containers.Windows.Silos.Tests/WindowsViewTests.cs)
- **File lookup.** `BindMapping(root, target)` takes the application-visible path first and the backing path second. Bind Filter associates the mapping with the job. A folder mapping covers its descendants. To redirect only one file, supply the two absolute filenames instead. The managed `Bindings` example uses Bind Filter on its own. Parsing a Wc XML `<filesystem>` section also creates WCI configuration and requests the layer driver. [Filesystem provider](../src/Containers.Windows.Silos/NativeApi.FileSystem.cs) · [XML reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs)
- **Process launch.** `LaunchApplication` takes one Windows command-line string. Quote executable and argument paths that contain spaces. The library puts the job in `PROC_THREAD_ATTRIBUTE_JOB_LIST` before `CreateProcessAsUserW`, so the child starts in the configured job. Keep the controller alive while its children run. The caller owns the returned `SafeProcessHandle`. The example waits for the whole job to empty. Cancellation unwinds the two handles. With this owned job and last-close policy, remaining job processes terminate. To report build success, wait for the child handle and read its process exit code as the Windows view test does. [Process launcher](../src/Containers.Windows.Silos/NativeApi.Process.cs) · [Exit-code example](../tests/Containers.Windows.Silos.Tests/WindowsViewTests.cs)
- **Remote shares and identity.** This description leaves `NetworkCompartment` unset. Host routing remains available. An open of `\\server\share\file` goes through the SMB client and has its own authorization checks. A local folder mapping supplies neither a remote share mapping nor credentials. This recipe also leaves host authentication services in place.

## What happens during creation and server boot

1. **Create or supply a job.** `WindowsSilo.Create` allocates and owns a new job. `ConfigureJob` configures an existing job and holds a reference to the supplied `JobSafeHandle` until disposal. The caller keeps ownership of that handle. If another library supplies the job in a different SafeHandle type, use `using var job = JobSafeHandle.Borrow(existingJob);` before calling `ConfigureJob(job, description)`. Configuration mutates the supplied job as it proceeds, so a failure can leave job settings applied.
2. **Convert the job.** The controller applies requested CPU and memory limits and the job lifetime policy, then calls `NtSetInformationJobObject(JobObjectCreateSilo)` with an empty payload. Both modes go through this step. For a server silo the next mode-specific step registers the job through the DAM control device, with a Session Manager (SMSS) registration path if the device cannot be opened.
3. **Configure the views.** Setup proceeds in order through Object Manager, filesystem, virtual registry, network compartment, drive mounts, and named pipes as requested. A missing provider generally skips setup. An explicitly present empty provider can still initialize kernel or driver state. For example, an empty named-pipe list still sends NPFS a silo-arrival notification. `Identifier`, `ObjectRoot`, and `RegistryRoot` expose the job GUID and namespace paths for diagnostics. Query the paths on the controller rather than constructing them from a guessed silo number. [Controller and setup order](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Server registration](../src/Containers.Windows.Silos/NativeApi.cs)

| Provider | What the configuration changes |
|---|---|
| Object Manager | Creates silo-relative directories and symbolic links. Controls host-directory fallback and optional security descriptor cloning. |
| Filesystem | Installs explicit mappings through `bindfltapi!BfSetupFilter` or reads Bind batches. A WCI configuration calls `wc_storage!WcAttachFilterEx` to attach the Windows Container Isolation filesystem filter for layers and a writable scratch root. |
| Virtual registry | Registers the job with the virtual registry driver, builds hive stacks, then installs redirections and symbolic links. |
| Network | Creates and assigns a TCP/IP compartment for a nonempty compartment name. Adapter, endpoint, and route provisioning are separate operations. |
| Mount Manager and NPFS | Establish silo drive mappings and named-pipe links respectively. These use separate device interfaces from ordinary file bindings. |

The implementations follow these boundaries. [Description records](../src/Containers.Windows.Silos/DescriptionModel/SiloDescription.cs) · [Registry provider](../src/Containers.Windows.Silos/NativeApi.Registry.cs) · [Network, mount and pipe providers](../src/Containers.Windows.Silos/NativeApi.Providers.cs)

**Booting a server silo** requires an image-backed filesystem and virtual registry prepared for that silo. With those inputs ready, the hosting sequence is:

```csharp
using var deleted = new EventWaitHandle(false, EventResetMode.ManualReset);
using var server = WindowsSilo.Create(serverDescription, server: true);
server.BootServer(deleted.SafeWaitHandle);
// The host keeps the controller and event alive while it manages the server.
```

`BootServer` passes the deletion event in job information class 40, then asks SMSS to start the registered server silo. The event observes deletion. It is distinct from workload readiness and from the child process handle. Server 2022 checks host/image compatibility during creation. Server 2025 checks at boot and includes the image build in the initialization packet. On 2025, `ServiceSessionId` reports the assigned service session, and `IsFirstBoot` queries whether the job has **ever** had a process. Standalone UnionFS setup and migration are separate 2025 APIs. [2022 initialization](../src/Containers.Windows.Silos/WindowsSilo.ServerInitialize.cs) · [2025 initialization and queries](../src/Containers.Windows.Silos2025/WindowsSilo.Initialize.cs) · [2025 filesystem API](../src/Containers.Windows.Silos2025/README.md)

## Observe completion and release resources

| When you need to… | Use | What it tells you or does |
|---|---|---|
| Collect a command's result | The handle from `LaunchApplication` | Wait for that process and read its exit code. Other processes can still belong to the job. |
| Check whether the job is empty | `IsQuiescent` | Queries the current process list. This is a snapshot. |
| Observe termination notifications | `RegisterForTerminationNotification()`, then `ReadAsync()` | Register before launching a short-lived workload. One active managed subscription is supported per job. |
| Request application-silo shutdown or terminate the whole job | `ShutdownApplication()` or `Terminate(status)` | The first requests an application-silo state transition. The second calls `NtTerminateJobObject`. |
| Clean up providers while retaining the job | `Cleanup()` or `Cleanup(volumeMountPoint)` | Runs network, registry, then filesystem cleanup on every explicit call. Supply a nonempty volume mount point to request WCIFS detach. |

The managed reason names are `NormalExit` (0), `UnexpectedExit` (1), `Reboot` (2), and, on 2025, `Terminated` (3) for a critical system-process exit. A non-server silo reports 0 for every delivered termination notification, including when an application failed. Multiple readers of one subscription compete for events. `WaitForCallbacks()` detaches and drains callbacks already queued. To receive a future event, keep the subscription attached and read from it. [Notification lifecycle](../src/Containers.Windows.Silos/SiloTerminationNotification.cs) · [2022 reasons](../src/Containers.Windows.Silos/SiloPlatform.cs) · [2025 reasons](../src/Containers.Windows.Silos2025/SiloPlatform.cs)

**Handle ownership determines last-close behavior.** With `KillOnLastHandleClose = true`, Windows terminates remaining job processes when the last job handle closes. A subscription owns a duplicate job handle until disposed, even after `WaitForCallbacks()`. A caller-owned handle passed through `ConfigureJob` also counts. Dispose the subscription and any other job handles if the service relies on last-close termination.

`Dispose()` runs null-path cleanup if explicit cleanup has not already succeeded, then releases the controller's job reference even if cleanup throws. Normal cleanup leaves live explicit Bind mappings in place. Failed creation has a separate rollback path that attempts to remove the explicit mappings already installed. Backing folders and their output files belong to the caller. On a native failure, log `SiloException.Operation`, `HResult`, and the populated `NtStatus` or `Win32Error`. Setup can attach a secondary rollback failure at `Exception.Data["SiloCleanupFailure"]`. [Cleanup and ownership](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Error mapping](../src/Containers.Windows.Silos/SiloException.cs)

## Verify a workload on Windows

The **two-worker folder harness** is the closest reference for the build-service case. On Server 2022 build 20348.587, two native children in session 0 each acquired the identical global mutex name and held an exclusive file handle under the same visible path for over 12 seconds concurrently. The host observed separate backing files. In the control run, removing only the private object namespace made the second child wait for the first to release the mutex. The manifest entry `verification.twoConcurrentSiloWriters` identifies the retained harness, native worker, and output.

For a **single-file check** against the current checkout, run the opt-in Windows test on a configured x64 guest from the repository root. In PowerShell, select an existing writable scratch directory on the filtered volume:

```powershell
$env:WC_SILO_TEST_SCRATCH = 'C:\SiloScratch'
dotnet test .\tests\Containers.Windows.Silos.Tests\Containers.Windows.Silos.Tests.csproj --artifacts-path "$env:WC_SILO_TEST_SCRATCH\artifacts" --filter 'FullyQualifiedName~WindowsViewTests.BindMappingOverridesOnlyTheChildView'
```

The combined `ChildUsesPrivateGlobalMutexAndOverriddenFile` test uses PowerShell as its child. In the recorded runs that child timed out before it signalled readiness under the private namespace in both the native Wc implementation and the C# port. A native worker passed the mutex and file checks on both OS families. A separate session-0 domain-worker profile passed PowerShell STA, DFS, and SQL Kerberos checks on both OS families. It explicitly shares the host’s `Sessions`, `Windows`, and `RPC Control` directories. Live coverage also includes server-silo creation and queries on 2025. Full server OS boot remains unverified. [Domain-worker XML and link requirements](windows-silo-object-manager.md#host-links-for-session-0-domain-workers) [Windows view tests and opt-in settings](../tests/Containers.Windows.Silos.Tests/WindowsViewTests.cs) · [Server 2025 verification](../src/Containers.Windows.Silos2025/README.md#windows-verification)
