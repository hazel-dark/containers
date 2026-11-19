# Windows silo registry, device and host-file options

These records belong to `Containers.Windows.Silos.DescriptionModel`. The Server 2022 and Server 2025 projects share them and the XML reader. The XML sections below go under `<container><namespace>`. An omitted section leaves its `SiloDescription` property `null`. An empty section creates a configuration with the defaults shown below. The XML reader accepts omitted fields even when the native operation needs a usable path or other resource. Use the main reference for scalar formats and the rest of the description. [Description reference](windows-silo-description.md) · [Shared reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs) · [2025 project](../src/Containers.Windows.Silos2025/Containers.Windows.Silos2025.csproj)

## Virtual registry

`SiloDescription.Registry` accepts a `RegistryConfiguration`. `<registry>` has no attributes. Its children are repeatable and populate three lists, each empty by default. Both server and non-server creation register the job with `\Device\VRegDriver` when this section is present, including `<registry/>`. An omitted registry skips that setup step. The server image check reads the image's virtual SOFTWARE and SYSTEM roots when it runs. [Registry configuration](../src/Containers.Windows.Silos/DescriptionModel/RegistryConfiguration.cs) · [Creation order](../src/Containers.Windows.Silos/WindowsSilo.cs#L170) · [Virtual registry provider](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L19) · [Server image reads](../src/Containers.Windows.Silos/NativeApi.ServerImage.cs#L34)

| Property | XML child | Setup order |
| --- | --- | --- |
| `HiveStacks` | `<hivestack>` | 1. Mount layers and apply key edits. |
| `Redirections` | `<redirectionnode>` | 2. Install virtual namespace mappings. |
| `Symlinks` | `<symlink>` | 3. Create registry symbolic links. |

The provider walks each list in its own order. XML interleaving between lists has no effect on these stages. [Registry reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L242) · [Virtual registry provider](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L19)

### Hive stacks and layers

| `RegistryHiveStack` property | XML in `<hivestack>` | Default | Where used |
| --- | --- | --- | --- |
| `Name` | `hive` attribute | `""` | Constructor argument. Identifies the stack for redirections and supplies the literal suffix for mounted layer paths. |
| `Hives` | Repeated `<layer>` | Empty list | Ordered from the backing layer to the final layer. The last layer's registry path becomes the stack root. |
| `Keys` | Repeated `<mkkey>` | Empty list | Open or create keys relative to the final stack root. |
| `DeleteKeys` | Repeated `<delkey name="…"/>` | Empty list. An omitted `name` yields `""`. | Recursively delete each relative key after the creation and value writes. A key that returns `STATUS_OBJECT_NAME_NOT_FOUND` on open is skipped. |

An existing hive is a live registry path. With an existing hive as the final layer, the provider applies key edits relative to that path. Even with no edits, setup opens the final root. Supply layers that resolve to a usable registry root when creating a silo. The stack lookup lowercases each UTF-16 code unit and compares the resulting names ordinally. Later entries with the same normalized name supply the target for subsequent redirections. The mounted path preserves the configured name's case. [Hive stack record](../src/Containers.Windows.Silos/DescriptionModel/RegistryHiveStack.cs) · [Stack setup](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L49)

| `RegistryHive` property | XML in `<layer>` | Accepted type and default | Where used |
| --- | --- | --- | --- |
| `ExistingPath` | `hosthive` attribute | NT registry path. `null` when omitted. | A nonempty path supplies an existing hive as the current layer. The next file-backed layer uses that path with the driver flag for an existing backing hive. |
| `FilePath` | `filepath` attribute | File path. `""` when omitted. | File passed to the driver's differencing-hive load request when `ExistingPath` is empty or absent. |
| `MountId` | `identifier` attribute | Unbraced `8-4-4-4-12` GUID. `Guid.Empty` when omitted. | File-backed layer mount identifier. |
| `ReadOnly` | `readonly` attribute | Boolean, `false` | Adds `REG_OPEN_READ_ONLY` to the hive-load options. |
| `Immutable` | `immutable` attribute | Boolean, `false` | Adds `REG_IMMUTABLE` to the hive-load options. |
| `InheritTrust` | `inherittrustclass` attribute | Boolean, `false` | Sets the differencing-load driver's inherit-trust flag. |
| `TrustedHive` | `trustedhive` attribute | Boolean, `false` | Sets the differencing-load driver's trusted-hive flag. |
| `WriteThrough` | `writethrough` attribute | Boolean, `false` | Sets the differencing-load driver's write-through flag. |
| `FileAccessToken` | `<fileaccesstoken>` text | `ulong`, `0` | Passed as the native token handle in the hive-load packet. |

`hosthive` takes precedence when nonempty. In that branch the provider uses the existing path and bypasses the file-load request. In the file-backed branch it always includes `REG_HIVE_NO_RM` and passes the preceding layer's key path if there was one. The mounted key path concatenates `\Registry\WC\Silo`, the lowercase, unbraced `MountId` and the literal `Name`. The managed hive-load request limits each resulting key path and the file path to 32,767 UTF-16 code units. [Hive record](../src/Containers.Windows.Silos/DescriptionModel/RegistryHive.cs) · [Hive XML reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L298) · [Load request and options](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L56) · [Path formatter](../src/Containers.Windows.Silos/NativeApi.cs#L153)

### Key and value edits

| `RegistryKeyConfiguration` property | XML in `<mkkey>` | Accepted type and default | Where used |
| --- | --- | --- | --- |
| `Name` | `name` attribute | Key name relative to its parent. `""` when omitted. | Constructor argument. Top-level keys use the final stack root as parent. |
| `SecurityDescriptorSource` | `clonesd` attribute | NT registry path. `null` when omitted. | A nonempty path supplies security for key creation. An empty or absent path skips the security-source lookup. |
| `Volatile` | `volatile` attribute | Boolean, `false` | Requests `REG_OPTION_VOLATILE` when true and `REG_OPTION_NON_VOLATILE` otherwise. A persistent child under a volatile key fails at setup with `E_INVALIDARG`. |
| `Keys` | Repeated `<mkkey>` | Empty list | Recursively open or create child keys before writing the parent's values. |
| `Values` | Repeated `<mkvalue>` | Empty list | Write values on the current key with `NtSetValueKey`. |

The XML `mkvalue` element uses `name` for `RegistryValue.Name`. Omit it or use `name=""` for the default value of the key. The selected payload determines `Type` and `Data`. [Key record](../src/Containers.Windows.Silos/DescriptionModel/RegistryKeyConfiguration.cs) · [Key setup](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L203) · [Value reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L341)

| XML payload attribute | `RegistryValue.Type` | Encoding written in `Data` |
| --- | --- | --- |
| `data_binary` | `REG_BINARY` (3) | Even-length ASCII hex, two digits per byte. Empty hex writes a zero-byte binary value. |
| `data_dword` | `REG_DWORD` (4) | A parsed `uint` written as four little-endian bytes. |
| `data_multistring` | `REG_MULTI_SZ` (7) | Literal UTF-16LE text plus one NUL code unit. Punctuation, including semicolons, stays in the text. |
| `data_string` | `REG_SZ` (1) | UTF-16LE text plus one NUL code unit. |

When several payload attributes are present, the reader validates each one and selects the first in the table. Omitting all four creates a `RegistryValue` with `HasPayload = false`, `Type = REG_NONE` and empty `Data`. The managed provider throws `InvalidOperationException` when setup encounters it. Use a typed payload for key edits. [Value reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L341) · [Payload validation](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L239)

In C#, `new RegistryValue(name, type, data)` takes the raw native type and bytes and sets `HasPayload = true`. `String` and `Dword` encode the corresponding types above. `MultiString` joins an `IEnumerable<string>` with NUL code units and adds two final NUL code units. For example, `RegistryValue.MultiString("SearchRoots", [@"D:\First", @"D:\Second"])` creates a value with two strings. `WithoutPayload(name)` represents the omitted XML payload state. [Value record and factories](../src/Containers.Windows.Silos/DescriptionModel/RegistryValue.cs)

### Redirections and symbolic links

| `RegistryRedirection` property | XML in `<redirectionnode>` | Accepted type and default | Where used |
| --- | --- | --- | --- |
| `Path` | `containerpath` | Silo-side NT registry path. `""` when omitted. | Constructor argument. Path registered with the virtual registry driver. |
| `Target` | `hostpath` | Backing NT registry path. `null` when omitted. | Target sent to the driver if `HiveStack` is empty or absent. A null target becomes `""` in the request. |
| `HiveStack` | `hivestack` | Name of a stack in this section. `null` when omitted. | A nonempty name selects that stack's final key path and takes precedence over `Target`. A missing named stack fails at setup with `E_INVALIDARG`. |
| `AccessMask` | `access_mask` | Raw `uint`, `0` | Passed to the driver as supplied, including zero. |
| `Trusted` | `trustedhive` | Boolean, `false` | Sets the driver's trusted-redirection flag. |
| `ExitNode` | `exitnode` | Boolean, `false` | Sets the driver's exit-node flag. |

Redirection requests accept at most 32,767 UTF-16 code units per path, including the resolved stack path when `HiveStack` supplies the target. `RegistrySymlink(path, target)` takes two NT registry paths. `<symlink key="…" target="…"/>` assigns `key` to `Path` and `target` to `Target`. Both default to `""` if omitted in XML. The provider creates a volatile symbolic-link key, sets its `SymbolicLinkValue` to the target's UTF-16LE bytes as `REG_LINK`, and deletes and retries if creation opens an existing key. [Redirection record](../src/Containers.Windows.Silos/DescriptionModel/RegistryRedirection.cs) · [Link record](../src/Containers.Windows.Silos/DescriptionModel/RegistrySymlink.cs) · [Registry actions](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L19) · [Link creation](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L258)

### Registry example

This description shows a file-backed stack with a private edit and a redirection by name. Supply a prepared writable hive at the example path, a mount GUID for the layer, and the namespace paths and access mask for your application. A bootable server image needs its full filesystem and registry setup. [Silo creation and boot](windows-silos.md#what-happens-during-creation-and-server-boot)

```xml
<container>
  <namespace>
    <registry>
      <hivestack hive="BuildSettings">
        <layer filepath="\??\D:\SiloScratch\BuildSettings.hiv"
               identifier="00112233-4455-6677-8899-aabbccddeeff" />
        <mkkey name="Worker">
          <mkvalue name="Mode" data_string="isolated" />
          <mkvalue name="Parallelism" data_dword="4" />
        </mkkey>
      </hivestack>
      <redirectionnode containerpath="\Registry\Machine\Software\BuildSettings"
                       hivestack="BuildSettings" access_mask="0xffffffff" />
    </registry>
  </namespace>
</container>
```

### Runtime virtual keys

`RuntimeVirtualKey(path, target, accessMask = 0)` is a managed-only record for `WindowsSilo.AddRuntimeVirtualKeys`. `Path` is the silo-side NT registry path, `Target` is the backing NT registry path, and `AccessMask` is the raw `uint` sent to the driver. The call submits a batch on an existing silo and sets the node flags to zero. Each path may contain up to 32,767 UTF-16 code units. `RegistryRoot` and `GetRegistryRootPath(suffix)` query the root path, with the supplied suffix appended verbatim. `OpenComRegistryRoot()` returns a caller-owned registry handle. `SetRegistryFlushEnabled(bool)` updates the job's driver flag controlling flushing. [Runtime record](../src/Containers.Windows.Silos/DescriptionModel/RuntimeVirtualKey.cs) · [Controller methods](../src/Containers.Windows.Silos/WindowsSilo.cs#L244) · [Runtime request layout](../src/Containers.Windows.Silos/NativeApi.Registry.cs#L155)

## Device metadata

`SiloDescription.Device` accepts a `DeviceConfiguration`. The parser retains all device declarations, including `RequiresDriverStore`, as metadata. The direct Wc creation path leaves the record unused in both silo modes. [Device record](../src/Containers.Windows.Silos/DescriptionModel/DeviceConfiguration.cs) · [Device reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L370)

| Record and property | XML | Accepted type and default |
| --- | --- | --- |
| `DeviceConfiguration.RequiresDriverStore` | `<device requires_driverstore="…">` | Boolean, `false`. |
| `DeviceConfiguration.Categories` | Repeated `<device_category>` inside `<device>` | Empty list. |
| `DeviceConfiguration.Instances` | Repeated `<device_instance>` inside `<device>` | Empty list. |
| `DeviceCategory.Name` | `device_category` attribute `name` | String, `""` when omitted. |
| `DeviceCategory.InterfaceClasses` | Repeated `<interface_class>` inside `<device_category>` | Empty list. |
| `DeviceInstance.Id` | `device_instance` attribute `id` | String, `""` when omitted. |
| `DeviceInstance.LocationPath` | `device_instance` attribute `location_path` | String, `""` when omitted. |
| `DeviceInstance.PortName` | `device_instance` attribute `port_name` | String, `""` when omitted. |
| `DeviceInstance.InterfaceClasses` | Repeated `<interface_class>` inside `<device_instance>` | Empty list. |
| `DeviceInterfaceClass.Type` | `interface_class` attribute `type` | `inherit` (0, default), `deviceinstance` (1), or `classguid` (2). The managed enum members are `Inherit`, `DeviceInstance`, and `ClassGuid`. |
| `DeviceInterfaceClass.Id` | `interface_class` attribute `identifier` | Unbraced `8-4-4-4-12` GUID. `Guid.Empty` when omitted. |
| `DeviceInterfaceClass.Recurse` | `interface_class` attribute `recurse` | Boolean, **`true`** when omitted in XML. The managed constructor takes an explicit `bool`. |

The reader accepts the instance strings independently and parses a provided interface GUID for any `type`. C# callers pass the name and list to `DeviceCategory`, the three strings and list to `DeviceInstance`, and type, GUID, and recursion flag to `DeviceInterfaceClass`. [Category](../src/Containers.Windows.Silos/DescriptionModel/DeviceCategory.cs) · [Instance](../src/Containers.Windows.Silos/DescriptionModel/DeviceInstance.cs) · [Interface class](../src/Containers.Windows.Silos/DescriptionModel/DeviceInterfaceClass.cs) · [Interface class type](../src/Containers.Windows.Silos/DescriptionModel/DeviceInterfaceClassType.cs)

```xml
<container>
  <namespace>
    <device requires_driverstore="true">
      <device_category name="Example">
        <interface_class type="classguid" identifier="00112233-4455-6677-8899-aabbccddeeff" />
      </device_category>
      <device_instance id="ExampleDevice" location_path="PCI(1)" port_name="COM1">
        <interface_class type="deviceinstance" recurse="false" />
      </device_instance>
    </device>
  </namespace>
</container>
```

## Downlevel host-file mappings

`SiloDescription.Downlevel` accepts a `DownlevelConfiguration`. `<hostfiles>` requests a server-creation pass that uses Bind Filter read-only mappings to overlay selected files in the prepared image with files from the host Windows directory. Non-server creation leaves the configuration unused. [Host-file record](../src/Containers.Windows.Silos/DescriptionModel/DownlevelConfiguration.cs) · [Host-file reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs#L406) · [Creation dispatch](../src/Containers.Windows.Silos/WindowsSilo.cs#L185)

| Property | XML in `<hostfiles>` | Accepted type and default | Where used |
| --- | --- | --- | --- |
| `BaseImagePath` | `base_image_path` attribute | String, `null` when omitted. | Retained as description metadata. |
| `ContainerRoot` | `sandbox_path` attribute | Host-side Windows path of the prepared sandbox root. `""` when omitted. | Constructor argument. The provider ensures a trailing backslash, attaches Bind Filter at that path, and uses its `Windows\` child for mapping destinations. |
| `HostFiles` | Repeated `<file>` text | Paths relative to the Windows directory. Empty list when omitted. | Constructor argument. Each entry names both the source under the host Windows directory and the destination under the sandbox's `Windows\` directory. The provider concatenates these paths directly. |

An empty `HostFiles` list skips the mapping pass. For each listed source, the provider also reads its `$CI.CatalogHint` extended attribute and adds the named catalog under `System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}\` when a hint is present. Duplicate catalog names are emitted once using case-sensitive comparison. The provider skips failed existence probes and tolerates file-not-found and path-not-found results from `BfSetupFilter`. Other failed bindings stop the pass. [Host-file implementation](../src/Containers.Windows.Silos/NativeApi.ServerImage.cs#L151) · [Host-file tests](../tests/Containers.Windows.Silos.Tests/HostFileFixupTests.cs)

| Host edition | When it runs | Existence probe and host condition |
| --- | --- | --- |
| Server 2022 | During creation, after validating the host and image versions. That validation also runs when `<hostfiles>` is omitted. | Skips the pass on `UtilityVM` hosts. Probes the destination with `FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY`. |
| Server 2025 | During creation. The image compatibility gate runs later during `BootServer`. | Probes both destination and source. Utility VMs go through the same pass. |

The `<hostfiles>` section controls the mappings. Host registry policy and image registry values govern build and minimum-revision checks. [2022 server lifecycle](../src/Containers.Windows.Silos/ServerLifecycle.cs) · [2025 server lifecycle](../src/Containers.Windows.Silos2025/ServerLifecycle.cs) · [2025 boot gate call](../src/Containers.Windows.Silos2025/WindowsSilo.Initialize.cs)

For example, this section makes `System32\ntdll.dll` a candidate for a read-only mapping from the host Windows directory into `D:\SiloScratch\Windows\System32\ntdll.dll`. Add the section to the description for a prepared server silo. `sandbox_path` names the sandbox root above `Windows`. [Host-file path handling](../src/Containers.Windows.Silos/NativeApi.ServerImage.cs#L151)

```xml
<container>
  <namespace>
    <hostfiles sandbox_path="D:\SiloScratch">
      <file>System32\ntdll.dll</file>
    </hostfiles>
  </namespace>
</container>
```

The grammar is recorded against `container.dll` 10.0.20348.1 and 10.0.26100.32230. The manifests identify the retained XML descriptors, provider instruction checks, and verification limits. Tests cover managed parsing, registry packet layouts, and host-file selection. Full server OS boot remains unverified. [XML tests](../tests/Containers.Windows.Silos.Tests/SiloDescriptionXmlTests.cs) · [Registry layout tests](../tests/Containers.Windows.Silos.Tests/NativeContractTests.cs)
