using Windows.Win32.Storage.FileSystem;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;
using Containers.Windows.Silos.DescriptionModel;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

internal sealed unsafe partial class NativeApi
{
    internal readonly record struct ImagePolicy
    {
        public bool AllowDownlevel { get; }
        public bool CheckBuild { get; }
        public uint ContainerMinimum { get; }
        public uint HostMinimum { get; }

        public ImagePolicy(bool allowDownlevel, bool checkBuild, uint containerMinimum, uint hostMinimum)
        {
            AllowDownlevel = allowDownlevel;
            CheckBuild = checkBuild;
            ContainerMinimum = containerMinimum;
            HostMinimum = hostMinimum;
        }
    }


    private const uint Server2019Build = 17763;

    private static readonly Lazy<bool> UtilityVm = new Lazy<bool>(ReadIsUtilityVm);

    private bool SkipHostFileInjection() => _platform.IsUtilityVm();

    internal partial class PlatformProvider
    {
        internal virtual bool IsUtilityVm() => UtilityVm.Value;
    }

    private bool HostFileMappingExists(string destination, string source)
        => _platform.FindFixupFile(destination, FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_ON_DISK_ENTRIES_ONLY);

    private static bool ReadIsUtilityVm()
    {
        const int sizeInBytes = 0x208;

        var value = stackalloc char[sizeInBytes / sizeof(char)];
        uint size = sizeInBytes;
        if (ReadValue(HKEY.HKEY_LOCAL_MACHINE, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", (uint)REG_ROUTINE_FLAGS.RRF_RT_REG_SZ, value, &size) != 0)
            return false;

        var text = new ReadOnlySpan<char>(value, (int)size / sizeof(char));
        var terminator = text.IndexOf('\0');
        return terminator >= 0 && text[..terminator] is "UtilityVM";
    }

    public void ConfigureServerImage(nint job, DownlevelConfiguration? configuration)
    {
        var policy = ReadImagePolicy();
        var host = ReadHostVersion();

        var image = ReadSiloVersion(job);

        if (!ImageAllowed(policy, host.Build, host.Revision, image.Build, image.Revision))
            SiloException.CheckHResult(unchecked((int)WIN32_ERROR.ERROR_VMCOMPUTE_IMAGE_MISMATCH), "Validate server silo image");

        if (configuration is not null)
            InjectHostFiles(job, configuration);
    }

    internal static bool ImageAllowed(ImagePolicy policy, uint hostBuild, uint hostUbr, uint imageBuild, uint imageUbr)
    {
        var buildMatches = policy.AllowDownlevel ? imageBuild >= Server2019Build && imageBuild <= hostBuild : imageBuild == hostBuild;
        return (!policy.CheckBuild || buildMatches) && imageUbr >= policy.ContainerMinimum && hostUbr >= policy.HostMinimum;
    }

    private static ImagePolicy ReadImagePolicy()
    {
        uint container = 0, host = 0, skipCheck = 0, downlevel = 0;
        var table = stackalloc RegistryQueryEntry[5];
        fixed (char* c = "ContainerMinimumLCU", h = "HostMinimumLCU", s = "SkipVersionCheck", d = "AllowDownlevelContainer")
        {
            table[0] = QueryDword(c, &container);
            table[1] = QueryDword(h, &host);
            table[2] = QueryDword(s, &skipCheck);
            table[3] = QueryDword(d, &downlevel);
            table[4] = default;
            var status = NativeMethods.RtlQueryRegistryValuesEx(Wdk.RTL_REGISTRY_CONTROL, "Windows Containers", table, 0, 0);
            if (status != NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND.Value)
                SiloException.CheckNt(status, "Read Windows Containers registry policy");
        }

        return new ImagePolicy(downlevel != 0, skipCheck == 0, container, host);
    }
}
