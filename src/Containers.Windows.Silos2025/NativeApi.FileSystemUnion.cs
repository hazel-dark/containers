using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Registry;
using Windows.Win32.System.Rpc;
using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    private static readonly JobSafeHandle NoJob = new JobSafeHandle(0, ownsHandle: false);
    private const uint DeltaHiveFormat = 1;

    private static readonly string[] SandboxHives = ["Software_Delta", "System_Delta", "DefaultUser_Delta", "Sam_Delta", "Security_Delta"];

    internal static void SetupFileSystemUnion(JobSafeHandle? job, string scratchRoot, IReadOnlyList<WciLayer> layers, IReadOnlyList<BindBatch> batches)
    {
        var bindBatches = batches.Select(batch => batch with { Exceptions = batch.Exceptions.ToArray() }).ToArray();
        job ??= NoJob;
        if (job.DangerousGetHandle() == 0 && bindBatches.Length != 0)
            SiloException.CheckWin32((int)WIN32_ERROR.ERROR_INVALID_PARAMETER, "Job handle required for Bind batches");

        var mountPath = BeforeTerminator(scratchRoot);
        var id = ParseUnionMountId(mountPath);
        var paths = layers.Select(layer => layer.Path).Prepend(mountPath).ToArray();

        using (var nativePaths = new NativeStrings(paths))
        {
            fixed (nint* p = nativePaths.Pointers)
            {
                var configPath = UnionConfigPath(mountPath);
                if (NativeMethods.RtlDoesFileExists_U(configPath) != 0)
                {
                    SiloException.CheckHResult(NativeMethods.MigrateFileSystemUnion(p, (uint)paths.Length, configPath, &id, job, 0), "MigrateFileSystemUnion");
                    if (!PInvoke.DeleteFile(configPath))
                        SiloEventSource.Log.MigrationConfigDeleteFailed(configPath, GetLastErrorOrDefault());
                }
                else
                {
                    InitializeUnionSandbox(scratchRoot);
                    SiloException.CheckHResult(NativeMethods.CreateFileSystemUnion(&id, p, (uint)paths.Length, 0, job, 0), "CreateFileSystemUnion");
                }
            }
        }

        // FilesystemProvider::Setup arms its removal guard after a successful
        // create or migration (0x180024b71). Bind file reads and both Bind calls are covered.
        try
        {
            for (var i = 0; i < bindBatches.Length; i++)
                SetupBindBatch(job, bindBatches[i], registerExceptions: i != 0);
        }
        catch (Exception failure)
        {
            try
            {
                RemoveUnion(id, scratchRoot);
            }
            catch (Exception cleanupError)
            {
                failure.Data["UnionCleanupFailure"] = cleanupError;
            }

            throw;
        }
    }

    // WcCleanupFilesystemNamespace 0x180011c50 selects provider 1 at 0x180024098.
    // Negative RemoveFileSystemUnion results are logged at the provider boundary.
    internal static void CleanupFileSystemUnion(string mountPath)
    {
        mountPath = BeforeTerminator(mountPath);

        var id = ParseUnionMountId(mountPath);
        RemoveUnion(id, mountPath);
    }

    private static void RemoveUnion(Guid id, string mountPath)
    {
        var configPath = UnionConfigPath(mountPath);
        var result = NativeMethods.RemoveFileSystemUnion(&id, 0, configPath);
        if (result < 0)
            SiloEventSource.Log.UnionRemovalFailed(configPath, result);
    }

    // ExtractGuidFromMountPath 0x18002417c uses the first brace of each kind,
    // and maps either a missing pair or any UUID parser failure to 0x80070057.
    private static Guid ParseUnionMountId(string mountPath)
    {
        var start = mountPath.IndexOf('{');
        var end = mountPath.IndexOf('}');
        if (start < 0 || end <= start)
            SiloException.CheckWin32((int)WIN32_ERROR.ERROR_INVALID_PARAMETER, "Find filesystem union mount GUID");

        fixed (char* guidText = mountPath[(start + 1)..end])
        {
            if (PInvoke.UuidFromString(guidText, out var id) != RPC_STATUS.RPC_S_OK)
                SiloException.CheckWin32((int)WIN32_ERROR.ERROR_INVALID_PARAMETER, "Parse filesystem union mount GUID");

            return id;
        }
    }

    private static string UnionConfigPath(string mountPath)
        => AppendUnionPath(mountPath, @"Windows\container_tombstones.json");

    private static string AppendUnionPath(string root, string relative)
        => root.EndsWith('\\') ? root + relative : root + "\\" + relative;

    private static string BeforeTerminator(string path)
    {
        var nul = path.IndexOf('\0');
        return nul >= 0 ? path[..nul] : path;
    }

    // InitializeSandbox 0x1800243dc creates the Hives directory once, then
    // writes five empty delta hives with a specialization marker in System_Delta.
    private static void InitializeUnionSandbox(string root)
    {
        var directory = AppendUnionPath(root, @"WcSandboxState\Hives");
        if (!PInvoke.CreateDirectory(directory))
            SiloException.CheckWin32(GetLastErrorOrDefault(), "Create " + directory);

        for (var i = 0; i < SandboxHives.Length; i++)
        {
            nint hive = 0;
            Exception? primaryFailure = null;
            try
            {
                CheckOfflineStatus(NativeMethods.CreateOfflineHive(out hive, DeltaHiveFormat), "ORCreateHiveEx");
                if (i == 1)
                    SetOfflineSystemMarker(hive, ["ControlSet001", "Control", "Windows Containers"], 0);

                var path = AppendUnionPath(directory, SandboxHives[i]);
                using var file = PInvoke.CreateFile(path, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE, FILE_SHARE_MODE.FILE_SHARE_READ, null, FILE_CREATION_DISPOSITION.CREATE_NEW, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS);

                if (file.IsInvalid)
                    SiloException.CheckWin32(GetLastErrorOrDefault(), "Create " + path);

                CheckOfflineStatus(NativeMethods.SaveOfflineHive(hive, file, 10, 0, DeltaHiveFormat), "ORSaveHiveToHandle");
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                if (hive != 0)
                {
                    // The native destructor fail-fasts here. Stop managed setup, preserving
                    // the earlier error when both saving and closing the hive have failed.
                    try
                    {
                        CheckOfflineStatus(NativeMethods.CloseOfflineHive(hive), "ORCloseHive");
                    }
                    catch (Exception closeError) when (primaryFailure is not null)
                    {
                        primaryFailure.Data["OfflineHiveCloseFailure"] = closeError;
                    }
                }
            }
        }
    }

    // OfflineHiveLib::SetValueRecursively 0x18002bc28 opens each key, creates
    // and reopens it on failed open, and keeps open keys alive while descending.
    private static void SetOfflineSystemMarker(nint parent, ReadOnlySpan<string> keys, int index)
    {
        if (index == keys.Length)
        {
            uint enabled = 1;
            CheckOfflineStatus(NativeMethods.SetOfflineValue(parent, "CryptoSpecializeNeeded", (uint)REG_VALUE_TYPE.REG_DWORD, &enabled, sizeof(uint)), "ORSetValue");
            return;
        }

        var name = keys[index];
        nint key = 0;
        try
        {
            if (NativeMethods.OpenOfflineKey(parent, name, out key) != 0)
            {
                CloseOfflineKey(ref key);
                CheckOfflineStatus(NativeMethods.CreateOfflineKey(parent, name, null, 0, null, out key, null), "ORCreateKey");
                CloseOfflineKey(ref key);
                CheckOfflineStatus(NativeMethods.OpenOfflineKey(parent, name, out key), "OROpenKey");
            }

            SetOfflineSystemMarker(key, keys, index + 1);
        }
        finally { CloseOfflineKey(ref key); }
    }

    private static void CloseOfflineKey(ref nint key)
    {
        if (key != 0)
        {
            _ = NativeMethods.CloseOfflineKey(key);
            key = 0;
        }
    }

    private static void CheckOfflineStatus(uint status, string operation)
        => SiloException.CheckWin32(unchecked((int)status), operation);
}