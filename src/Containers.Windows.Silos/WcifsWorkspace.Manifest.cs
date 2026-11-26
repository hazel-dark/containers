using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Win32;

namespace Containers.Windows.Silos;

public static partial class WcifsWorkspace
{
    private const string ViewName = "view";
    private const string SupplementalName = "directories";
    private const string PackagesName = "layers";
    private const string ManifestName = "workspace.json";
    private const string PendingManifestName = "workspace.json.new";
    private const int ManifestVersion = 1;

    private static void PrepareMappedWorkspace(string scratch, string view, string[] paths, Guid[] layerIds)
    {
        var packages = Path.Combine(scratch, PackagesName);
        CreateDirectoryExclusive(packages);

        var layers = new WcifsMappedWorkspaceLayer[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            var package = PackagePath(scratch, i, layerIds[i]);
            CreateDirectoryExclusive(package);

            var alias = Path.Combine(package, "Files");
            CreateLayerJunction(alias, paths[i]);

            var lowerId = DirectoryIdentity(paths[i], followReparse: true);
            if (DirectoryIdentity(alias, followReparse: true) != lowerId)
                throw new IOException($"The layer alias '{alias}' does not resolve to '{paths[i]}'.");

            layers[i] = new WcifsMappedWorkspaceLayer(paths[i], layerIds[i], lowerId, DirectoryIdentity(package), null,
                DirectoryIdentity(alias, expectedTag: PInvoke.IO_REPARSE_TAG_MOUNT_POINT));
        }

        var manifest = new MappedWorkspaceManifest(ManifestVersion, false, scratch, DirectoryIdentity(scratch), DirectoryIdentity(view, expectedTag: null), DirectoryIdentity(packages), layers);
        using var file = new FileStream(Path.Combine(scratch, PendingManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, manifest, WcifsMappedWorkspaceJson.Default.MappedWorkspaceManifest);
        file.Flush(flushToDisk: true);
    }

    private static void PublishManifest(string scratch) =>
        File.Move(Path.Combine(scratch, PendingManifestName), Path.Combine(scratch, ManifestName));

    private static void RemovePackages(string scratch)
    {
        var packages = Path.Combine(scratch, PackagesName);
        if (!Directory.Exists(packages))
            return;

        foreach (var package in Directory.EnumerateDirectories(packages))
        {
            var files = Path.Combine(package, "Files");
            if (Directory.Exists(files))
                Directory.Delete(files);

            Directory.Delete(package);
        }

        Directory.Delete(packages);
    }

    private static MappedWorkspaceManifest ReadMappedWorkspace(string workspace, string view)
    {
        var workspaceId = DirectoryIdentity(workspace);
        var manifestPath = Path.Combine(workspace, ManifestName);
        MappedWorkspaceManifest? manifest;
        try
        {
            using var file = OpenManifest(manifestPath);
            manifest = JsonSerializer.Deserialize(file, WcifsMappedWorkspaceJson.Default.MappedWorkspaceManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid WCIFS workspace manifest '{manifestPath}'.", ex);
        }

        if (manifest is null || manifest.Version != ManifestVersion || manifest.Scoped || !PathEquals(manifest.WorkspacePath, workspace) || manifest.WorkspaceId != workspaceId || manifest.Layers == null! || manifest.Layers.Length is 0 or 2)
            throw new InvalidDataException($"The WCIFS workspace manifest does not match '{workspace}'.");

        CheckIdentity(view, manifest.ViewId, expectedTag: null);
        CheckIdentity(Path.Combine(workspace, PackagesName), manifest.PackagesId);

        var supplemental = Path.Combine(workspace, SupplementalName);
        var ids = new HashSet<Guid>();
        for (var i = 0; i < manifest.Layers.Length; i++)
        {
            var layer = manifest.Layers[i];
            var isSupplemental = manifest.Layers.Length > 1 && i == manifest.Layers.Length - 1;
            if (layer == null! || string.IsNullOrWhiteSpace(layer.Path) || layer.LayerId == Guid.Empty || !ids.Add(layer.LayerId) || isSupplemental != PathEquals(layer.Path, supplemental) || layer.FilesId.HasValue)
                throw new InvalidDataException($"Invalid WCIFS layer {i} in '{manifestPath}'.");

            var lower = GetDirectory(layer.Path);
            if (!PathEquals(layer.Path, lower))
                throw new InvalidDataException($"Layer {i} is not an absolute normalized path in '{manifestPath}'.");

            CheckIdentity(lower, layer.LowerId, followReparse: !isSupplemental);

            var package = PackagePath(workspace, i, layer.LayerId);
            CheckIdentity(package, layer.PackageId);

            var alias = Path.Combine(package, "Files");
            CheckIdentity(alias, layer.AliasId, expectedTag: PInvoke.IO_REPARSE_TAG_MOUNT_POINT);
            CheckIdentity(alias, layer.LowerId, followReparse: true);
        }

        CheckRootMarker(view, manifest.Layers[0].LayerId);
        return manifest;
    }

    private static unsafe void CheckRootMarker(string view, Guid layerId)
    {
        var buffer = new byte[PInvoke.MAXIMUM_REPARSE_DATA_BUFFER_SIZE];
        var length = checked((ushort)buffer.Length);
        fixed (byte* data = buffer)
            ThrowForFailure(WciReadReparsePointData(view, data, ref length), "WciReadReparsePointData");

        // The packed root marker has flags, a layer GUID and an empty UTF-16 name.
        // WCIFS can set bit 1 after the first job materializes the view.
        if (length < 22 || BinaryPrimitives.ReadUInt32LittleEndian(buffer) is not (0 or 2) || new Guid(buffer.AsSpan(4, 16)) != layerId || BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(20)) != 0)
            throw new InvalidDataException($"The WCIFS root marker at '{view}' does not match the first layer.");
    }

    private static string PackagePath(string workspace, int index, Guid layerId) =>
        Path.Combine(workspace, PackagesName, index.ToString(CultureInfo.InvariantCulture) + "-" + layerId.ToString("N"));

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void CheckIdentity(string path, WcifsWorkspaceEntryId expected, bool followReparse = false, uint? expectedTag = 0)
    {
        if (DirectoryIdentity(path, followReparse, expectedTag) != expected)
            throw new InvalidDataException($"The WCIFS workspace directory '{path}' was replaced.");
    }

    [LibraryImport("wci.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int WciReadReparsePointData(string path, byte* data, ref ushort bytes);

    internal readonly record struct WcifsWorkspaceEntryId(ulong VolumeSerial, Guid FileId);

    internal sealed record WcifsMappedWorkspaceLayer
    {
        [JsonConverter(typeof(WcifsNtfsPathConverter)), JsonPropertyName("PathUtf16")]
        public string Path { get; set; }

        public Guid LayerId { get; set; }
        public WcifsWorkspaceEntryId LowerId { get; set; }
        public WcifsWorkspaceEntryId PackageId { get; set; }
        public WcifsWorkspaceEntryId? FilesId { get; set; }
        public WcifsWorkspaceEntryId AliasId { get; set; }

        public WcifsMappedWorkspaceLayer(
            string path,
            Guid layerId,
            WcifsWorkspaceEntryId lowerId,
            WcifsWorkspaceEntryId packageId,
            WcifsWorkspaceEntryId? filesId,
            WcifsWorkspaceEntryId aliasId)
        {
            Path = path;
            LayerId = layerId;
            LowerId = lowerId;
            PackageId = packageId;
            FilesId = filesId;
            AliasId = aliasId;
        }
    }

    internal sealed record MappedWorkspaceManifest
    {
        public int Version { get; set; }
        public bool Scoped { get; set; }

        [JsonConverter(typeof(WcifsNtfsPathConverter)), JsonPropertyName("WorkspacePathUtf16")]
        public string WorkspacePath { get; set; }

        public WcifsWorkspaceEntryId WorkspaceId { get; set; }
        public WcifsWorkspaceEntryId ViewId { get; set; }
        public WcifsWorkspaceEntryId PackagesId { get; set; }
        public WcifsMappedWorkspaceLayer[] Layers { get; set; }

        public MappedWorkspaceManifest(
            int version,
            bool scoped,
            string workspacePath,
            WcifsWorkspaceEntryId workspaceId,
            WcifsWorkspaceEntryId viewId,
            WcifsWorkspaceEntryId packagesId,
            WcifsMappedWorkspaceLayer[] layers)
        {
            Version = version;
            Scoped = scoped;
            WorkspacePath = workspacePath;
            WorkspaceId = workspaceId;
            ViewId = viewId;
            PackagesId = packagesId;
            Layers = layers;
        }
    }

    // NTFS names can contain unpaired UTF-16 code units. JSON Unicode text would
    // replace them, leaving a manifest that cannot reopen the directory we created.
    internal sealed class WcifsNtfsPathConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !reader.TryGetBytesFromBase64(out var bytes) || (bytes.Length & 1) != 0)
                throw new JsonException("Expected a base64-encoded UTF-16 Windows path.");

            return new string(MemoryMarshal.Cast<byte, char>(bytes));
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteBase64StringValue(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(MappedWorkspaceManifest))]
    internal sealed partial class WcifsMappedWorkspaceJson : JsonSerializerContext;
}