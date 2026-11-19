using System.Text;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed unsafe class ServerImageMetadataTests
{
    [Theory]
    [InlineData(0xea, 0)]
    [InlineData(2, 0x490)]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(2, 2)]
    public void ReadsImageMetadataAndPreservesProbeErrorIdentities(int kmodeStatus, int revisionStatus)
    {
        var seen = new List<string>();
        int Read(nint key, string subkey, string name, uint flags, void* data, uint* size)
        {
            seen.Add(name);
            Assert.Equal(name == "Kmode" ? 2 : 1, key);
            if (name == "Kmode")
            {
                Assert.Equal(1u, *size);
                return kmodeStatus;
            }
            if (name == "UBR")
            {
                Assert.Equal(0x10u, flags);
                Assert.Equal(4u, *size);
                *(uint*)data = 17;
                return revisionStatus;
            }
            var text = name == "CurrentBuildNumber" ? "20348" : new string('x', 128);
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            var capacity = *size;
            *size = (uint)bytes.Length;
            if (capacity < *size)
                return 0xea;
            bytes.CopyTo(new Span<byte>(data, bytes.Length));
            return 0;
        }

        NativeApi.ImageVersion Version() => NativeApi.ReadImageVersion(1, 2, Read);
        if (revisionStatus == 2 || kmodeStatus == 5)
            Assert.Equal(revisionStatus == 2 ? 2 : 5, Assert.Throws<SiloException>(() => Version()).Win32Error);
        else if (kmodeStatus == 0)
            Assert.Throws<InvalidDataException>(() => Version());
        else
            Assert.Equal(new NativeApi.ImageVersion(20348, revisionStatus == 0 ? 17u : 0u), Version());
        string[] expected = revisionStatus == 2 ? ["CurrentBuildNumber", "UBR"] : ["CurrentBuildNumber", "UBR", "EditionID", "EditionID", "Kmode"];
        Assert.Equal(expected, seen);
    }

    [Theory]
    [InlineData("٢٦١٠٠", 26100u)]
    [InlineData(" +２０３４８.extra", 20348u)]
    public void BuildNumberAcceptsUcrtDecimalDigits(string text, uint build)
        => Assert.Equal(build, NativeApi.ParseBuildNumber(text));

    [Theory]
    [InlineData("no digits")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void InvalidBuildNumberPreservesTheNativeExceptionHResult(string text)
    {
        var error = Assert.Throws<SiloException>(() => NativeApi.ParseBuildNumber(text));
        Assert.Equal(13, error.Win32Error);
        Assert.Equal(unchecked((int)0x8007000d), error.HResult);
    }
}
