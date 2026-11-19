using Windows.Win32.Storage.FileSystem;
using Containers.Windows.Silos.DescriptionModel;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class HostFileFixupTests
{
    private const string Host = @"C:\Windows\";
    private const string Container = @"D:\scratch\Windows\";
    private const string Catalogs = @"System32\CatRoot\{F750E6C3-38EE-11D1-85E5-00C04FC295EE}\";

    [Fact]
    public void FixupsProbeAndBindInNativeOrder()
    {
        var calls = new Calls();
        var api = new NativeApi(calls);
        api.InjectHostFiles(17, new DownlevelConfiguration(@"D:\scratch", ["a", "missing", "no-source", "b", "c"]));
        Assert.Equal([Host + "a", Host + "missing", Host + "no-source", Host + "b", Host + "c"], calls.Hints);
        Assert.Equal(@"D:\scratch\", calls.Attached);
        string[] candidates = ["a", Catalogs + "A.cat", "missing", "no-source", "b", "c", Catalogs + "a.cat"];
#if SILO2025
        Assert.Equal(candidates.SelectMany(name => name == "missing" ? [Container + name] : new[] { Container + name, Host + name }), calls.Probes);
        Assert.All(calls.Flags, flag => Assert.Equal(0u, flag));
        var mapped = candidates.Where(name => name != "missing" && name != "no-source");
#else
        Assert.Equal(candidates.Select(name => Container + name), calls.Probes);
        Assert.All(calls.Flags, flag => Assert.Equal(4u, flag));
        var mapped = candidates.Where(name => name != "missing");
#endif
        Assert.Equal(mapped.Select(name => (Container + name, Host + name)), calls.Bindings);
    }

    [Fact]
    public void AFailedBindingStopsTheRemainingMappings()
    {
        var calls = new Calls { FailBinding = "d" };
        var error = Assert.Throws<SiloException>(() => new NativeApi(calls).InjectHostFiles(17, new DownlevelConfiguration(@"D:\scratch", ["x", "d", "e"])));
        Assert.Equal(unchecked((int)0x80070005), error.HResult);
        Assert.Equal([Container + "x", Container + "d"], calls.Bindings.Select(pair => pair.Destination));
    }

    [Fact]
    public void MalformedCatalogUtf8FailsAndAnAbsentEaAddsNoMapping()
    {
        var ea = new byte[32];
        Assert.Null(NativeApi.DecodeCatalogHint(ea));
        ea[5] = 15;
        ea[6] = 5;
        ea[24] = 1;
        ea[26] = 1;
        ea[28] = 255;
        Assert.Equal(unchecked((int)0xc00000e5), Assert.Throws<SiloException>(() => NativeApi.DecodeCatalogHint(ea)).NtStatus);
    }

    [Fact]
    public void CatalogHintValueStartsAfterTheTerminatedEaName()
    {
        // Eight-byte FILE_FULL_EA_INFORMATION prefix, the 15-byte EA name and its NUL.
        byte[] entry = [
            0, 0, 0, 0, 0, 15, 6, 0,
            .. "$CI.CatalogHint"u8, 0,
            1, 0, 2, 0, 0xc3, 0xa9
        ];
        Assert.Equal("é\0", NativeApi.DecodeCatalogHint(entry));
        Assert.Null(NativeApi.DecodeCatalogHint(entry.AsSpan(0, entry.Length - 1)));
    }

    [Fact]
    public void SystemRootPassesANulTerminatedUnicodeStringToClass45()
    {
        var calls = new Calls();
        new NativeApi(calls).SetSystemRoot(17, "A\0B");
        Assert.Equal(("A", (ushort)2, (ushort)4), calls.SystemRoot);
    }

#if !SILO2025
    [Fact]
    public void UtilityVmSkipsAllHostFileWork()
    {
        var calls = new Calls { UtilityVm = true };
        new NativeApi(calls).InjectHostFiles(17, new DownlevelConfiguration(@"D:\scratch", ["a"]));
        Assert.Empty(calls.Hints);
        Assert.Null(calls.Attached);
    }
#endif

    private sealed unsafe class Calls : NativeApi.PlatformProvider
    {
        internal readonly List<string> Hints = [];
        internal readonly List<string> Probes = [];
        internal readonly List<uint> Flags = [];
        internal readonly List<(string Destination, string Source)> Bindings = [];
        internal (string Text, ushort Length, ushort Capacity) SystemRoot;
        internal string? Attached;
        internal string? FailBinding { get; init; }
#if !SILO2025
        internal bool UtilityVm { get; init; }
        internal override bool IsUtilityVm() => UtilityVm;
#endif
        internal override string GetHostWindowsDirectory() => Host;
        internal override string? GetCatalogHint(string path)
        {
            Hints.Add(path);
            return path switch { Host + "a" or Host + "b" => "A.cat", Host + "c" => "a.cat", _ => null };
        }

        internal override int AttachHostFileFilter(string volume)
        {
            Attached = volume;
            return 0;
        }

        internal override bool FindFixupFile(string path, FIND_FIRST_EX_FLAGS flags)
        {
            Flags.Add((uint)flags);
            Probes.Add(path);
            return path != Container + "missing" && path != Host + "no-source";
        }

        internal override int BindHostFile(nint job, string destination, string source)
        {
            Assert.Equal((nint)17, job);
            Bindings.Add((destination, source));
            return destination == Container + FailBinding ? unchecked((int)0x80070005) : 0;
        }

        internal override int SetJob(nint job, int informationClass, void* data, uint length)
        {
            Assert.Equal(45, informationClass);
            Assert.Equal(16u, length);
            var name = *(UnicodeString*)data;
            SystemRoot = (new string(name.Buffer, 0, name.Length / 2), name.Length, name.MaximumLength);
            return 0;
        }
    }
}
