using System.Runtime.InteropServices;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed partial class WindowsXmlOracleTests
{
    private sealed class SiloBuildTheoryAttribute : TheoryAttribute
    {
        public SiloBuildTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
                Environment.OSVersion.Version.Build != SiloPlatform.MinimumBuild)
                Skip = $"Requires the build {SiloPlatform.MinimumBuild} x64 container.dll reference implementation.";
        }
    }

    [SiloBuildTheory]
    [InlineData("<container/>", 0)]
    [InlineData("<container><namespace><job><memory limit='０x４'/></job></namespace></container>", 0)]
    [InlineData("<container><namespace><job><memory limit='&#xA0;٤٢'/></job></namespace></container>", 0)]
    [InlineData("<container><?pi value?><", unchecked((int)0x800705b9))]
    [InlineData("<container><namespace><job><terminationpolicy><?pi value?></terminationpolicy></job></namespace><", unchecked((int)0x800705b9))]
    [InlineData("<container><namespace><filesystem><layer identifier='00112233-0x55-6677-8899-aabbccddeeff'/></filesystem></namespace></container>", unchecked((int)0x800705b9))]
    public void ManagedAndNativeReadersAgreeOnAcceptanceAndSchemaHResult(string xml, int expected)
    {
        var native = WcCreateDescriptionFromXml(xml, out var description);
        try
        {
            Assert.Equal(expected, native);
            Assert.Equal(native >= 0, description != 0);
            if (native >= 0)
                Assert.NotNull(SiloDescriptionXml.Parse(xml));
            else
                Assert.Equal(native, Assert.Throws<SiloException>(() => SiloDescriptionXml.Parse(xml)).HResult);
        }
        finally { WcDestroyDescription(description); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("container.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WcCreateDescriptionFromXml(string xml, out nint description);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("container.dll")]
    private static partial void WcDestroyDescription(nint description);
}
