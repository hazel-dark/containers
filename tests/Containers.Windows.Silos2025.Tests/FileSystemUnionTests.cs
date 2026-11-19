using Containers.Windows.Silos.DescriptionModel;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class FileSystemUnionTests
{
    [Theory]
    [InlineData(@"C:\scratch")]
    [InlineData(@"C:\}\{")]
    [InlineData("missing\0{00000000-0000-0000-0000-000000000000}")]
    public void SetupRejectsAnInvalidEffectiveMountPath(string path)
    {
        const int invalidArgument = unchecked((int)0x80070057);
        Assert.Equal(invalidArgument, Assert.Throws<SiloException>(() => SiloFileSystemNamespace.Setup(path, [])).HResult);

    }

    [Fact]
    public void ManagedCleanupRejectsAnInvalidMountBeforeCallingTheProvider()
    {
        Assert.Equal(87, Assert.Throws<SiloException>(() => SiloFileSystemNamespace.Cleanup("invalid")).Win32Error);
    }

    [Fact]
    public void BindBatchRequiresAJobBeforeUnionFilesystemWork()
    {
        var error = Assert.Throws<SiloException>(() =>
            SiloFileSystemNamespace.Setup(@"{f04ca1bf-8d52-4b16-9de8-e236aaae8954}", [], [new BindBatch("map")]));
        Assert.Equal(87, error.Win32Error);
    }
}
