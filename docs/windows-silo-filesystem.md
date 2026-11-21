# Windows silo filesystem, drives, and named pipes

`SiloDescription.FileSystem` configures file lookup through WCIFS and Bind Filter. `Mounts` assigns drive letters to volume devices through Mount Manager. `NamedPipes` installs aliases through the Named Pipe File System (NPFS). These providers run during `WindowsSilo.Create` or `ConfigureJob`, after Object Manager setup if requested. The Object Manager resolves names such as `\Device` and `\DosDevices` to kernel objects. The selected filesystem or driver then handles the rest of the open. Use `ObjectNamespace` to configure named kernel objects such as mutexes and `NamedPipes` for pipe aliases. Both the `<ob>` and `<namedpipe>` sections include a `<symlink>` element, with different fields and driver behavior. [Description model](../src/Containers.Windows.Silos/DescriptionModel/SiloDescription.cs) · [Setup order](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Object Manager provider](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Description reference](windows-silo-description.md)

## `FileSystemConfiguration`

| Property | Default | Role and XML mapping |
|---|---|---|
| `SiloDescription.FileSystem` | `null` | Omit it to skip the filesystem provider. `<filesystem>` under `<container><namespace>` supplies this record and always creates `Wci`. |
| `Wci` | `null` | Set it to attach WCIFS to prepared scratch storage. `<filesystem>` supplies the scratch path, modes, and layer list. |
| `Batches` | Empty | Ordered list of `BindBatch` records. Each `<bindings>` child supplies one batch. |
| `Bindings` | Empty | Ordered list of explicit `BindMapping` records, available in C#. |

Setup attaches WCIFS first if `Wci` is set, installs batches in list order, then installs explicit mappings in list order. A managed description can use batches or explicit mappings with `Wci = null`. An empty `<filesystem/>` still sets `Wci` and passes an empty scratch path, default modes and zero layers to the attach call. Parse success establishes the description's shape. The driver and the supplied paths determine setup success. [Filesystem model](../src/Containers.Windows.Silos/DescriptionModel/FileSystemConfiguration.cs) · [XML reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs) · [Filesystem provider](../src/Containers.Windows.Silos/NativeApi.FileSystem.cs)

### WCIFS layers and scratch storage

`wc_storage.dll!WcAttachFilterEx` registers the supplied scratch root and ordered layers with the Windows Container Isolation filesystem minifilter (`wcifs.sys`). WCIFS resolves reads of prepared placeholders to the backing layers. On a write it can replace the placeholder with a private copy in the writable filesystem. Supply a prepared scratch root and existing layers. The attach call configures their view. Image acquisition and preparation occur before this call. [Native attach and descriptor mapping](ports/windows-silos.md#storage-teardown-evidence) · [Microsoft's WCIFS documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/anti-virus-optimization-for-windows-containers)

| Record property | Values and behavior | Wc XML |
|---|---|---|
| `WciConfiguration.ScratchRoot` | Constructor argument to `WciConfiguration(string scratchRoot)` and then get-only. Pass the prepared writable root to the storage provider. | `<filesystem path="…">`. Omission parses as an empty string. |
| `WciConfiguration.IsolationMode` | `uint`. `0` is hard, the default. `1` is soft. | `isolation="hard"` or `isolation="soft"`. |
| `WciConfiguration.NestingMode` | `uint`. `0` is inner, the default. `1` is outer. | `nesting="inner"` or `nesting="outer"`. |
| `WciConfiguration.Layers` | Empty by default. The provider forwards each layer in supplied order. | Repeated `<layer>` children. |
| `WciConfiguration.DetachVolumePath` | Optional Win32 volume mount point for failed-creation rollback after a successful attach. Defaults to `ScratchRoot` if null. | C# only. |
| `WciLayer.Id` | Get-only `Guid` supplied to `WciLayer(Guid id, string path)`. | `<layer identifier="…">`. Use an unbraced `8-4-4-4-12` hex GUID. Omission parses as `Guid.Empty`. |
| `WciLayer.Path` | Get-only path supplied to the same constructor. The provider passes it as UTF-16. | `<layer path="…">`. Omission parses as an empty string. |

The managed provider rejects mode values above `1` and sends a fixed zero `Flags` DWORD on every native layer descriptor. `IsolationMode` and `NestingMode` select driver modes. `server: true` independently selects a server silo in `WindowsSilo`. [WCI properties](../src/Containers.Windows.Silos/DescriptionModel/WciConfiguration.cs) · [Layer record](../src/Containers.Windows.Silos/DescriptionModel/WciLayer.cs) · [Mode and layer packing](../src/Containers.Windows.Silos/NativeApi.FileSystem.cs)

### Bind Filter batches

| `BindBatch` property | Values and behavior | Wc XML |
|---|---|---|
| `ConfigurationPath` | Get-only path supplied to `BindBatch(string configurationPath)`. It identifies an existing Bind Filter batch file opened by `CreateFileW`. | `<bindings><filepath>…</filepath></bindings>`. Omission parses as an empty string. |
| `Exceptions` | Init-only list of exclusion roots passed to Bind Filter, empty by default. | Repeated `<bindingroots>` children. Each contains one path, despite the plural element name. |

The file holds the driver's batch payload. The wrapper forwards opaque bytes and the separately supplied exception strings. It opens the file for shared read, sizes a zeroed buffer, performs a single read and closes the file before invoking `BfSetupFilterBatched`. The supplied byte count is the original file size, including any zeroed tail after a successful short read. The first batch calls the install operation with `0x000000D1`. Each later batch first calls exception registration with `0x20000000`, then install with `0x000000D1`. Both calls carry the same file buffer and exception array for that batch. These are fixed batch control words. [Batch model](../src/Containers.Windows.Silos/DescriptionModel/BindBatch.cs) · [Batch calls](../src/Containers.Windows.Silos/NativeApi.FileSystem.cs) · [Provider constants](../src/Containers.Windows.Silos/NativeProviderConstants.cs)

### Explicit Bind Filter mappings

`BindMapping(root, target)` takes the silo-visible path first and the backing path second. Use full Windows filenames for a single file, or full directory paths to redirect a tree. `bindfltapi.dll!BfSetupFilter` associates the mapping with the silo job. Writes through a writable mapping go to the backing path, subject to the driver's access checks. For separate working copies, supply each job its own backing directory. [Bind provider](../src/Containers.Windows.Silos/NativeApi.FileSystem.cs) · [Two concurrent non-server writers](progress.md#two-concurrent-non-server-silo-writers-on-windows)

| `BindMapping` property | Values and behavior |
|---|---|
| `Root` | Get-only constructor argument. The path opened by the workload. |
| `Target` | Get-only constructor argument. The backing path for that open. |
| `Flags` | Get-only fixed value `0x00000004` (`BINDFLT_FLAG_USE_CURRENT_SILO_MAPPING`). |
| `Exceptions` | Get-only empty list. Per-batch exclusions are configurable through `BindBatch.Exceptions`. |

Use the managed API for explicit mappings. The XML `<bindings>` element represents a batch file. The separate server-image host-file fixup uses the driver's `0x00000001` read-only flag internally. [Explicit mapping model](../src/Containers.Windows.Silos/DescriptionModel/BindMapping.cs) · [Server-image fixups](../src/Containers.Windows.Silos/NativeApi.ServerImage.cs) · [Pinned hcsshim Bind Filter declarations](https://github.com/microsoft/hcsshim/blob/900183417d29765de2947de22a6e72c22c195550/internal/winapi/bindflt.go#L3-L19)

```csharp
using Containers.Windows.Silos.DescriptionModel;

var description = new SiloDescription
{
    FileSystem = new FileSystemConfiguration
    {
        Bindings = [new BindMapping(@"C:\BuildView", @"C:\BuildRuns\A")]
    }
};
```

Pass this description to `WindowsSilo.Create` for a Bind-only application silo. Prepare the two directories before creation and use a volume with Bind Filter available. If a child opens `C:\BuildView\result.bin`, the configured backing is `C:\BuildRuns\A\result.bin`. See the walkthrough for launch and handle lifetime. [Application-silo example](windows-silos.md#run-two-builds-with-separate-file-and-mutex-views)

## `Mounts` and `DriveMapping`

`SiloDescription.Mounts` is a nullable `IReadOnlyList<DriveMapping>`. Omit it to skip Mount Manager. An empty list or `<mountmgr/>` still sends Mount Manager the silo-arrival notification. Each `<mount_point>` adds a record. [Mount provider](../src/Containers.Windows.Silos/NativeApi.Providers.cs) · [Drive record](../src/Containers.Windows.Silos/DescriptionModel/DriveMapping.cs)

| `DriveMapping` property | Values and behavior | Wc XML |
|---|---|---|
| `Drive` | Get-only `char` supplied to `DriveMapping(char drive, string target)`. Supply a drive letter such as `'X'`. Setup uppercases it and makes the silo link `\DosDevices\X:`. | `<mount_point name="X:">` takes the first UTF-16 code unit. Omission or an empty name parses as `\0`. |
| `Target` | Get-only constructor argument. Supply the native device name of the existing volume, such as `\Device\HarddiskVolume9`. | `<mount_point path="…">`. Omission parses as an empty string. |

The provider sends silo arrival to `\\.\MountPointManager` (`0x006DC054`), then volume arrival (`0x006D402C`) and create point (`0x006DC000`) to the silo's `\Device\MountPointManager` for each mapping. This associates a letter with an existing volume. `ERROR_INVALID_PARAMETER` (`87`) is accepted only from create point, following the pinned Wc implementation. A target of 32,768 or more UTF-16 code units exceeds the managed packet length field and throws `OverflowException`. [Managed control calls and packing](../src/Containers.Windows.Silos/NativeApi.Providers.cs) · [WDK target-name contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ns-mountmgr-_mountmgr_target_name) · [WDK create-point contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ns-mountmgr-_mountmgr_create_point_input)

## `NamedPipes` and `PipeMapping`

`SiloDescription.NamedPipes` is a nullable `IReadOnlyList<PipeMapping>`. Omit it to skip NPFS. An empty list or `<namedpipe/>` still sends NPFS the silo-arrival notification. Each `<symlink>` adds a record. [NPFS provider](../src/Containers.Windows.Silos/NativeApi.Providers.cs) · [Pipe record](../src/Containers.Windows.Silos/DescriptionModel/PipeMapping.cs)

| `PipeMapping` property | Values and behavior | Wc XML |
|---|---|---|
| `Name` | Get-only constructor argument to `PipeMapping(string name, string target)`. Give the name within the silo's pipe filesystem, such as `build-status`. A Win32 child opens `\\.\pipe\build-status`. | `<symlink name="…">`. Omission parses as an empty string. |
| `Target` | Get-only constructor argument. Give the native substitute path, such as `\Device\NamedPipe\host-build-status`. | `<symlink path="…">`. Omission parses as an empty string. |

The provider sends `FSCTL_PIPE_SILO_ARRIVAL` (`0x118048`) to `\\.\pipe` with the job handle. For a nonempty list it opens the silo's `\Device\NamedPipe` and issues `FSCTL_PIPE_CREATE_SYMLINK` (`0x11004C`) for each mapping. The link packet contains counted UTF-16 strings and the fixed `FILE_PIPE_SYMLINK_FLAG_GLOBAL` (`1`). Microsoft documents the global NPFS reparse as a link from a server silo into the host silo. The target pipe server supplies instances, and its access checks still apply. The wrapper caps `Name` at 32,761 UTF-16 code units and `Target` at 32,767. Lengths above these packing limits throw `SiloException` with `NtStatus` set to `STATUS_INVALID_PARAMETER`. [Pipe packet encoding](../src/Containers.Windows.Silos/NativeApi.Providers.cs) · [Microsoft reparse tags](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4) · [Pipe names](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-names) · [Pipe access rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)

## Wc XML example

Pass the description text to `SiloDescriptionXml.Parse`. The example shows the grammar and path forms. Substitute prepared scratch/layer paths, a real driver batch, the device name of an existing volume, and the target pipe used by your deployment. The schema compares element names, attribute names and mode choices without regard to ASCII case. Each `<filesystem>`, `<mountmgr>` and `<namedpipe>` occurs at most once per namespace. [XML reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs) · [Parser tests](../tests/Containers.Windows.Silos.Tests/SiloDescriptionXmlTests.cs)

```xml
<container>
  <namespace>
    <filesystem path="C:\SiloStorage\Scratch\" isolation="hard" nesting="inner">
      <layer identifier="00112233-4455-6677-8899-aabbccddeeff" path="C:\SiloStorage\Layers\base" />
      <bindings>
        <filepath>C:\SiloStorage\config\bindings.bin</filepath>
        <bindingroots>C:\SiloStorage\excluded</bindingroots>
      </bindings>
    </filesystem>
    <mountmgr>
      <mount_point name="C:" path="\Device\HarddiskVolume9" />
    </mountmgr>
    <namedpipe>
      <symlink name="build-status" path="\Device\NamedPipe\host-build-status" />
    </namedpipe>
  </namespace>
</container>
```

On Server 2022, HCS's `wsc.def` server template includes empty `<mountmgr>` and `<namedpipe>` elements. HCS adds the `C:` mount to the in-memory definition when that section is present and populates the emitted XML from runtime inputs. The non-server `helium.def` template is empty. The C# create path dispatches all three providers in either mode when configured. A server silo also has separate image/registry and boot requirements. A successful Bind-only app-silo test establishes job-scoped file redirection. Mount Manager and NPFS tests establish packet layout and error handling, with live behavior for each mode and configured WCIFS still requiring evidence. [Server setup and boot](windows-silos.md#what-happens-during-creation-and-server-boot) · [Provider tests](../tests/Containers.Windows.Silos.Tests/PlatformProviderTests.cs)

## Cleanup and Server 2025 standalone UnionFS

`WindowsSilo.Cleanup(volumePath)` requests WCIFS detachment for a nonempty Win32 mount point, such as a drive root with a trailing backslash. The provider resolves its volume GUID and calls `FilterDetach` for the fixed `wcifs Instance` in either silo mode. Negative detach results are logged. `Cleanup()` or an empty path skips detachment. Normal cleanup preserves explicit Bind mappings on the live job. On failed creation, managed rollback attempts to remove completed explicit mappings in reverse order and detach a successfully attached WCIFS volume using `DetachVolumePath` or `ScratchRoot`. Live 2022 tests covered repeated cleanup with a file binding, with live nonempty-path WCIFS detachment still pending.

Both assemblies use WCIFS for the `FileSystem.Wci` path in ordinary silo creation. Server 2025 also exposes `SiloFileSystemNamespace.Setup(scratchRoot, layers, batches, job)` and `Cleanup(mountPath)` as a standalone UnionFS lifecycle. The scratch path must contain a braced mount GUID. The API uses the scratch path first and then each supplied `WciLayer.Path` in order. The standalone provider identifies the union from the scratch GUID and consumes layer paths only. A fresh union initializes five offline delta hives under `WcSandboxState\Hives`. If `Windows\container_tombstones.json` exists beneath the scratch root, setup migrates from it instead. Optional Bind batches require a supplied live job handle, which setup borrows for the call. Cleanup removes the union identified by the mount path. These calls use the separate UnionFS provider in the Server 2025 assembly. [Standalone provider](../src/Containers.Windows.Silos2025/NativeApi.FileSystemUnion.cs) · [Server 2025 version comparison and Windows results](../src/Containers.Windows.Silos2025/README.md)
