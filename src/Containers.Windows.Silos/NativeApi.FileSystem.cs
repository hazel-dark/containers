using System.Buffers;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Containers.Windows.Silos.DescriptionModel;
using static Containers.Windows.Silos.NativeProviderConstants;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    public IFileSystemRegistration CreateFileSystemRegistration(JobSafeHandle job) => new FileSystemRegistration(this, job);

    public void CleanupFileSystem(JobSafeHandle job, string? volumePath)
    {
        if (string.IsNullOrEmpty(volumePath))
            return;

        _ = IsServer(job);
        var hr = DetachWciVolume(volumePath);
        if (hr < 0)
            SiloEventSource.Log.WciDetachFailed(volumePath, hr);
    }

    private sealed class FileSystemRegistration : IFileSystemRegistration
    {
        private readonly List<string> _installedBindings = [];
        private string? _detachPath;
        private readonly NativeApi api;
        private readonly JobSafeHandle _job;

        public FileSystemRegistration(NativeApi api, JobSafeHandle job)
        {
            this.api = api;
            _job = job;
        }

        public void Setup(FileSystemConfiguration configuration)
        {
            _installedBindings.EnsureCapacity(configuration.Bindings.Count);
            if (configuration.Wci is { } wci)
            {
                SetupWci(_job, wci);
                _detachPath = wci.DetachVolumePath ?? wci.ScratchRoot;
            }

            for (var i = 0; i < configuration.Batches.Count; i++)
                SetupBindBatch(_job, configuration.Batches[i], registerExceptions: i != 0);

            foreach (var binding in configuration.Bindings)
            {
                using var exceptions = new NativeStrings(binding.Exceptions);
                fixed (nint* p = exceptions.Pointers)
                    SiloException.CheckHResult(NativeMethods.BfSetupFilter(_job, binding.Flags, binding.Root, binding.Target, p, (uint)exceptions.Pointers.Length), "BfSetupFilter");

                _installedBindings.Add(binding.Root);
            }
        }

        public void Rollback()
        {
            // Undo completed setup steps if creation fails, including on a caller-owned job.
            List<Exception>? failures = null;
            for (var i = _installedBindings.Count - 1; i >= 0; i--)
            {
                var root = _installedBindings[i];
                try
                {
                    SiloException.CheckHResult(NativeMethods.BfRemoveMapping(_job, root), "BfRemoveMapping(" + root + ")");
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            try
            {
                api.CleanupFileSystem(_job, _detachPath);
            }
            catch (Exception error) when (failures is { Count: > 0 })
            {
                error.Data["BindMappingCleanupFailures"] = failures.AsReadOnly();
                throw;
            }

            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);

            if (failures is { Count: > 1 })
                throw new AggregateException("Bind mappings failed to clean up.", failures);
        }
    }

    private static void SetupWci(JobSafeHandle job, WciConfiguration configuration)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.IsolationMode, WcMaximumIsolationMode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(configuration.NestingMode, WcMaximumNestingMode);

        using var paths = new NativeStrings(configuration.Layers.Select(layer => layer.Path).ToArray());
        var data = new WciLayerDescriptor[paths.Pointers.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var layer = configuration.Layers[i];
            data[i] = new WciLayerDescriptor { Id = layer.Id, Flags = WcLayerFlags, Path = (char*)paths.Pointers[i] };
        }

        fixed (WciLayerDescriptor* d = data)
            SiloException.CheckHResult(NativeMethods.WcAttachFilterEx(job, configuration.IsolationMode, configuration.ScratchRoot, d, (uint)data.Length, configuration.NestingMode), "WcAttachFilterEx");
    }

    internal static int DetachWciVolume(string path)
    {
        var volume = stackalloc char[VolumeGuidPathCapacity];
        fixed (char* input = path)
        {
            if (!PInvoke.GetVolumeNameForVolumeMountPoint(input, volume, VolumeGuidPathCapacity))
                return SiloException.FromWin32(GetLastErrorOrDefault());
        }

        var name = new string(volume);
        if (name.Length == VolumeGuidPathLength && name[1] == '\\')
        {
            volume[1] = '?';
            name = new string(volume, 0, name.Length);
        }

        return PInvoke.FilterDetach("wcifs", name, "wcifs Instance").Value;
    }

    private static void SetupBindBatch(JobSafeHandle job, BindBatch batch, bool registerExceptions)
    {
        using var exceptions = new NativeStrings(batch.Exceptions);
        using var file = PInvoke.CreateFile(batch.ConfigurationPath, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ, FILE_SHARE_MODE.FILE_SHARE_READ, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0);
        if (file.IsInvalid)
            SiloException.CheckWin32(GetLastErrorOrDefault(), "CreateFileW(Bind batch)");

        var handle = (HANDLE)file.DangerousGetHandle();
        var size = PInvoke.GetFileSize(handle, null);
        if (size == PInvoke.INVALID_FILE_SIZE)
            SiloException.CheckWin32(GetLastErrorOrDefault(), "GetFileSize(Bind batch)");

        var buffer = size == 0 ? null : (byte*)NativeMemory.AllocZeroed(size);
        try
        {
            uint bytesRead;
            if (!PInvoke.ReadFile(handle, buffer, size, &bytesRead, null))
                SiloException.CheckWin32(GetLastErrorOrDefault(), "ReadFile(Bind batch)");

            // ReSharper disable once DisposeOnUsingVariable
            file.Dispose(); // The native provider closes its share-read handle before installing the batch.
            fixed (nint* paths = exceptions.Pointers)
            {
                var count = (uint)exceptions.Pointers.Length;
                if (registerExceptions)
                    SiloException.CheckHResult(NativeMethods.BfSetupFilterBatched(job, 0, buffer, size, WcBindBatchRegisterExceptions, count, paths), "BfSetupFilterBatched(exceptions)");

                SiloException.CheckHResult(NativeMethods.BfSetupFilterBatched(job, 0, buffer, size, WcBindBatchInstallMappings, count, paths), "BfSetupFilterBatched");
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    internal ref struct NativeStrings : IDisposable
    {
        internal nint[] Pooled { get; private set; }
        internal Span<nint> Pointers { get; private set; }

        internal NativeStrings(IReadOnlyList<string?> strings)
        {
            Pooled = ArrayPool<nint>.Shared.Rent(strings.Count);
            Pointers = Pooled.AsSpan(0, strings.Count);
            try
            {
                for (var i = 0; i < Pointers.Length; i++)
                {
                    var value = strings[i];
                    if (value is null)
                        continue;

                    var buffer = (char*)NativeMemory.Alloc((nuint)value.Length + 1, sizeof(char));
                    Pointers[i] = (nint)buffer;
                    value.CopyTo(new Span<char>(buffer, value.Length));
                    buffer[value.Length] = '\0';
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Pooled is null)
                return;
            
            ArrayPool<nint>.Shared.Return(Pooled);
            
            for (var i = 0; i < Pointers.Length; i++)
            {
                NativeMemory.Free((void*)Pointers[i]);
                Pointers[i] = 0;
            }

            this = default;
        }
    }
}
