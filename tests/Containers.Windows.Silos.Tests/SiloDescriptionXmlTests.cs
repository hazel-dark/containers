using System.Text;
using System.Xml;
using Containers.Windows.Silos.DescriptionModel;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class SiloDescriptionXmlTests
{
    [Theory]
    [InlineData("٤٢", 42)]
    [InlineData("４２", 42)]
    [InlineData("０１０", 8)]
    [InlineData("０xF", 15)]
    [InlineData("0x４", 4)]
    [InlineData("&#x00A0;1", 1)]
    [InlineData("&#x85;1", 1)]
    public void AcceptsPinnedUcrtIntegerForms(string token, int expected)
    {
        var description = SiloDescriptionXml.Parse($"<container><namespace><job><memory limit='{token}'/></job></namespace></container>");
        Assert.Equal((ulong)expected, description.Job!.MemoryLimit);
    }

    [Theory]
    [InlineData("<container><?pi value?><")]
    [InlineData("<container><namespace><job><terminationpolicy><?pi value?></terminationpolicy></job></namespace><")]
    [InlineData("<container><namespace><job><memory limit='1&#x00A0;'/></job></namespace></container>")]
    [InlineData("<container><namespace><job><memory limit='০xG'/></job></namespace></container>")]
    [InlineData("<container><namespace><filesystem><layer identifier='00112233-0x55-6677-8899-aabbccddeeff'/></filesystem></namespace></container>")]
    [InlineData("<container><namespace><filesystem><layer identifier='+0112233-4455-6677-8899-aabbccddeeff'/></filesystem></namespace></container>")]
    public void RejectsInvalidScalarOrUnexpectedRootContent(string xml)
    {
        Assert.Equal(unchecked((int)0x800705b9), Assert.Throws<SiloException>(() => SiloDescriptionXml.Parse(xml)).HResult);
    }
    [Fact]
    public void ParsesBuildXlEmptyDescription()
    {
        Assert.Equal(new SiloDescription(), SiloDescriptionXml.Parse("<container></container>"));
        Assert.Equal(new SiloDescription(), SiloDescriptionXml.Parse(
            "\ufeff<?xml version='1.0' encoding='UTF-16'?>\n<!--description-->\n<CONTAINER/>"));
    }

    [Fact]
    public void PreservesEmptyProviderPresenceAndNativeDefaults()
    {
        var description = SiloDescriptionXml.Parse("""
            <container>
              <namespace>
                <job><cpu/><memory/><systemroot/></job>
                <filesystem><layer/><bindings/></filesystem>
                <ob><objdir/><symlink/></ob>
                <registry><hivestack><layer/><mkkey><mkvalue/></mkkey><delkey/></hivestack></registry>
                <network/><mountmgr/><namedpipe/><device/><hostfiles/>
              </namespace>
            </container>
            """);

        var job = Assert.IsType<JobConfiguration>(description.Job);
        Assert.Equal(new CpuConfiguration(), job.Cpu);
        Assert.Equal(0ul, job.MemoryLimit);
        Assert.Equal("", job.SystemRoot);
        Assert.False(job.KillOnLastHandleClose);
        Assert.False(job.EnableThreadImpersonation);
        var files = Assert.IsType<FileSystemConfiguration>(description.FileSystem);
        var wci = Assert.IsType<WciConfiguration>(files.Wci);
        Assert.Equal("", wci.ScratchRoot);
        Assert.Equal(0u, wci.IsolationMode);
        Assert.Equal(0u, wci.NestingMode);
        Assert.Equal(new WciLayer(Guid.Empty, ""), Assert.Single(wci.Layers));
        Assert.Equal("", Assert.Single(files.Batches).ConfigurationPath);
        var objects = Assert.IsType<ObjectNamespaceConfiguration>(description.ObjectNamespace);
        Assert.False(objects.ShadowRoot);
        Assert.Equal("", Assert.Single(objects.Directories).Name);
        Assert.Equal(new ObjectSymlink(""), Assert.Single(objects.Symlinks));
        var hiveStack = Assert.Single(Assert.IsType<RegistryConfiguration>(description.Registry).HiveStacks);
        Assert.Equal(new RegistryHive(), Assert.Single(hiveStack.Hives));
        var noPayload = Assert.Single(Assert.Single(hiveStack.Keys).Values);
        Assert.Equal("", noPayload.Name);
        Assert.False(noPayload.HasPayload);
        Assert.Equal("", Assert.Single(hiveStack.DeleteKeys));
        Assert.Equal("", description.NetworkCompartment);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<DriveMapping>>(description.Mounts));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<PipeMapping>>(description.NamedPipes));
        var device = Assert.IsType<DeviceConfiguration>(description.Device);
        Assert.False(device.RequiresDriverStore);
        Assert.Empty(device.Categories);
        Assert.Empty(device.Instances);
        var hostFiles = Assert.IsType<DownlevelConfiguration>(description.Downlevel);
        Assert.Equal("", hostFiles.ContainerRoot);
        Assert.Null(hostFiles.BaseImagePath);
        Assert.Empty(hostFiles.HostFiles);
    }

    [Fact]
    public void ParsesJobFilesystemMountsPipesAndNestedObjects()
    {
        var description = SiloDescriptionXml.Parse("""
            <xml:Container>
              <NaMeSpAcE>
                <JoB THREADIMPERSONATION="TRUE">
                  <cpu RATE="0X32" weight="011"/>
                  <memory limit="  +0x100000000"/>
                  <systemroot path="\??\C:\image\Windows"/>
                  <terminationpolicy>KiLlOn<!-- policy -->Handle<![CDATA[Close]]></terminationpolicy>
                </JoB>
                <filesystem path="C:\scratch" isolation="SOFT" nesting="Outer">
                  <layer identifier="00112233-4455-6677-8899-AABBCCDDEEFF" path="C:\parent"/>
                  <bindings>
                    <filepath>C:\files &amp; paths\config.bin</filepath>
                    <bindingroots>C:\first</bindingroots>
                    <bindingroots><![CDATA[C:\second]]></bindingroots>
                  </bindings>
                  <layer identifier="11223344-5566-7788-99aa-bbccddeeff00" path="C:\base"/>
                  <bindings><filepath>C:\second.bin</filepath></bindings>
                </filesystem>
                <mountmgr><mount_point name="x:ignored-by-mountmgr" path="\Device\HarddiskVolume9"/></mountmgr>
                <namedpipe><symlink name="input" path="\Device\NamedPipe\host"/></namedpipe>
                <ob shadow="IFEXISTS">
                  <symlink name="host" pathtoclone="\??\HostLink" scope="GlobalDropSilo" access_mask="0x220"/>
                  <objdir name="Global??" clonesd="\GLOBAL??" shadow="TrUe">
                    <symlink name="link" path="\Device\Target" scope="Global"/>
                    <objdir name="inner" shadow="ifexists">
                      <symlink name="local" path="\Target" scope="Local"/>
                    </objdir>
                  </objdir>
                </ob>
                <network compartment="Build Network"/>
              </NaMeSpAcE>
            </xml:Container>
            """);

        var job = Assert.IsType<JobConfiguration>(description.Job);
        Assert.Equal(new CpuConfiguration(9, 50), job.Cpu);
        Assert.Equal(0x100000000ul, job.MemoryLimit);
        Assert.Equal(@"\??\C:\image\Windows", job.SystemRoot);
        Assert.True(job.EnableThreadImpersonation);
        Assert.True(job.KillOnLastHandleClose);

        var files = Assert.IsType<FileSystemConfiguration>(description.FileSystem);
        var wci = Assert.IsType<WciConfiguration>(files.Wci);
        Assert.Equal(@"C:\scratch", wci.ScratchRoot);
        Assert.Equal(1u, wci.IsolationMode);
        Assert.Equal(1u, wci.NestingMode);
        Assert.Equal(
            [
                new WciLayer(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), @"C:\parent"),
                new WciLayer(Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00"), @"C:\base"),
            ], wci.Layers);
        Assert.Empty(files.Bindings);
        Assert.Equal(@"C:\files & paths\config.bin", files.Batches[0].ConfigurationPath);
        Assert.Equal([@"C:\first", @"C:\second"], files.Batches[0].Exceptions);
        Assert.Equal(@"C:\second.bin", files.Batches[1].ConfigurationPath);
        Assert.Equal(new DriveMapping('x', @"\Device\HarddiskVolume9"), Assert.Single(description.Mounts!));
        Assert.Equal(new PipeMapping("input", @"\Device\NamedPipe\host"), Assert.Single(description.NamedPipes!));
        Assert.Equal("Build Network", description.NetworkCompartment);

        var objects = Assert.IsType<ObjectNamespaceConfiguration>(description.ObjectNamespace);
        Assert.True(objects.ShadowRoot);
        Assert.Equal(new ObjectSymlink("host")
        {
            TargetSource = @"\??\HostLink", Scope = SymlinkScope.GlobalDropSilo, AccessMask = 0x220,
        }, Assert.Single(objects.Symlinks));
        var directory = Assert.Single(objects.Directories);
        Assert.Equal("Global??", directory.Name);
        Assert.Equal(@"\GLOBAL??", directory.SecurityDescriptorSource);
        Assert.Equal(DirectoryShadow.Required, directory.Shadow);
        Assert.Equal(new ObjectSymlink("link") { Target = @"\Device\Target", Scope = SymlinkScope.Global },
            Assert.Single(directory.Symlinks));
        var inner = Assert.Single(directory.Directories);
        Assert.Equal(DirectoryShadow.IfExists, inner.Shadow);
        Assert.Equal(SymlinkScope.Local, Assert.Single(inner.Symlinks).Scope);
    }

    [Fact]
    public void ParsesRegistryLayersRedirectionsAndValuePayloads()
    {
        var description = SiloDescriptionXml.Parse("""
            <container><namespace><registry>
              <symlink key="\Registry\Key" target="\Registry\Target"/>
              <redirectionnode containerpath="\Container" hostpath="\Host" hivestack="SOFTWARE"
                               access_mask="0xffffffff" trustedhive="True" exitnode="TRUE"/>
              <hivestack hive="SOFTWARE">
                <layer hosthive="\Registry\Machine\Software" readonly="true" immutable="true"
                       inherittrustclass="true" trustedhive="true" writethrough="true">
                  <fileaccesstoken>0xfffffffffffffabc</fileaccesstoken>
                </layer>
                <layer filepath="C:\hive" identifier="00112233-4455-6677-8899-aabbccddeeff"/>
                <mkkey name="Root" clonesd="\Registry\Machine" volatile="true">
                  <mkkey name="Child" volatile="false"/>
                  <mkvalue name="text" data_string="a &amp; b"/>
                  <mkvalue name="multi" data_multistring="one;two"/>
                  <mkvalue name="num" data_dword="0377"/>
                  <mkvalue name="bin" data_binary="0A00fF" data_dword="2" data_multistring="m" data_string="s"/>
                  <mkvalue name="emptyBinary" data_binary=""/>
                  <mkvalue name="unset"/>
                </mkkey>
                <delkey name="Old"/>
                <delkey name="Older"/>
              </hivestack>
            </registry></namespace></container>
            """);
        var registry = Assert.IsType<RegistryConfiguration>(description.Registry);
        Assert.Equal(new RegistrySymlink(@"\Registry\Key", @"\Registry\Target"), Assert.Single(registry.Symlinks));
        Assert.Equal(new RegistryRedirection(@"\Container")
        {
            Target = @"\Host", HiveStack = "SOFTWARE", AccessMask = uint.MaxValue, Trusted = true, ExitNode = true,
        }, Assert.Single(registry.Redirections));

        var stack = Assert.Single(registry.HiveStacks);
        Assert.Equal("SOFTWARE", stack.Name);
        Assert.Equal(new RegistryHive
        {
            ExistingPath = @"\Registry\Machine\Software", ReadOnly = true, Immutable = true,
            InheritTrust = true, TrustedHive = true, WriteThrough = true, FileAccessToken = 0xfffffffffffffabc,
        }, stack.Hives[0]);
        Assert.Equal(new RegistryHive
        {
            FilePath = @"C:\hive", MountId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        }, stack.Hives[1]);
        Assert.Equal(["Old", "Older"], stack.DeleteKeys);

        var key = Assert.Single(stack.Keys);
        Assert.Equal("Root", key.Name);
        Assert.Equal(@"\Registry\Machine", key.SecurityDescriptorSource);
        Assert.True(key.Volatile);
        var child = Assert.Single(key.Keys);
        Assert.Equal("Child", child.Name);
        Assert.False(child.Volatile);

        Assert.Collection(key.Values,
            value => AssertValue(value, "text", 1, Encoding.Unicode.GetBytes("a & b\0")),
            value => AssertValue(value, "multi", 7, Encoding.Unicode.GetBytes("one;two\0")),
            value => AssertValue(value, "num", 4, [255, 0, 0, 0]),
            value => AssertValue(value, "bin", 3, [10, 0, 255]),
            value => AssertValue(value, "emptyBinary", 3, []),
            value =>
            {
                Assert.Equal("unset", value.Name);
                Assert.False(value.HasPayload);
            });
    }

    [Fact]
    public void ParsesDeviceDeclarationsAndBothHostFilesPaths()
    {
        var description = SiloDescriptionXml.Parse("""
            <container><namespace>
              <device requires_driverstore="true">
                <device_category name="Category">
                  <interface_class type="ClassGuid" identifier="00112233-4455-6677-8899-aabbccddeeff"/>
                  <interface_class type="Inherit"/>
                </device_category>
                <device_instance id="Device ID" location_path="PCI(1)" port_name="COM1">
                  <interface_class type="DEVICEINSTANCE" recurse="false"/>
                </device_instance>
              </device>
              <hostfiles base_image_path="C:\base" sandbox_path="C:\sandbox">
                <file>a.dll</file><file>b.dll</file><file/>
              </hostfiles>
            </namespace></container>
            """);
        var device = Assert.IsType<DeviceConfiguration>(description.Device);
        Assert.True(device.RequiresDriverStore);
        var category = Assert.Single(device.Categories);
        Assert.Equal("Category", category.Name);
        Assert.Equal(
            [
                new DeviceInterfaceClass(DeviceInterfaceClassType.ClassGuid, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), true),
                new DeviceInterfaceClass(DeviceInterfaceClassType.Inherit, Guid.Empty, true),
            ], category.InterfaceClasses);
        var instance = Assert.Single(device.Instances);
        Assert.Equal("Device ID", instance.Id);
        Assert.Equal("PCI(1)", instance.LocationPath);
        Assert.Equal("COM1", instance.PortName);
        Assert.Equal(new DeviceInterfaceClass(DeviceInterfaceClassType.DeviceInstance, Guid.Empty, false), Assert.Single(instance.InterfaceClasses));
        var host = Assert.IsType<DownlevelConfiguration>(description.Downlevel);
        Assert.Equal(@"C:\base", host.BaseImagePath);
        Assert.Equal(@"C:\sandbox", host.ContainerRoot);
        Assert.Equal(["a.dll", "b.dll", ""], host.HostFiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<invalid/>")]
    [InlineData("<container invalid='1'/>")]
    [InlineData("<container xmlns='urn:unknown'/>")]
    [InlineData("<container><job/></container>")]
    [InlineData("<container><namespace/><NAMESPACE/></container>")]
    [InlineData("<container><namespace><job threadimpersonation='true' THREADIMPERSONATION='false'/></namespace></container>")]
    [InlineData("<container><namespace><job threadimpersonation='1'/></namespace></container>")]
    [InlineData("<container><namespace><job><threadimpersonation>true</threadimpersonation></job></namespace></container>")]
    [InlineData("<container><namespace><job><cpu rate='4294967296'/></job></namespace></container>")]
    [InlineData("<container><namespace><job><cpu rate='08'/></job></namespace></container>")]
    [InlineData("<container><namespace><job><cpu rate='1 '/></job></namespace></container>")]
    [InlineData("<container><namespace><job><terminationpolicy>KillOnLastHandleClose</terminationpolicy></job></namespace></container>")]
    [InlineData("<container><namespace><filesystem isolation='SparseHard'/></namespace></container>")]
    [InlineData("<container><namespace><filesystem><layer identifier='{00112233-4455-6677-8899-aabbccddeeff}'/></filesystem></namespace></container>")]
    [InlineData("<container><namespace><filesystem><bindings><filepath>A</filepath><FILEPATH>B</FILEPATH></bindings></filesystem></namespace></container>")]
    [InlineData("<container><namespace><registry><hivestack><mkkey><mkvalue data_binary='ABC'/></mkkey></hivestack></registry></namespace></container>")]
    [InlineData("<container><namespace><registry><hivestack><mkkey><mkvalue data_binary='zz'/></mkkey></hivestack></registry></namespace></container>")]
    [InlineData("<container><namespace><registry><hivestack><mkkey><mkvalue data_binary='12' data_dword='bad'/></mkkey></hivestack></registry></namespace></container>")]
    [InlineData("<container><?processing ignored?></container>")]
    [InlineData("<?processing ignored?><container/>")]
    [InlineData("<container>text</container>")]
    [InlineData("<container><namespace><hostfiles><file><name/></file></hostfiles></namespace></container>")]
    [InlineData("<container><namespace><hostfiles><file> <!-- split -->value</file></hostfiles></namespace></container>")]
    public void ReturnsWcSchemaErrorForInvalidDescription(string xml)
    {
        var error = Assert.Throws<SiloException>(() => SiloDescriptionXml.Parse(xml));
        Assert.Equal(0x5b9, error.Win32Error);
        Assert.Equal(unchecked((int)0x800705b9), error.HResult);
    }

    [Theory]
    [InlineData("<container>")]
    [InlineData("<container></other>")]
    [InlineData("<container a='1' a='2'/>")]
    [InlineData("<!DOCTYPE container [<!ENTITY value 'A'>]><container/>")]
    public void RejectsMalformedXmlAndDtds(string xml) =>
        Assert.Throws<XmlException>(() => SiloDescriptionXml.Parse(xml));

    [Fact]
    public void MatchesNativeStringAndIntegerBoundaries()
    {
        Assert.Equal(new SiloDescription(), SiloDescriptionXml.Parse("<container/>trailing & invalid"));
        Assert.Equal(new SiloDescription(), SiloDescriptionXml.Parse("<container></container>\0<bad"));
        Assert.Throws<ArgumentNullException>(() => SiloDescriptionXml.Parse(null!));
        var overflow = SiloDescriptionXml.Parse("""
            <container><namespace><job><cpu rate="" weight="0"/><memory limit="18446744073709551616"/>
            <terminationpolicy>PermanentQuiescent</terminationpolicy></job></namespace></container>
            """).Job!;
        Assert.Equal(new CpuConfiguration(), overflow.Cpu);
        Assert.Equal(ulong.MaxValue, overflow.MemoryLimit);
        Assert.False(overflow.KillOnLastHandleClose);
        var negative = SiloDescriptionXml.Parse("""
            <container><namespace><job><memory limit="-1"/></job></namespace></container>
            """).Job!;
        Assert.Equal(ulong.MaxValue, negative.MemoryLimit);
    }

    [Fact]
    public void RejectsElementDepthAboveTheXmlLiteDefault()
    {
        var directories = string.Concat(Enumerable.Repeat("<objdir>", 254))
            + string.Concat(Enumerable.Repeat("</objdir>", 254));
        var error = Assert.Throws<SiloException>(() => SiloDescriptionXml.Parse(
            "<container><namespace><ob>" + directories + "</ob></namespace></container>"));
        Assert.Equal(unchecked((int)0xc00cee81), error.HResult);
    }

    private static void AssertValue(RegistryValue value, string name, uint type, byte[] data)
    {
        Assert.True(value.HasPayload);
        Assert.Equal(name, value.Name);
        Assert.Equal(type, value.Type);
        Assert.Equal(data, value.Data);
    }
}
