namespace Containers.Windows.Silos;

internal static class SiloPlatform
{
    internal const int ServerSiloBasicInformationSize = 32;
    internal const int MinimumBuild = 20348;
    internal const string SupportedVersion = "windows10.0.20348";

    [System.Runtime.Versioning.SupportedOSPlatform(SupportedVersion)]
    internal static SiloTerminationReason? GetNotificationReason(IJobNotificationApi api, JobSafeHandle job, nuint code, int status)
        => SiloTerminationNotification.Classify(api.IsServer(job), code, status);

    internal static SiloTerminationReason OtherServerTerminationReason(int status) =>
        SiloTerminationReason.UnexpectedExit;
}

public enum SiloTerminationReason : uint
{
    NormalExit = 0,
    UnexpectedExit = 1,
    Reboot = 2,
}