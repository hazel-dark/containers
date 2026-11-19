using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32.Foundation;
using Windows.Win32.System.Kernel;
using Containers.Windows.Silos.DescriptionModel;
using static Containers.Windows.Silos.NativeProviderConstants;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    public void SetupNetwork(nint job, string name)
    {
        if (name.Length >= CompartmentDescriptionCapacity || name.Contains('\0'))
            throw new ArgumentException("A compartment name must fit the native 257-character buffer.", nameof(name));

        _platform.InitializeCompartment(out var entry);
        entry.Guid = GetId(job);

        var description = new Span<char>(entry.Description, CompartmentDescriptionCapacity);
        name.AsSpan().CopyTo(description);
        description[name.Length] = '\0';
        entry.Flags |= WcCompartmentCreationFlag;
        SiloException.CheckWin32((int)_platform.CreateCompartment(&entry), "CreateCompartment");

        try
        {
            SiloException.CheckWin32((int)_platform.SetCompartment(job, entry.Id), "SetJobCompartmentId");
        }
        catch (Exception error)
        {
            var status = _platform.DeleteCompartment(entry.Id);
            if (status != 0)
            {
                SiloEventSource.Log.NetworkCleanupFailed(entry.Id, "DeleteCompartment", status);
                error.Data["DeleteCompartmentError"] = status;
            }

            throw;
        }
    }

    public void CleanupNetwork(nint job)
    {
        var id = _platform.GetCompartment(job);
        if (id == (uint)COMPARTMENT_ID.DEFAULT_COMPARTMENT_ID)
            return;

        var unbind = _platform.SetCompartment(job, (uint)COMPARTMENT_ID.UNSPECIFIED_COMPARTMENT_ID);
        if (unbind != 0)
            SiloEventSource.Log.NetworkCleanupFailed(id, "SetJobCompartmentId", unbind);

        var delete = _platform.DeleteCompartment(id);
        if (delete != 0)
            SiloEventSource.Log.NetworkCleanupFailed(id, "DeleteCompartment", delete);
    }

    public void SetupMounts(nint job, IReadOnlyList<DriveMapping> mounts)
    {
        const uint mountAccess = (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE);
        var jobHandle = new HANDLE(job);
        using (var host = _platform.OpenDevice(@"\\.\MountPointManager", mountAccess, WcMountManagerTemplateHandle))
            Ioctl(host, IoctlMountMgrSiloArrival, jobHandle.AsBytes());

        foreach (var mount in mounts)
        {
            var root = GetObjectRoot(job);
            var linkName = @"\DosDevices\" + _platform.UpperCharacter(mount.Drive) + ":";
            using var device = _platform.OpenDevice(@"\\?\" + root + @"\Device\MountPointManager", mountAccess, WcMountManagerTemplateHandle);

            Ioctl(device, IoctlMountMgrVolumeArrivalNotification, Pack.MountTarget(mount.Target));

            var point = Pack.MountPoint(linkName, mount.Target);
            // SetVolumeNameForRoot treats ERROR_INVALID_PARAMETER (0x57) as accepted.
            Ioctl(device, IoctlMountMgrCreatePoint, point, acceptedError: (int)WIN32_ERROR.ERROR_INVALID_PARAMETER);
        }
    }

    public void SetupPipes(nint job, IReadOnlyList<PipeMapping> pipes)
    {
        var arrival = new FILE_PIPE_SILO_ARRIVAL_INPUT { JobHandle = new HANDLE(job) };
        using (var host = _platform.OpenDevice(@"\\.\pipe", (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE, 0))
            SiloException.CheckNt(_platform.FsControl(host, Wdk.FSCTL_PIPE_SILO_ARRIVAL, arrival.AsBytes()), "NtFsControlFile(0x118048)");

        if (pipes.Count == 0)
            return;

        using var device = _platform.OpenDevice(@"\\?\" + GetObjectRoot(job) + @"\Device\NamedPipe", (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE, 0);
        var maximumNameLength = (ushort.MaxValue - sizeof(FILE_PIPE_CREATE_SYMLINK_INPUT)) / sizeof(char);
        foreach (var pipe in pipes)
        {
            if (pipe.Name.Length > maximumNameLength || pipe.Target.Length > ushort.MaxValue / sizeof(char))
                SiloException.CheckNt(NTSTATUS.STATUS_INVALID_PARAMETER.Value, "Build NPFS symbolic link");

            var data = Pack.PipeSymlink(pipe.Name, pipe.Target);
            SiloException.CheckNt(_platform.FsControl(device, Wdk.FSCTL_PIPE_CREATE_SYMLINK, data), "NtFsControlFile(0x11004c)");
        }
    }

    internal partial class PlatformProvider
    {
        internal virtual char UpperCharacter(char value) => char.ToUpperInvariant(value);
        internal virtual uint CreateCompartment(CompartmentEntry* entry) => NativeMethods.CreateCompartment(entry);
        internal virtual uint GetCompartment(nint job) => NativeMethods.GetJobCompartmentId(job);
        internal virtual uint SetCompartment(nint job, uint id) => NativeMethods.SetJobCompartmentId(job, id);
        internal virtual uint DeleteCompartment(uint id) => NativeMethods.DeleteCompartment(id);

        internal virtual void InitializeCompartment(out CompartmentEntry entry)
        {
            fixed (CompartmentEntry* p = &entry)
                NativeMethods.InitializeCompartmentEntry(p);
        }

        internal virtual int FsControl(SafeFileHandle handle, uint code, Span<byte> input)
        {
            var borrowed = false;
            try
            {
                handle.DangerousAddRef(ref borrowed);
                fixed (byte* p = input)
                    return NativeMethods.NtFsControlFile(handle.DangerousGetHandle(), 0, 0, 0, out _, code, p, (uint)input.Length, null, 0);
            }
            finally
            {
                if (borrowed)
                    handle.DangerousRelease();
            }
        }
    }
}
