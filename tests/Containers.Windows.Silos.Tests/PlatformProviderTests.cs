using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

// The event assertions capture process-wide provider errors, including errors from other tests.
[CollectionDefinition(nameof(ProviderEvents), DisableParallelization = true)]
public sealed class ProviderEvents;

[Collection(nameof(ProviderEvents))]
public sealed unsafe class PlatformProviderTests
{
    private const int Missing = unchecked((int)0xc0000034);
    private const int Denied = unchecked((int)0xc0000022);
    private const int Collision = unchecked((int)0xc0000035);
    private const int TooSmall = unchecked((int)0xc0000023);
    private const int Overflow = unchecked((int)0xc0000095);
    private const int CannotDelete = unchecked((int)0xc0000121);
    private const nint Job = 0x1234;
    private static readonly Guid Id = new Guid("1234abcd-789a-bcde-f123-abcdef102abc");

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x6dc000, 87)]
    [InlineData(0x6dc000, 5)]
    [InlineData(0x6d402c, 87)]
    [InlineData(0x6dc054, 87)]
    public void MountPacketsAndPerIoctlFailureRules(uint failedCode, int error)
    {
        var calls = new Provider();
        calls.DeviceErrors[failedCode] = error;
        var api = new NativeApi(calls);
        void Setup() => api.SetupMounts(Job, [new DriveMapping('x', "TΩ🙂"), new DriveMapping('y', "U")]);

        if (failedCode == 0 || (failedCode == 0x6dc000 && error == 87))
        {
            Setup();
            Assert.Equal(new uint[] { 0x6dc054, 0x6d402c, 0x6dc000, 0x6d402c, 0x6dc000 }, calls.Controls.Select(c => c.Code));
        }
        else
        {
            var failure = Assert.Throws<SiloException>(Setup);
            Assert.Equal(error, failure.Win32Error);
            Assert.Equal(failedCode, calls.Controls.Last().Code);
        }

        Assert.Equal((@"\\.\MountPointManager", 0xc0000000u, (nint)(-1)), calls.Opens[0]);
        Assert.Equal("3412000000000000", Convert.ToHexString(calls.Controls[0].Input));
        if (calls.Opens.Count > 1)
        {
            Assert.Equal((@"\\?\\Silos\7\Device\MountPointManager", 0xc0000000u, (nint)(-1)), calls.Opens[1]);
            Assert.Equal("08005400A9033DD842DE00000000", Convert.ToHexString(calls.Controls[1].Input));
        }

        if (calls.Controls.Count >= 3)
        {
            var names = Convert.ToHexString(Encoding.Unicode.GetBytes(@"\DosDevices\X:" + "TΩ🙂"));
            Assert.Equal("08001C0024000800" + names, Convert.ToHexString(calls.Controls[2].Input));
        }

        Assert.All(calls.Devices, handle => Assert.True(handle.IsClosed));
    }

    [Theory]
    [InlineData(0x104u)]
    [InlineData(0x10bu)]
    [InlineData(0x10000u)]
    public void ObjectLinkRejectsImpossibleRetrySizesAndReleasesItsSource(uint required)
    {
        using var log = new ProviderLog();
        var calls = new Object { SourceRequired = required };
        new NativeApi(calls).SetupObjects(Job, new ObjectNamespaceConfiguration
        {
            Symlinks = [new ObjectSymlink("copy") { TargetSource = @"\source" }, new ObjectSymlink("after") { Target = "direct" }]
        });
        Assert.Equal((ushort)0x104, Assert.Single(calls.SourceSizes));
        Assert.Equal(("after", "direct"), Assert.Single(calls.Links));
        Assert.Contains((nint)31, calls.Closed);
        Assert.Equal(TooSmall, Assert.Single(log.Events, e => e.EventName == "ObjectLinkSourceFailed").Payload![3]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PipeControlsHaveNullTemplatesAndPreserveFailureStatus(bool map, bool fail)
    {
        var calls = new Provider { PipeStatus = fail ? Denied : 0 };
        var api = new NativeApi(calls);
        void Setup() => api.SetupPipes(Job, map ? [new PipeMapping("A🙂", "B")] : []);
        if (fail)
            Assert.Equal(Denied, Assert.Throws<SiloException>(Setup).NtStatus);
        else
            Setup();

        Assert.Equal((@"\\.\pipe", 0x40000000u, (nint)0), calls.Opens[0]);
        Assert.Equal((0x118048u, "3412000000000000"), calls.PipeControls[0]);
        Assert.Equal(map ? 2 : 1, calls.Opens.Count);
        if (map)
        {
            Assert.Equal((@"\\?\\Silos\7\Device\NamedPipe", 0x40000000u, (nint)0), calls.Opens[1]);
            Assert.Equal((0x11004cu, "0C000600120002000100000041003DD842DE4200"), calls.PipeControls[1]);
        }

        Assert.All(calls.Devices, handle => Assert.True(handle.IsClosed));
    }

    [Fact]
    public void PipeLimitsRejectEachOversizeFieldWithNativeStatus()
    {
        var calls = new Provider();
        var api = new NativeApi(calls);
        api.SetupPipes(Job, [new PipeMapping(new string('n', 0xfff3 / 2), new string('t', 0xffff / 2))]);
        Assert.StartsWith("0C00F2FFFEFFFEFF01000000", calls.PipeControls[1].Input);
        Assert.Equal(unchecked((int)0xc000000d), Assert.Throws<SiloException>(() => api.SetupPipes(Job,
            [new PipeMapping(new string('n', 0xfff3 / 2 + 1), "t")])).NtStatus);
        Assert.Equal(unchecked((int)0xc000000d), Assert.Throws<SiloException>(() => api.SetupPipes(Job,
            [new PipeMapping("n", new string('t', 0xffff / 2 + 1))])).NtStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryDetachFailureIsDiagnosticAndReleasesOnlyItsDevice(bool openFails)
    {
        using var log = new ProviderLog();
        var calls = new Registry { OpenStatus = openFails ? Denied : 0 };
        calls.DeviceErrors[0x220018] = 5;
        var api = new NativeApi(calls);

        api.CleanupRegistry(Job);
        Assert.Empty(calls.Closed);
        var item = Assert.Single(log.Events, e => e.EventName == "RegistryCleanupFailed");
        Assert.Equal(openFails ? Denied : 0, item.Payload![1]);
        Assert.Equal(openFails ? 0 : 5, item.Payload[2]);
        var expectedHResult = openFails && !OperatingSystem.IsWindows()
            ? SiloException.MapNtStatus(Denied, 0)
            : SiloException.FromWin32(5);
        Assert.Equal(expectedHResult, item.Payload[3]);
        if (openFails)
            Assert.Empty(calls.Devices);
        else
            Assert.True(Assert.Single(calls.Devices).IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrySetupArmsRollbackOnlyAfterRegistration(bool registered)
    {
        using var log = new ProviderLog();
        var calls = new Registry();
        calls.DeviceErrors[registered ? 0x22000cu : 0x220004u] = 5;
        calls.DeviceErrors[0x220018] = 32;
        var api = new NativeApi(calls);
        var error = Assert.Throws<SiloException>(() =>
            api.SetupRegistry(Job, new RegistryConfiguration { Redirections = [new RegistryRedirection("a") { Target = "b" }] }));

        Assert.Equal(5, error.Win32Error);
        Assert.Equal(registered ? new uint[] { 0x220004, 0x22000c, 0x220018 } : [0x220004],
            calls.Controls.Select(c => c.Code));
        Assert.Equal(registered ? 1 : 0, log.Events.Count(e => e.EventName == "RegistryCleanupFailed"));
        Assert.All(calls.Controls, call => Assert.Equal((long)Job, BinaryPrimitives.ReadInt64LittleEndian(call.Input)));
        Assert.All(calls.Devices, handle => Assert.True(handle.IsClosed));
        Assert.Empty(calls.Closed);
    }

    [Fact]
    public void RootAndHiveUseLowercaseUnbracedGuidAndAppendLiteralSuffix()
    {
        const string expected = @"\Registry\WC\Silo1234abcd-789a-bcde-f123-abcdef102abc";
        var calls = new Registry();
        var api = new NativeApi(calls);
        using var job = new SafeFileHandle(Job, ownsHandle: false);
        using var silo = WindowsSilo.ConfigureJob(job, new SiloDescription(), server: false, api);
        Assert.Equal(expected + @"\Suffix", silo.GetRegistryRootPath(@"\Suffix"));

        var stack = new RegistryHiveStack("StÅck🙂") { Hives = [new RegistryHive { MountId = Id, FilePath = "a" }] };
        api.SetupRegistry(Job, new RegistryConfiguration
        {
            HiveStacks = [stack],
            Redirections = [new RegistryRedirection("b") { HiveStack = "sTåCK🙂" }]
        });
        var mounted = expected + stack.Name;
        var hive = Assert.Single(calls.Controls, c => c.Code == 0x220008).Input;
        Assert.Equal(mounted.Length * 2, BinaryPrimitives.ReadUInt16LittleEndian(hive.AsSpan(0x14)));
        Assert.Equal(Encoding.Unicode.GetBytes(mounted + "a"), hive[0x28..]);
        var redirect = Assert.Single(calls.Controls, c => c.Code == 0x22000c).Input;
        Assert.Equal(mounted.Length * 2, BinaryPrimitives.ReadUInt16LittleEndian(redirect.AsSpan(0x0a)));
        Assert.Equal(Encoding.Unicode.GetBytes("b" + mounted), redirect[0x14..]);
        Assert.Equal("StÅck🙂sTåCK🙂", new string(calls.Lowered.ToArray()));
    }

    [Fact]
    public void RegistryWordLimitsAndAggregateAllocationPreserveNativeOverflow()
    {
        var maximum = new string('x', 0x7fff);
        var excessive = maximum + "x";
        var redirect = NativeApi.PackRegistryRedirection(Job, new RegistryRedirection(maximum), maximum);
        Assert.Equal("FEFFFEFF", Convert.ToHexString(redirect.AsSpan(8, 4)));
        var records = NativeApi.PackRuntimeVirtualKeys(Job, [new RuntimeVirtualKey(maximum, "A"), new RuntimeVirtualKey("B", maximum)]);
        Assert.Equal("FEFF020000000000", Convert.ToHexString(records.AsSpan(16, 8)));
        Assert.Equal("0200FEFF00000000", Convert.ToHexString(records.AsSpan(16 + 12 + 0xfffe + 2, 8)));
        Assert.Equal(Encoding.Unicode.GetBytes(maximum), records[^0xfffe..]);

        Assert.Equal(Overflow, Assert.Throws<SiloException>(() => NativeApi.PackRegistryRedirection(Job, new RegistryRedirection(excessive), "")).NtStatus);
        Assert.Equal(Overflow, Assert.Throws<SiloException>(() => NativeApi.PackRegistryRedirection(Job, new RegistryRedirection(""), excessive)).NtStatus);
        Assert.Equal(Overflow, Assert.Throws<SiloException>(() => NativeApi.PackRuntimeVirtualKeys(Job, [new RuntimeVirtualKey(excessive, "")])).NtStatus);
        Assert.Equal(Overflow, Assert.Throws<SiloException>(() => NativeApi.PackRuntimeVirtualKeys(Job, [new RuntimeVirtualKey("", excessive)])).NtStatus);
        var repeated = Enumerable.Repeat(new RuntimeVirtualKey(maximum, maximum), 17000).ToArray();
        Assert.Equal(Overflow, Assert.Throws<SiloException>(() => NativeApi.PackRuntimeVirtualKeys(Job, repeated)).NtStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Missing)]
    [InlineData(TooSmall)]
    public void RegistryEnumerationUsesCapturedCountAndIncreasingIndices(int status)
    {
        var calls = new Registry { TreeOpenStatus = status == Missing ? status : 0, EnumerationStatus = status == TooSmall ? status : 0 };
        var api = new NativeApi(calls);
        var config = new RegistryConfiguration
        {
            HiveStacks = [new RegistryHiveStack("tree") { Hives = [new RegistryHive { ExistingPath = @"\Base" }], DeleteKeys = ["parent"] }]
        };
        if (status == Missing)
        {
            api.SetupRegistry(Job, config);
            Assert.Empty(calls.Indices);
            Assert.Empty(calls.Deleted);
        }
        else
        {
            Assert.Equal(status == TooSmall ? TooSmall : CannotDelete,
                Assert.Throws<SiloException>(() => api.SetupRegistry(Job, config)).NtStatus);
            Assert.Equal(status == TooSmall ? new uint[] { 0 } : [0, 1, 2], calls.Indices);
            Assert.Equal(status == TooSmall ? [] : new[] { @"\Base\parent\A", @"\Base\parent\C", @"\Base\parent" }, calls.Deleted);
        }
    }

    [Fact]
    public void RegistryKeyCreateAndLinkUseDistinctDispositionContracts()
    {
        var calls = new Registry();
        new NativeApi(calls).SetupRegistry(Job, new RegistryConfiguration
        {
            HiveStacks = [new RegistryHiveStack("tree") { Hives = [new RegistryHive { ExistingPath = @"\Base" }], Keys = [new RegistryKeyConfiguration("key")] }],
            Symlinks = [new RegistrySymlink("link", "dest")]
        });
        Assert.Equal(new[] { ("key", true), ("link", false), ("link", false) }, calls.KeyCreates);
        Assert.Equal("link", Assert.Single(calls.Deleted));
        Assert.Equal(("SymbolicLinkValue", 6u, "6400650073007400"), calls.WrittenValue);
        Assert.Contains((nint)65, calls.Closed);
    }

    [Theory]
    [InlineData(Missing)]
    [InlineData(Denied)]
    [InlineData(0)]
    public void ObjectLinkSourceFailuresSkipOnlyTheCurrentLink(int status)
    {
        using var log = new ProviderLog();
        var calls = new Object { SourceStatus = status };
        var config = new ObjectNamespaceConfiguration
        {
            Symlinks = [new ObjectSymlink("copy") { TargetSource = @"\source" }, new ObjectSymlink("after") { Target = @"\direct" }]
        };
        new NativeApi(calls).SetupObjects(Job, config);
        if (status == 0)
        {
            Assert.Equal(new[] { (ushort)0x104, (ushort)0x10a }, calls.SourceSizes);
            Assert.Equal(("copy", "resolved"), calls.Links[0]);
            Assert.Contains((nint)31, calls.Closed);
            Assert.DoesNotContain(log.Events, e => e.EventName == "ObjectLinkSourceFailed");
        }
        else
        {
            Assert.Empty(calls.SourceSizes);
            var item = Assert.Single(log.Events, e => e.EventName == "ObjectLinkSourceFailed");
            Assert.Equal("copy", item.Payload![0]);
            Assert.Equal(@"\source", item.Payload[1]);
            Assert.Equal(status, item.Payload[3]);
        }

        Assert.Equal(("after", @"\direct"), calls.Links.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectLinkGuardsItsOutputAndPropagatesScopeFailure(bool collision)
    {
        var calls = new Object { LinkStatus = collision ? Collision : 0, ScopeStatus = Denied };
        var config = new ObjectNamespaceConfiguration
        {
            Symlinks = [new ObjectSymlink("scoped") { TargetSource = @"\source", Scope = SymlinkScope.GlobalDropSilo }]
        };
        void Setup() => new NativeApi(calls).SetupObjects(Job, config);
        if (collision)
        {
            Setup();
            Assert.Empty(calls.LinkClasses);
        }
        else
        {
            var error = Assert.Throws<SiloException>(Setup);
            Assert.Equal(Denied, error.NtStatus);
            Assert.Equal("SetSymbolicLinkGlobal", error.Operation);
            Assert.Equal((1, 1u, 4u), Assert.Single(calls.LinkClasses));
        }

        Assert.Equal(new nint[] { 31, 44, 20 }, calls.Closed);
    }

    [Fact]
    public void ExplicitEmptyCloneSdPreservesXmlPresenceAndSkipsSourceOpen()
    {
        var parsed = SiloDescriptionXml.Parse("<container><namespace><ob><objdir name='private' clonesd=''/></ob></namespace></container>");
        var objects = Assert.IsType<ObjectNamespaceConfiguration>(parsed.ObjectNamespace);
        Assert.Equal("", Assert.Single(objects.Directories).SecurityDescriptorSource);
        var calls = new Object();
        new NativeApi(calls).SetupObjects(Job, objects);
        Assert.Equal(new[] { "create:private:0", "close:42", "close:20" }, calls.Trace);
        Assert.Equal(0, calls.SecurityCalls);
    }

    [Theory]
    [InlineData(false, null, 0)]
    [InlineData(true, null, 1)]
    [InlineData(false, DirectoryShadow.None, 2)]
    [InlineData(true, DirectoryShadow.IfExists, 7)]
    public void ObjectRootFlagsOccupyTheLowDwordOfTheClass37Packet(bool shadowRoot, DirectoryShadow? global, uint flags)
    {
        var calls = new Object();
        new NativeApi(calls).SetupObjects(Job, new ObjectNamespaceConfiguration
        {
            ShadowRoot = shadowRoot,
            Directories = global.HasValue ? [new ObjectDirectory("Global??") { Shadow = global.Value }] : []
        });

        var expected = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, flags);
        Assert.Equal(expected, calls.RootPacket);
    }

    [Fact]
    public void ObjectShadowClosesBeforeChildrenAndUnexpectedSecuritySizeStopsQuery()
    {
        var calls = new Object();
        var api = new NativeApi(calls);
        api.SetupObjects(Job, new ObjectNamespaceConfiguration
        {
            Directories = [new ObjectDirectory("shadow") { Shadow = DirectoryShadow.Required, Directories = [new ObjectDirectory("child")] }]
        });
        Assert.Equal(new[] { "create:shadow:30", "close:30", "create:child:0", "close:42", "close:41", "close:20" }, calls.Trace);

        using var log = new ProviderLog();
        calls.SecuritySizeStatus = 1;
        var error = Assert.Throws<InvalidDataException>(() => api.SetupObjects(Job,
            new ObjectNamespaceConfiguration { Symlinks = [new ObjectSymlink("name") { Target = "path" }] }));
        Assert.Equal(1, error.Data["NtStatus"]);
        Assert.Equal(1, calls.SecurityCalls);
        Assert.Contains((nint)32, calls.Closed);
        Assert.Equal(1, Assert.Single(log.Events, e => e.EventName == "UnexpectedSecuritySizeStatus").Payload![0]);
    }

    [Fact]
    public void NetworkRollbackAndCleanupLogInNativeOrder()
    {
        var calls = new Provider { BindError = 5, DeleteError = 32, Compartment = 37 };
        using var log = new ProviderLog(entry =>
        {
            if (entry.EventName == "NetworkCleanupFailed")
                calls.Trace.Add("event:" + entry.Payload![1]);
        });
        var api = new NativeApi(calls);

        var error = Assert.Throws<SiloException>(() => api.SetupNetwork(Job, "valid"));
        Assert.Equal("SetJobCompartmentId", error.Operation);
        Assert.Equal(5, error.Win32Error);
        Assert.Equal(32u, error.Data["DeleteCompartmentError"]);
        Assert.Equal(new[] { "initialize", "create:valid", "set:37", "delete:37", "event:DeleteCompartment" }, calls.Trace);
        calls.Trace.Clear();
        api.CleanupNetwork(Job);
        Assert.Equal(new[] { "get", "set:0", "event:SetJobCompartmentId", "delete:37", "event:DeleteCompartment" }, calls.Trace);
        calls.Trace.Clear();
        calls.Compartment = 1;
        api.CleanupNetwork(Job);
        Assert.Equal("get", Assert.Single(calls.Trace));
        Assert.Empty(calls.Closed);
    }

    private class Provider : NativeApi.PlatformProvider
    {
        internal readonly List<(string Path, uint Access, nint Template)> Opens = [];
        internal readonly List<SafeFileHandle> Devices = [];
        internal readonly List<(uint Code, byte[] Input)> Controls = [];
        internal readonly List<(uint Code, string Input)> PipeControls = [];
        internal readonly List<nint> Closed = [];
        internal readonly List<string> Trace = [];
        internal readonly Dictionary<uint, int> DeviceErrors = [];
        internal int PipeStatus;
        internal uint Compartment = 1;
        internal uint BindError;
        internal uint DeleteError;

        internal SafeFileHandle NewDevice()
        {
            var handle = new SafeFileHandle(0x100 + Devices.Count, ownsHandle: false);
            Devices.Add(handle);
            return handle;
        }

        internal override SafeFileHandle OpenDevice(string name, uint access, nint templateHandle)
        {
            Opens.Add((name, access, templateHandle));
            return NewDevice();
        }

        internal override int DeviceControl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
        {
            Assert.False(handle.IsClosed);
            Controls.Add((code, input.ToArray()));
            return DeviceErrors.GetValueOrDefault(code);
        }

        internal override int FsControl(SafeFileHandle handle, uint code, Span<byte> input)
        {
            Assert.False(handle.IsClosed);
            PipeControls.Add((code, Convert.ToHexString(input)));
            return code == 0x11004c ? PipeStatus : 0;
        }

        internal override int SetJob(nint job, int informationClass, void* data, uint length)
        {
            Assert.Equal(Job, job);
            return 0;
        }

        internal override int QueryJob(nint job, int informationClass, void* data, uint length, uint* returned)
        {
            Assert.Equal(Job, job);
            if (informationClass == 0x1e)
            {
                Assert.True(Id.TryWriteBytes(new Span<byte>(data, (int)length)));
            }
            else
            {
                Assert.Equal(0x25, informationClass);
                Assert.True(returned != null);
                Assert.Equal(0x34u, length);
                const string root = @"\Silos\7";
                var buffer = (char*)((byte*)data + 0x10);
                root.AsSpan().CopyTo(new Span<char>(buffer, root.Length));
                *(UnicodeString*)data = new UnicodeString(buffer, root.Length);
                *returned = 0x34;
            }

            return 0;
        }

        internal override char UpperCharacter(char value) => char.ToUpperInvariant(value);
        internal override void Close(nint handle) => Closed.Add(handle);

        internal override void InitializeCompartment(out CompartmentEntry entry)
        {
            entry = default;
            fixed (CompartmentEntry* pointer = &entry)
            {
                new Span<byte>(pointer, 0x21c).Fill(0xff);
                pointer->Id = 0;
                pointer->Description[0] = '\0';
            }

            Trace.Add("initialize");
        }

        internal override uint CreateCompartment(CompartmentEntry* entry)
        {
            Assert.Equal(Id, entry->Guid);
            Assert.Equal(0xff, entry->Flags);
            var description = new ReadOnlySpan<char>(entry->Description, 257);
            var terminator = description.IndexOf('\0');
            Assert.InRange(terminator, 0, 256);
            Trace.Add("create:" + new string(description[..terminator]));
            entry->Id = Compartment;
            return 0;
        }

        internal override uint GetCompartment(nint job)
        {
            Assert.Equal(Job, job);
            Trace.Add("get");
            return Compartment;
        }

        internal override uint SetCompartment(nint job, uint id)
        {
            Assert.Equal(Job, job);
            Trace.Add("set:" + id);
            return BindError;
        }

        internal override uint DeleteCompartment(uint id)
        {
            Trace.Add("delete:" + id);
            return DeleteError;
        }
    }

    private sealed class Registry : Provider
    {
        internal int OpenStatus;
        internal int TreeOpenStatus;
        internal int EnumerationStatus;
        internal readonly List<char> Lowered = [];
        internal readonly List<uint> Indices = [];
        internal readonly List<string> Deleted = [];
        internal readonly List<(string Name, bool DispositionIsNull)> KeyCreates = [];
        internal (string Name, uint Type, string Bytes) WrittenValue;
        private readonly Dictionary<nint, string> keys = [];
        private readonly List<string> children = ["A", "B", "C"];

        internal override SafeFileHandle OpenRegistryDriver()
        {
            SiloException.CheckNt(OpenStatus, "Open VRegDriver");
            return NewDevice();
        }

        internal override char LowerCharacter(char value)
        {
            Lowered.Add(value);
            return char.ToLowerInvariant(value);
        }

        internal override int OpenKey(out nint key, uint access, ObjectAttributes* attributes)
        {
            var name = Read(attributes->ObjectName);
            key = 0;
            if (name == @"\Base\parent" && TreeOpenStatus != 0)
                return TreeOpenStatus;

            key = 50 + keys.Count;
            keys.Add(key, name);
            return 0;
        }

        internal override int QueryKey(nint key, int informationClass, void* data, uint length, out uint needed)
        {
            Assert.Equal(4, informationClass);
            Assert.Equal(0x28u, length);
            if (keys[key] == @"\Base\parent")
            {
                *((uint*)data + 3) = (uint)children.Count;
                *((uint*)data + 4) = 2;
            }

            needed = 0x28;
            return 0;
        }

        internal override int EnumerateKey(nint key, uint index, int informationClass, void* data, uint length, out uint needed)
        {
            Assert.Equal(@"\Base\parent", keys[key]);
            Assert.Equal(0, informationClass);
            Assert.Equal(0x1au, length);
            Indices.Add(index);
            needed = 0;
            if (EnumerationStatus != 0)
                return EnumerationStatus;
            if (index >= children.Count)
                return unchecked((int)0x8000001a);

            *((uint*)data + 3) = 2;
            *((char*)data + 8) = children[(int)index][0];
            needed = 0x12;
            return 0;
        }

        internal override int DeleteKey(nint key)
        {
            var name = keys[key];
            Deleted.Add(name);
            if (name == @"\Base\parent")
                return children.Count == 0 ? 0 : CannotDelete;

            if (name.Length == @"\Base\parent\".Length + 1)
                children.Remove(name[^1..]);
            return 0;
        }

        internal override int CreateKey(nint* key, uint access, ObjectAttributes* attributes, uint options, uint* disposition)
        {
            Assert.Equal(0, *key);
            var name = Read(attributes->ObjectName);
            KeyCreates.Add((name, disposition == null));
            if (disposition == null)
            {
                Assert.Equal(0xf003fu, access);
                *key = 65;
                return Collision;
            }

            Assert.Equal(0x10022u, access);
            Assert.Equal(3u, options);
            Assert.Equal(0u, *disposition);
            *key = KeyCreates.Count == 2 ? 66 : 67;
            *disposition = KeyCreates.Count == 2 ? 2u : 1u;
            keys.Add(*key, name);
            return 0;
        }

        internal override int SetValue(nint key, UnicodeString* name, uint type, void* data, uint length)
        {
            Assert.Equal(67, key);
            WrittenValue = (Read(name), type, Convert.ToHexString(new ReadOnlySpan<byte>(data, (int)length)));
            return 0;
        }
    }

    private sealed class Object : Provider
    {
        internal int SourceStatus;
        internal uint SourceRequired = 0x10a;
        internal int LinkStatus;
        internal int ScopeStatus;
        internal int? SecuritySizeStatus;
        internal int SecurityCalls;
        internal readonly List<ushort> SourceSizes = [];
        internal readonly List<(string Name, string Destination)> Links = [];
        internal readonly List<(int Class, uint Value, uint Size)> LinkClasses = [];

        internal byte[]? RootPacket;

        internal override int SetJob(nint job, int informationClass, void* data, uint length)
        {
            Assert.Equal(37, informationClass);
            RootPacket = new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            return base.SetJob(job, informationClass, data, length);
        }

        internal override int OpenDirectory(out nint handle, uint access, ObjectAttributes* attributes)
        {
            handle = Read(attributes->ObjectName) switch
            {
                @"\Silos\7" => 20,
                @"\shadow" => 30,
                @"\" when SecuritySizeStatus.HasValue => 32,
                _ => 0
            };
            return handle == 0 ? Missing : 0;
        }

        internal override int CreateDirectory(out nint handle, uint access, ObjectAttributes* attributes, nint shadow, uint flags)
        {
            var name = Read(attributes->ObjectName);
            Trace.Add($"create:{name}:{shadow}");
            handle = name == "shadow" ? 41 : 42;
            return 0;
        }

        internal override void Close(nint handle)
        {
            base.Close(handle);
            Trace.Add("close:" + handle);
        }

        internal override int OpenObjectLink(out nint handle, uint access, ObjectAttributes* attributes)
        {
            Assert.Equal(@"\source", Read(attributes->ObjectName));
            Assert.Equal(1u, access);
            handle = SourceStatus == 0 ? 31 : 0;
            return SourceStatus;
        }

        internal override int QueryObjectLink(nint handle, UnicodeString* target, uint* length)
        {
            Assert.Equal(31, handle);
            Assert.Equal((uint)target->MaximumLength, *length);
            SourceSizes.Add(target->MaximumLength);
            if (target->MaximumLength == 0x104)
            {
                *length = SourceRequired;
                return TooSmall;
            }

            const string value = "resolved";
            value.AsSpan().CopyTo(new Span<char>(target->Buffer, target->MaximumLength / 2));
            target->Length = (ushort)(value.Length * 2);
            return 0;
        }

        internal override int CreateObjectLink(nint* handle, uint access, ObjectAttributes* attributes, UnicodeString* target)
        {
            Assert.Equal(0, *handle);
            *handle = 44;
            Links.Add((Read(attributes->ObjectName), Read(target)));
            return LinkStatus;
        }

        internal override int SetObjectLink(nint handle, int informationClass, void* data, uint size)
        {
            LinkClasses.Add((informationClass, *(uint*)data, size));
            return ScopeStatus;
        }

        internal override int QuerySecurity(nint handle, uint information, void* data, uint size, out uint needed)
        {
            Assert.Equal(0xfu, information);
            Assert.True(data == null);
            Assert.Equal(0u, size);
            SecurityCalls++;
            needed = 0;
            return SecuritySizeStatus!.Value;
        }
    }

    private static string Read(UnicodeString* value) => new string(value->Buffer, 0, value->Length / 2);

    private sealed class ProviderLog(Action<EventWrittenEventArgs>? record = null) : EventListener
    {
        internal readonly ConcurrentQueue<EventWrittenEventArgs> Events = new ConcurrentQueue<EventWrittenEventArgs>();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "Containers-Windows-Silos")
                EnableEvents(eventSource, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            Events.Enqueue(eventData);
            record?.Invoke(eventData);
        }
    }
}