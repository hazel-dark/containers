# Windows Server 2025 silos

For application and server silo examples, provider internals, and cleanup, see the [Windows silo guide](../../docs/windows-silos.md).

Reference the following project for Server 2025 x64. It has the same
`Containers.Windows.Silos` namespace and managed ownership conventions as
the Server 2022 project. An application should reference one assembly for
its target host. Both implement Wc operations using their underlying Windows
APIs. The native XML oracle in the test project calls the inbox DLL directly.

The reference is Standard Evaluation Server Core, install WIM index 1,
`container.dll` 10.0.26100.32230. The reconstruction covers the 21 named
`Wc*` exports. The two assemblies share the source for common paths.
They have separate server lifecycle contracts. Select the project for the
host OS even when that host will boot an older image.

| Operation | Server 2022 (20348.1) | Server 2025 (26100.32230) |
|---|---|---|
| Create a server silo | Configures providers and validates image compatibility before host-file injection | Configures providers and applies host-file fixups. The image gate runs during boot |
| Boot a server silo | Class 40 uses a deletion event and a zero downlevel byte | Checks host and image minimum revisions, allows builds 20348 and 26100 by default, then submits the image build as a DWORD at offset 8 |
| Detect server mode | Class 38 with a 32-byte buffer | Class 38 with a 40-byte buffer |
| Termination callback | A server system-process-terminated status produces reason 1 | The same status produces reason 3 |
| Host-file fixups | Skips utility VMs and checks the destination | Checks both destination and host source |
| Standalone filesystem | No standalone export | UnionFS create, migrate, and remove. Ordinary silo creation still selects WCIFS |
| Query a job | Service-session query exists internally | Exposes the service session ID and a first-boot query based on lifetime total processes |

The two new job queries are `WindowsSilo.ServiceSessionId` and
`WindowsSilo.IsFirstBoot`. The former preserves the unassigned DWORD value.
The latter can remain false after every process has exited. For standalone
filesystem operations use `SiloFileSystemNamespace.Setup` and `Cleanup`.
Creating a fresh union initializes five offline delta hives. Migrating a
union uses the tombstone configuration under the scratch root.

The XML descriptor tables match across the two builds (30 classes and
98 field records). The project shares the XML model and reader. The
native C++ parsed description remains opaque and build-specific.
The 17 inherited Wc exports all have different ordinals on Server 2025.

[Export mappings and pinned inputs](../../sources/ports/windows-silos2025.json)
· [Server 2022 API and managed adaptations](../Containers.Windows.Silos/README.md)
· [Implementation plan](../../docs/plans/05-wc-native.md)

## Windows verification

On Server Core 26100.32230 with the Containers feature enabled, the inbox
DLL and the managed port gave the same results for application and server
silo creation, job queries, and cleanup. A native child observed the
single-file binding and private global mutex namespace through both
implementations. The PowerShell child timed out waiting to publish readiness
under that same private namespace in both implementations.

On independent scratch directories, both implementations created five
delta hives, projected the layer marker, persisted a deletion on removal,
and restored the same view on migration. A Windows regression test also
forces a missing Bind batch after migration, checks the tombstone file was
restored, and retries successfully. The standalone fresh path uses the
System32 offreg exports for the offline hive operations embedded in Wc.
The installed offreg and UnionFSApi hashes are recorded in the manifest.

The managed cleanup entry rejects an invalid mount GUID with a recoverable
exception. The native cleanup terminates its caller on this input. A failed
offline hive close likewise stops managed setup and reports the status.
Full server OS boot across the two allowed image builds remains unverified.
Earlier managed limits on oversized object, mount, hive, and network names
are recorded in the provider review. Driver-side behavior beyond the tested
configurations remains open.

The final 2025 LocalSystem run passed 190 of 191 tests. The PowerShell
readiness timeout described above was the sole failing test. The portable
suites passed 179 tests for 2025 and 166 for 2022. The win-x64 Release build
completed with zero warnings and errors.
