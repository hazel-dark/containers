// BuildXL ConfigureWciFilter, (c) Microsoft Corporation, MIT. See THIRD-PARTY-NOTICES.txt.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Containers.Windows.Silos;

/// <summary>A sparse, copy-on-write directory view belonging to a Windows silo job.</summary>
[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
public static unsafe partial class WcifsWorkspace
{
    private const uint InheritSecurity = 1 << 4;
    private const uint BindUseCurrentSiloMapping = 0x4; // hcsshim internal/winapi/bindflt.go
    private const uint Inner = 0;
    private const uint SparseHard = 3;
    private const int SetupAttempts = 5;

    /// <summary>Creates a private copy-on-write workspace over an existing directory.</summary>
    /// <returns>The workspace directory to pass to <see cref="OpenMapped"/> or reclaim after job teardown.</returns>
    public static string CreateMapped(SafeHandle siloJob, string mappedDirectory, string scratchParent)
        => CreateMapped(siloJob, mappedDirectory, scratchParent, []);

    /// <summary>Overlays the existing mapped directory in this silo. Reads search that directory
    /// first and then the additional lowers in order. Writes and new files go to private scratch.</summary>
    /// <returns>The workspace directory to pass to <see cref="OpenMapped"/> or reclaim after job teardown.</returns>
    /// <remarks>Configure a fresh job before launching workers. The caller owns the job and scratch
    /// lifetime. Restrict access to the scratch parent and all workspace backing paths. If setup fails,
    /// the exception can carry the scratch path in Data["WcifsScratchPath"].</remarks>
    public static string CreateMapped(SafeHandle siloJob, string mappedDirectory, string scratchParent, params ReadOnlySpan<string> additionalLowerDirectories)
    {
        ArgumentNullException.ThrowIfNull(mappedDirectory);

        using var job = BorrowJob(siloJob);
        var parent = GetDirectory(scratchParent);
        var paths = new string[1 + additionalLowerDirectories.Length];
        paths[0] = GetDirectory(mappedDirectory);
        for (var i = 0; i < additionalLowerDirectories.Length; i++)
            paths[i + 1] = GetDirectory(additionalLowerDirectories[i]);

        CheckScratchParent(parent, paths);
        EnsureFiltersLoaded();

        var scratch = Path.Combine(parent, "wcifs-" + Guid.NewGuid().ToString("N"));
        SetUpWorkspace(job, scratch, paths);
        return scratch;
    }

    private static void SetUpWorkspace(JobSafeHandle job, string scratch, string[] paths)
    {
        CreateDirectoryExclusive(scratch);

        var view = Path.Combine(scratch, ViewName);
        string? directories = null;
        var registered = false;
        try
        {
            Directory.CreateDirectory(view);
            if (paths.Length > 1)
            {
                directories = Path.Combine(scratch, SupplementalName);
                BuildDirectoryLayer(directories, paths);
                paths = [.. paths, directories];
            }

            var layerIds = new Guid[paths.Length];
            for (var i = 0; i < layerIds.Length; i++)
                layerIds[i] = Guid.NewGuid();

            PrepareMappedWorkspace(scratch, view, paths, layerIds);

            var root = new WcifsRootMarker { LayerId = layerIds[0] };
            ThrowForFailure(WciSetReparsePointData(view, in root, (ushort)sizeof(WcifsRootMarker)), "WciSetReparsePointData");
            RegisterNativeWorkspace(job, view, paths, layerIds);
            registered = true;
            BindView(job, paths[0], view);
            PublishManifest(scratch);
        }
        catch (Exception ex)
        {
            ex.Data["WcifsScratchPath"] = scratch;
            if (!registered)
            {
                try
                {
                    // Delete the view itself without walking the WCIFS projection.
                    if (Directory.Exists(view))
                        Directory.Delete(view);

                    RemovePackages(scratch);

                    File.Delete(Path.Combine(scratch, PendingManifestName));
                    if (directories != null && Directory.Exists(directories))
                        Directory.Delete(directories, recursive: true);

                    Directory.Delete(scratch);
                }
                catch (Exception cleanup)
                {
                    ex.Data["WcifsScratchCleanup"] = cleanup;
                }
            }

            throw;
        }
    }

    /// <summary>Registers an existing mapped workspace on a fresh silo job. The application
    /// continues to open the original mapped directory.</summary>
    /// <remarks>Supply a mapped workspace in its original location.
    /// Keep its directory layout and lower layers stable, and restrict direct access to all backing paths.
    /// On failure, close the supplied job before retrying or reclaiming the workspace.</remarks>
    public static void OpenMapped(SafeHandle siloJob, string workspaceDirectory)
    {
        using var job = BorrowJob(siloJob);
        var workspace = GetDirectory(workspaceDirectory);
        var view = Path.Combine(workspace, ViewName);
        var manifest = ReadMappedWorkspace(workspace, view);
        EnsureFiltersLoaded();

        try
        {
            var layerIds = Array.ConvertAll(manifest.Layers, layer => layer.LayerId);
            RegisterNativeWorkspace(job, view, Array.ConvertAll(manifest.Layers, layer => layer.Path), layerIds);
            BindView(job, manifest.Layers[0].Path, view);
        }
        catch (Exception ex)
        {
            ex.Data["WcifsScratchPath"] = workspace;
            throw;
        }
    }

    private static void RegisterNativeWorkspace(JobSafeHandle job, string view, string[] paths, Guid[] layerIds)
    {
        using var strings = new NativeApi.NativeStrings(paths);
        var layers = new WciLayerDescriptor[paths.Length];
        for (var i = 0; i < layers.Length; i++)
            layers[i] = new WciLayerDescriptor { Id = layerIds[i], Flags = InheritSecurity, Path = (char*)strings.Pointers[i] };

        fixed (WciLayerDescriptor* descriptors = layers)
        {
            var hr = 0;
            for (var attempt = 0; attempt < SetupAttempts; attempt++)
            {
                hr = WciSetupFilter(job, SparseHard, view, descriptors, (uint)layers.Length, Inner);
                if (hr == 0)
                    return;
            }

            throw new COMException($"WciSetupFilter returned 0x{hr:X8} after {SetupAttempts} attempts.", hr);
        }
    }

    private static JobSafeHandle BorrowJob(SafeHandle siloJob)
    {
        ArgumentNullException.ThrowIfNull(siloJob);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, SiloPlatform.MinimumBuild) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"This library requires Windows build {SiloPlatform.MinimumBuild} or later and an x64 process.");

        if (siloJob.IsInvalid || siloJob.IsClosed)
            throw new ArgumentException("An open silo job handle is required.", nameof(siloJob));

        return JobSafeHandle.Borrow(siloJob);
    }

    private static string GetDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':')
            throw new ArgumentException("Supply an absolute local drive path.", nameof(path));

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if ((File.GetAttributes(fullPath) & FileAttributes.Directory) == 0)
            throw new IOException($"Expected a directory at {fullPath}.");

        return fullPath;
    }

    private static bool IsSameOrBelow(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(@"..\", StringComparison.Ordinal);
    }

    private static void CheckScratchParent(string scratchParent, string[] paths)
    {
        // A junction or second drive letter can put scratch inside a lower.
        var parent = FinalDirectoryPath(scratchParent);
        foreach (var path in paths)
        {
            var lower = FinalDirectoryPath(path);
            if (IsSameOrBelow(lower, parent))
                throw new ArgumentException("The scratch directory would be inside a lower layer.", nameof(scratchParent));
        }
    }

    private static void BindView(JobSafeHandle job, string mapped, string view) =>
        ThrowForFailure(NativeMethods.BfSetupFilter(job, BindUseCurrentSiloMapping, mapped, view, null, 0), $"BfSetupFilter mapping '{mapped}' to '{view}'");

    private static void ThrowForFailure(int hr, string operation)
    {
        if (hr != 0)
            throw new COMException($"{operation} returned 0x{hr:X8}.", hr);
    }

    [LibraryImport("wci.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WciSetReparsePointData(string directory, in WcifsRootMarker marker, ushort markerSize);

    [LibraryImport("wci.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WciSetupFilter(JobSafeHandle job,
        uint isolationMode,
        string scratch,
        WciLayerDescriptor* layers,
        uint layerCount,
        uint nestingMode);

    // BuildXL's empty relative name selects the first layer's root.
    [StructLayout(LayoutKind.Sequential)]
    private struct WcifsRootMarker
    {
        internal uint Flags;
        internal Guid LayerId;
        internal uint NameLength;
    }
}