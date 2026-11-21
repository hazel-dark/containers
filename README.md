# Direct Windows silos

To use an existing Wc XML description, call
`SiloDescriptionXml.Parse(File.ReadAllText(path))` and pass the result to
`Create` or `ConfigureJob`. For example, the BuildXL empty description is
`<container></container>`. You can add a managed Bind mapping to the parsed
description with a record `with` expression.

For a single-file override in a non-server silo, pass the visible filename first
and the backing filename second. This example assumes both files exist and the
caller has the required Windows privileges.

```csharp
using Containers.Windows.Silos;
using Containers.Windows.Silos.DescriptionModel;

var description = new SiloDescription
{
    Job = new JobConfiguration { KillOnLastHandleClose = true },
    FileSystem = new FileSystemConfiguration
    {
        Bindings = [new BindMapping(@"C:\Work\View\app.ini", @"C:\Work\Backing\app.ini")]
    }
};
using var silo = WindowsSilo.Create(description);
using var process = silo.LaunchApplication(@"cmd.exe /d /c type C:\Work\View\app.ini");
while (!silo.IsQuiescent)
    await Task.Delay(100);
silo.Cleanup();
```

For a server silo, supply the image's filesystem and virtual registry
configuration when calling `Create` or `ConfigureJob` with `server: true`.
Creation registers the silo with DAM or SMSS and checks the image against the
host policy. To start its operating-system session, keep an event handle open
and pass it to `BootServer`.

```csharp
using var deleted = new EventWaitHandle(false, EventResetMode.ManualReset);
using var silo = WindowsSilo.Create(serverDescription, server: true);
silo.BootServer(deleted.SafeWaitHandle);
```

`LaunchApplication` returns a process handle owned by the caller. Each explicit
`Cleanup` call queries the job identity and runs network, registry, then
filesystem cleanup. The job handle stays usable. Pass the volume mount point
to `Cleanup(volumePath)` to request WCIFS detach. A null or empty path skips
detach. Normal cleanup leaves explicit Bind roots alone.

If creation fails, rollback attempts to remove the Bind mappings already
installed and uses the WCI configuration’s `DetachVolumePath` or scratch path
for detachment. On disposal the controller runs null-path cleanup if no
explicit cleanup has succeeded, then releases its job reference in all cases.
If you own the job lifetime and its providers need no explicit cleanup, call
`DisposeWithoutProviderCleanup()` to release just the controller’s job reference.
A live job keeps its Bind Filter mappings until the job is destroyed.

A termination registration owns a duplicate of the job handle. Reading
`ReadAsync` consumes reasons dispatched by the native callback. Calling
`WaitForCallbacks` detaches its completion-port association and drains native
callbacks. Disposing the registration drains before release, then closes its
job duplicate. The library allows one active managed subscription per job,
including when two controllers hold different handles to the same job.
Wait for or dispose the first subscription before attaching another. Coordinate
with external completion-port users. The native registration retry can clear
an existing port association even when the retry fails. Close the subscription
when coordinating job last-handle termination.
