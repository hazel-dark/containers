using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

/// <summary>Manages the standalone UnionFS namespace supported by Windows Server 2025.</summary>
[SupportedOSPlatform(SiloPlatform.SupportedVersion)]
public static class SiloFileSystemNamespace
{
    /// <summary>
    /// Creates or migrates a filesystem union and optionally applies Bind Filter batches
    /// to a borrowed silo job. The scratch path must contain a braced mount GUID.
    /// </summary>
    public static void Setup(string scratchRoot,
        IReadOnlyList<WciLayer> layers,
        IReadOnlyList<BindBatch>? batches = null,
        SafeHandle? job = null)
    {
        ArgumentNullException.ThrowIfNull(scratchRoot);
        ArgumentNullException.ThrowIfNull(layers);
        var borrowed = false;
        try
        {
            job?.DangerousAddRef(ref borrowed);
            NativeApi.SetupFileSystemUnion(job?.DangerousGetHandle() ?? 0, scratchRoot, layers, batches ?? []);
        }
        finally
        {
            if (borrowed)
                job!.DangerousRelease();
        }
    }

    /// <summary>Removes the filesystem union identified by the mount path.</summary>
    /// <remarks>An invalid mount GUID produces a managed argument HRESULT. The pinned native
    /// cleanup crosses a noexcept boundary and terminates the caller on that input.</remarks>
    public static void Cleanup(string mountPath)
    {
        ArgumentNullException.ThrowIfNull(mountPath);
        NativeApi.CleanupFileSystemUnion(mountPath);
    }
}