using Windows.Win32.Foundation;
using Containers.Windows.Silos.DescriptionModel;
using Wdk = Windows.Wdk.PInvoke;

namespace Containers.Windows.Silos;

internal partial interface ISiloApi
{
    uint ValidateBootImage(JobSafeHandle job);
}

internal sealed unsafe partial class NativeApi
{
    private static bool SkipHostFileInjection() => false;

    private bool HostFileMappingExists(string destination, string source)
        => _platform.FindFixupFile(destination, 0) && _platform.FindFixupFile(source, 0);

    public void ConfigureServerImage(JobSafeHandle job, DownlevelConfiguration? configuration)
    {
        if (configuration is not null)
            InjectHostFiles(job, configuration);
    }

    public uint ValidateBootImage(JobSafeHandle job)
    {
        uint containerMinimum = 0, hostMinimum = 0, skipCheck = 0;
        var table = stackalloc RegistryQueryEntry[4];
        fixed (char* c = "ContainerMinimumLCU", h = "HostMinimumLCU", s = "SkipVersionCheck")
        {
            table[0] = QueryDword(c, &containerMinimum);
            table[1] = QueryDword(h, &hostMinimum);
            table[2] = QueryDword(s, &skipCheck);
            table[3] = default;

            var status = NativeMethods.RtlQueryRegistryValuesEx(Wdk.RTL_REGISTRY_CONTROL, "Windows Containers", table, 0, 0);
            if (status != NTSTATUS.STATUS_OBJECT_NAME_NOT_FOUND.Value)
                SiloException.CheckNt(status, "Read Windows Containers registry policy");
        }

        var host = ReadHostVersion();
        var image = ReadSiloVersion(job);
        if (!ImageAllowed(skipCheck != 0, containerMinimum, hostMinimum, image, host))
            SiloException.CheckHResult(unchecked((int)WIN32_ERROR.ERROR_VMCOMPUTE_IMAGE_MISMATCH), "Validate server silo image");

        return image.Build;
    }

    internal static bool ImageAllowed(bool skipCheck, uint containerMinimum, uint hostMinimum, ImageVersion image, ImageVersion host)
        => (skipCheck || image.Build is 20348 or 26100) && image.Revision >= containerMinimum && host.Revision >= hostMinimum;
}
