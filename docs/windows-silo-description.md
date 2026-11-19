# Windows silo description reference

`WindowsSilo.Create(description, server: …)` and `WindowsSilo.ConfigureJob(job, description, server: …)` take the same nine-section `SiloDescription`. **The `server` argument selects the mode.** Both editions parse the same Wc XML grammar. A server image happens to use many of these options together, but most providers can also be configured for an application silo. Here, **both** means the controller calls the provider in either mode. Each provider still needs its own Windows driver, privileges, and valid inputs. [Managed description](../src/Containers.Windows.Silos/DescriptionModel/SiloDescription.cs) · [Creation and dispatch](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Walkthrough and Windows verification](windows-silos.md)

## Which option controls which resource?

All XML section names in the table are children of `<container><namespace>`. Despite its name, `<namespace>` groups the whole description, including CPU limits and networking. Only `<ob>` directly defines an Object Manager directory tree.

| C# property | Wc XML | Mode | Resource owner and effect |
|---|---|---|---|
| [`Job`](#job-limits-lifetime-and-system-root) | `job` | Both | The kernel job carries CPU and memory limits, last-handle policy, the silo system-root string, and the thread impersonation setting. |
| [`ObjectNamespace`](windows-silo-object-manager.md) | `ob` | Both | The Object Manager resolves NT directory and symbolic-link names. Use this for private named mutexes, events, or sections. Creation takes a mode-specific preparation step before configuring the tree. |
| [`FileSystem`](windows-silo-filesystem.md#filesystemconfiguration) | `filesystem` | Both | WCIFS supplies layers and a scratch root. Bind Filter redirects selected paths for a job. Individual managed `Bindings` have no XML field. |
| [`Registry`](windows-silo-registry-device.md#virtual-registry) | `registry` | Both | The virtual registry driver and registry APIs load hives, redirect key lookups, and populate keys and values. |
| [`NetworkCompartment`](#network-compartment) | `network` | Both | The networking stack creates a TCP/IP compartment and associates its ID with the job. |
| [`Mounts`](windows-silo-filesystem.md#mounts-and-drivemapping) | `mountmgr` | Both | Mount Manager registers volumes and drive-letter mount points in the silo. Use a filesystem binding for redirection beneath an existing drive letter. |
| [`NamedPipes`](windows-silo-filesystem.md#namedpipes-and-pipemapping) | `namedpipe` | Both | The Named Pipe File System (NPFS) initializes the silo pipe view and creates pipe links. The Object Manager resolves the device prefix. |
| [`Device`](windows-silo-registry-device.md#device-metadata) | `device` | Parsed in both | Holds device-store, category, instance, and interface-class settings from the XML. Direct silo creation retains these records without configuring devices from them. |
| [`Downlevel`](windows-silo-registry-device.md#downlevel-host-file-mappings) | `hostfiles` | **Server only for setup** | The server image provider can bind selected host Windows files into the image. The parser also retains `BaseImagePath` as metadata. |

For example, an open of `C:\BuildView\result.bin` involves a drive name and a filesystem lookup. Change `Mounts` to associate a drive letter with a volume, or `FileSystem.Bindings` to give the job a different backing file or folder. An open of `Global\BuildLock` resolves to a named synchronization object and uses `ObjectNamespace`. For `\\.\pipe\Build`, name resolution reaches the pipe device, and NPFS handles the pipe name. [Object Manager paths and lookup](windows-silo-object-manager.md) · [Filesystem, mount points and pipes](windows-silo-filesystem.md)

## Job limits, lifetime and system root

All five `JobConfiguration` properties apply on the common creation path. Omitting the entire `Job` section still creates a silo job with its default lifetime policy. The following example caps CPU at 25%, requests a 1 GiB job memory limit, and enables termination when the last job handle closes.

```xml
<container>
  <namespace>
    <job>
      <cpu rate="2500"/>
      <memory limit="1073741824"/>
      <terminationpolicy>KillOnHandleClose</terminationpolicy>
    </job>
  </namespace>
</container>
```

| Managed option | XML inside `job` | Default and behavior |
|---|---|---|
| `Cpu` (`CpuConfiguration`) | `<cpu …/>` | Omitted means no CPU-control write. A present, empty element submits a zeroed CPU-control structure. |
| `Cpu.MaximumRate` | `<cpu rate="2500"/>` | Unsigned integer, default 0. A nonzero value sets the hard-cap flag. Units are hundredths of a percent, so 10,000 represents 100%. |
| `Cpu.Weight` | `<cpu weight="5"/>` | Unsigned integer, default 0. A nonzero value selects weight-based scheduling. The Windows weight scale runs from 1 to 9. In C# the constructor order is `new CpuConfiguration(weight, maximumRate)`. Named arguments make the choice explicit. |
| `MemoryLimit` | `<memory limit="1073741824"/>` | Nullable unsigned 64-bit byte count. Omission skips the write. A present element with a missing or zero limit submits zero as the job-total-memory limit. |
| `SystemRoot` | `<systemroot path="C:\Windows"/>` | Nullable string. Sets the silo's job system-root information (class 45). An omitted child skips it, while a present child with no path supplies an empty string. The filesystem provider supplies any storage behind that path. |
| `KillOnLastHandleClose` | `<terminationpolicy>KillOnHandleClose</terminationpolicy>` | Defaults to `false`, corresponding to `PermanentQuiescent`. The true value selects the kernel kill-on-job-close limit. Caller-owned duplicate job handles, including the termination subscription's handle, can keep the job alive. |
| `EnableThreadImpersonation` | `<job threadimpersonation="true">` | Defaults to `false`. When true the provider submits byte 1 to job information class 47. Process identity at launch still follows the launcher's token rules. |

Choose one CPU policy for a new configuration. Rate and weight share a native union. With both nonzero, the port follows Wc by setting both flags and writing the weight last. An application cannot rely on both limits being independently enforced. [Job setup](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Native packet layouts](ports/windows-silos.md#native-argument-layouts) · [Microsoft CPU rate and weight definitions](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information)

## Network compartment

`NetworkCompartment` is a nullable string. `<network compartment="BuildA"/>` requests a **new** compartment with the description `BuildA`. The provider uses the job's identifier as the compartment GUID, then calls `SetJobCompartmentId` with the new numeric ID. The description accepts up to 256 UTF-16 code units and rejects embedded NUL. Creating the compartment supplies no adapter, IP address, or route to a remote share. For the build-service example that needs the host's normal routes, omit the section. [Network setup](../src/Containers.Windows.Silos/NativeApi.Providers.cs) · [Build-service example](windows-silos.md#run-two-builds-with-separate-file-and-mutex-views)

An omitted `network` produces `null`. An empty `<network/>` produces `""`. Both skip compartment creation. An explicitly empty section makes network cleanup eligible during creation rollback. Normal `Cleanup()` checks the job's current compartment and skips the default one. [Controller setup and rollback](../src/Containers.Windows.Silos/WindowsSilo.cs)

## Reading and composing descriptions

- Start with `new SiloDescription()` or parse `<container/>`. Its nine section properties start as `null`. Set the properties you need using record initializers or a `with` expression. Use `Containers.Windows.Silos.DescriptionModel` for the records and `Containers.Windows.Silos` for `SiloDescriptionXml` and `WindowsSilo`.
- A **present but empty** provider is a request. For instance, `NamedPipes = []` still initializes the NPFS silo view, and `<filesystem/>` requests WCI setup with default fields. For a bind-only workload, build `new FileSystemConfiguration { Bindings = […] }` and leave `Wci` null.
- XML names and recognized option tokens use ASCII case folding. Sections are singletons. Fields such as layers, directory entries, and pipe mappings can repeat where documented. Unknown elements or attributes and duplicate singleton sections produce a schema error. Parsing validates the description’s shape. Providers check their inputs during creation.
- Booleans accept `true` and `false`. Integer fields recognize decimal, leading-zero octal, and `0x` hexadecimal. For example, `011` means nine. Use unpadded decimal for byte counts and CPU settings. GUID attributes use the hyphenated form, for example `00112233-4455-6677-8899-aabbccddeeff`.

The public records also expose two distinct scopes outside creation. `RuntimeVirtualKey` belongs to the later `AddRuntimeVirtualKeys` operation. The Server 2025 standalone UnionFS APIs accept their own setup arguments. Neither adds another XML section. [XML parser](../src/Containers.Windows.Silos/SiloDescriptionXml.cs) · [Parser examples and edge cases](../tests/Containers.Windows.Silos.Tests/SiloDescriptionXmlTests.cs) · [2025 API differences](../src/Containers.Windows.Silos2025/README.md)
