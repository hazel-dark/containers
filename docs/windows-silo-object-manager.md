# Windows silo Object Manager namespace

The Object Manager resolves names for kernel objects such as mutexes, events,
shared sections, devices, directories and symbolic links. An object directory is
a kernel data structure. When a file open reaches a volume or another device,
the filesystem or device driver interprets the rest of the path. The direct silo
library configures the Object Manager view through `SiloDescription.ObjectNamespace`.
Other parts of the description configure file projection, registry hives, drive
mounts and named pipes. [Windows Object Manager](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/windows-kernel-mode-object-manager) · [Object directories](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/object-directories) · [Silo description reference](windows-silo-description.md)

## Paths and their roles

| Path or name | Role |
|---|---|
| `\` and `\Silos\<id>` | The host Object Manager root is `\`. From the host, a configured silo root can appear under `\Silos`. A lookup inside the silo can start at that silo's root. Use `WindowsSilo.ObjectRoot` to get the actual host-side path. |
| `\BaseNamedObjects` | The directory used for host-wide Win32 named synchronization objects and sections. The session-0 application example creates a silo-local instance of this directory. |
| `\Sessions\<session-id>\BaseNamedObjects` | A session-specific directory for Win32 named objects. An interactive process can start a named-object lookup here. The process's session and the aliases it resolves determine the destination of `Global\Name` or `Local\Name`. |
| `\??`, `\DosDevices`, `\GLOBAL??` | DOS-device lookup uses the process or logon context's device map for aliases such as `C:`. Windows distinguishes local and global DOS-device mappings. A top-level `Global??` entry in the silo description also selects special root setup flags. |
| `\Device` | Contains named device objects exposed by drivers. A DOS-device alias can resolve to a device or volume here. The Server 2022 `wsc.def` pairs a local `GLOBAL??\UNC` alias to `\Device\Mup` with a global-scope `Mup` link under `Device`. MUP handles routing an ordinary UNC path to a network redirector. |
| `\Driver` and `\FileSystem` | These directories name driver objects, including filesystem drivers. Making a driver name visible in the silo does not mount a volume or redirect a file. File opens reach named devices and the filesystem serving them. |
| `\Device\NamedPipe` | For `\\.\pipe\Build`, the Object Manager resolves the pipe device and NPFS interprets the remaining pipe name. The `NamedPipes` provider configures mappings in NPFS. |
| `\RPC Control` | Local RPC commonly uses named ALPC port objects here. A named-pipe mapping applies to clients using the named-pipe transport. An ALPC client looks up its port through the object namespace. |
| `\KnownDlls` and `\KnownDlls32` | Object directories used by the Windows loader for section objects for known DLLs. The latter serves 32-bit processes on 64-bit Windows. |
| `\Registry` | The native registry namespace. The controller formats `RegistryRoot` as `\Registry\WC\Silo<job-guid>`. Registry hive stacks and key redirections come from the virtual registry provider. |

The root returned by `ObjectRoot`, the job GUID returned by `Identifier`, and the
Windows service session ID identify different things. The concurrent-writer
harness received `\Silos\624` and `\Silos\628` from `ObjectRoot`. Those are
example values from that run. [Native path queries](../src/Containers.Windows.Silos/NativeApi.cs) · [Windows kernel object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces) · [DOS-device lookup](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/local-and-global-ms-dos-device-names) · [Multiple UNC Provider](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/support-for-unc-naming-and-mup) · [Windows Internals object namespace examples](https://download.microsoft.com/download/1/4/0/14045a9e-c978-47d1-954b-92b9fd877995/97807356648739_samplechapters.pdf) · [Windows DLL search order](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-search-order)

## XML and managed configuration

`<ob>` is a child of `<container><namespace>`. Both `<ob>` and `<objdir>` may
contain repeated `<objdir>` and `<symlink>` elements. `<symlink>` has attributes
only. The XML enum choices below are case-insensitive. [XML reader](../src/Containers.Windows.Silos/SiloDescriptionXml.cs) · [Parser coverage](../tests/Containers.Windows.Silos.Tests/SiloDescriptionXmlTests.cs)

| Managed property | XML | Meaning |
|---|---|---|
| `ObjectNamespaceConfiguration.ShadowRoot` | `<ob shadow="…">` | Omitted or `false` leaves root shadowing off. `true` and `ifexists` both set the same Boolean flag. |
| `ObjectNamespaceConfiguration.Directories` | `<ob><objdir …/>` | Directories relative to the silo root. Defaults to an empty list. |
| `ObjectNamespaceConfiguration.Symlinks` | `<ob><symlink …/>` | Links relative to the silo root. Defaults to an empty list. |
| `ObjectDirectory.Name` | `<objdir name="…">` | Name passed relative to the containing object directory. Supply a name when creating a directory. |
| `ObjectDirectory.Shadow` | `<objdir shadow="…">` | `false` or omitted maps to `DirectoryShadow.None` (0), `true` to `Required` (1), and `ifexists` to `IfExists` (2). |
| `ObjectDirectory.SecurityDescriptorSource` | `<objdir clonesd="…">` | Optional host object-directory path for the security descriptor. An omitted or empty source lets Windows supply default security at creation. |
| `ObjectDirectory.Directories` | Nested `<objdir>` | Child directories. Defaults to an empty list. |
| `ObjectDirectory.Symlinks` | Nested `<symlink>` | Child links. Defaults to an empty list. |
| `ObjectSymlink.Name` | `<symlink name="…">` | Link name relative to the containing object directory. Supply a name when creating a link. |
| `ObjectSymlink.Target` | `<symlink path="…">` | Literal native object target, such as `\BaseNamedObjects`. |
| `ObjectSymlink.TargetSource` | `<symlink pathtoclone="…">` | Native path of an existing object link. Setup reads that link's target string and uses it as the new target. |
| `ObjectSymlink.Scope` | `<symlink scope="…">` | `local` or omitted maps to `SymlinkScope.Local` (0), `global` to `Global` (1), and `globaldropsilo` to `GlobalDropSilo` (2). |
| `ObjectSymlink.AccessMask` | `<symlink access_mask="…">` | Unsigned 32-bit link access-mask value. Omitted or zero skips the native mask-setting call. |

### Root and directory shadowing

Shadowing gives a silo directory a fallback directory for lookup. A local entry
takes precedence over a matching fallback entry. Root shadowing and shadowing of
an explicitly configured child directory are separate choices. For example, a
shadowed root can find names from the host root while an unshadowed
`BaseNamedObjects` supplies only its own members. [Object namespace setup](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Concurrent-writer control run](progress.md#two-concurrent-non-server-silo-writers-on-windows)

For a directory, `None` passes no shadow handle to `NtCreateDirectoryObjectEx`.
`Required` opens the same path on the host, built from the configured names of
the directory and its ancestors. A failed open stops setup. `IfExists` skips
that directory and its subtree if the host name or path is missing. Other
errors, including access denial, propagate. Setup creates or opens the named
directory with `OBJ_OPENIF`. Windows can return an existing directory in place
of creating one. Each directory's child directories are processed before its
links. [Object namespace setup](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Windows object attributes](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wudfwdm/ns-wudfwdm-_object_attributes)

The library first sets job information class 37. `ShadowRoot` adds flag `1`.
The first top-level directory named `Global??`, matched case-insensitively,
adds flag `2` and also adds flag `4` when its `Shadow` is `Required` or
`IfExists`. That special case configures the root's DOS-device directory and
its shadow. The library then queries the root's path, opens the directory, and
creates the configured descendants relative to it. [Root setup](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Native class-37 layout](ports/windows-silos.md#native-argument-layouts)

### Link scope and security

The provider creates each link under its configured parent. `Local` keeps normal
silo-relative resolution. `Global` marks the link for global resolution, as in
the server template's links to host device objects. `GlobalDropSilo` requests
the native drop-silo option for that link. At the native boundary, `Global` and
`GlobalDropSilo` set symbolic-link information class 1 with DWORD `0` and `1`
respectively. A nonzero `AccessMask` is passed separately via class 2. These
settings are applied to the specific object link. The Win32 `Global\`
prefix selects a named-object namespace through its own `Global` alias. Native
calls and template inspection establish the scope settings. Live results for
all scope and mask combinations are still open. [Link implementation](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Native argument tests](../tests/Containers.Windows.Silos.Tests/PlatformProviderTests.cs) · [HCS template findings](progress.md#hcs-description-xml-on-windows-server-2022)

Supply exactly one nonempty `Target` or `TargetSource`. Failure to open or query
the `TargetSource` logs `ObjectLinkSourceFailed` and skips that link. A link-name
collision leaves the existing link and its settings in place. Other creation,
scope-setting and mask-setting errors are reported to the caller. [Link implementation](../src/Containers.Windows.Silos/NativeApi.Objects.cs)

`clonesd` copies the source directory's owner, group, DACL and SACL. The clone
source can differ from the shadow source. For `IfExists`, a missing clone source
allows setup to continue without that descriptor. An access error still fails
setup. Links use the security descriptor read from the host Object Manager root
when available. The setup token needs permission to read these descriptors,
including `ACCESS_SYSTEM_SECURITY` for the SACL, and to create permanent objects.
The LocalSystem test enabled `SeSecurityPrivilege`. Windows requires
`SeCreatePermanentPrivilege` for permanent objects. `OBJ_PERMANENT` keeps a name
present after the library closes the setup handle. Access to any reachable
object, including a host object reached by fallback or a link, depends on the
caller's token and that object's access checks. [Security reads and object creation](../src/Containers.Windows.Silos/NativeApi.Objects.cs) · [Windows privileges](https://learn.microsoft.com/en-us/windows/win32/secauthz/privilege-constants) · [Windows object attributes](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wudfwdm/ns-wudfwdm-_object_attributes)

## Session-0 example and server-silo behavior

This is the Object Manager description used for the session-0 non-server silo
example. `Global` and `Local` are literal link names. Both use local scope and
point back to the silo's `BaseNamedObjects`, so `Global\BuildLock` resolves in
that silo. Any other named-object type using the same directory follows the
same path. The filesystem mapping in the complete example is configured
separately. [Complete build example](windows-silos.md#run-two-builds-with-separate-file-and-mutex-views) · [Windows view tests](../tests/Containers.Windows.Silos.Tests/WindowsViewTests.cs)

```xml
<container>
  <namespace>
    <ob shadow="true">
      <objdir name="BaseNamedObjects" clonesd="\BaseNamedObjects" shadow="false">
        <symlink name="Global" path="\BaseNamedObjects" scope="local" />
        <symlink name="Local" path="\BaseNamedObjects" scope="local" />
      </objdir>
    </ob>
  </namespace>
</container>
```

| Mode | Before configuring the object tree | Session used by `LaunchApplication` |
|---|---|---|
| Non-server | A present `<ob>`, including `<ob/>`, triggers the Wc assignment call `NtAssignProcessToJobObject` with the special process argument `-7`. An omitted `<ob>` skips that step and Object Manager setup. | The calling process's primary security context, without a session change. |
| Server | Registers the job through DAM or SMSS and then runs the same Object Manager provider for a supplied configuration. `BootServer` performs the separate server-initialization steps. | The server silo's assigned service session. The library duplicates the caller's token and sets that session ID for launch. |

In both modes the process starts in the job through the process-creation job
list. Name resolution applies to new named opens. An existing or inherited
handle retains its reference to the original object. In the traced Server 2022
HCS flow, the server base template `wsc.def` has root shadowing off and declares
an unshadowed `BaseNamedObjects` with a cloned descriptor and zero child
elements. It declares global-scope root links for `FileSystem`, `Driver` and
`Registry`, and global-scope links under `Device` for selected host devices
such as `ConDrv` and `Mup`. The local `GLOBAL??\UNC` alias described above is
part of the same template. The non-server base template `helium.def` is empty.
These trees come from HCS template choices. The direct library takes its tree
from the supplied description in either mode. HCS adds settings for each request
before handing its description to Wc. [Mode setup](../src/Containers.Windows.Silos/WindowsSilo.cs) · [Process launch](../src/Containers.Windows.Silos/NativeApi.Process.cs) · [HCS template findings](progress.md#hcs-description-xml-on-windows-server-2022)

On Server 2022 build 20348.587, two native session-0 workers held mutexes with the
same `Global\` name concurrently under the example configuration. The control
run without `<ob>` showed contention on the host mutex. A native child also
passed the private-mutex check on Server 2025 build 26100.32230. A PowerShell
child timed out before readiness with the private directory on both OS families,
using either the managed port or the inbox Wc implementation. The recorded
checks cover these named opens. Interactive-session lookup, full server boot,
and host RPC or domain authentication remain open. [Server 2022 results](progress.md#two-concurrent-non-server-silo-writers-on-windows) · [Server 2025 results](../src/Containers.Windows.Silos2025/README.md#windows-verification)
