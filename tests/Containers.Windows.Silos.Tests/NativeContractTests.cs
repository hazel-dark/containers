using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class NativeContractTests
{
    private static readonly nint RegistryJob = unchecked((nint)0xFEDCBA9876543210UL);

    [Fact]
    public unsafe void X64LayoutsMatchRetainedCallSites()
    {
        Assert.Equal(0x10, Unsafe.SizeOf<UnicodeString>());
        Assert.Equal(0x08, Marshal.OffsetOf<UnicodeString>(nameof(UnicodeString.Buffer)));
        Assert.Equal(0x30, Unsafe.SizeOf<ObjectAttributes>());
        Assert.Equal(0x20, Marshal.OffsetOf<ObjectAttributes>(nameof(ObjectAttributes.SecurityDescriptor)));
        Assert.Equal(0x20, Unsafe.SizeOf<RelativeNtName>());
        Assert.Equal(0x10, Unsafe.SizeOf<IoStatusBlock>());
        Assert.Equal(0x98, Unsafe.SizeOf<ExtendedLimitInformationV2>());
        Assert.Equal(0x90, Marshal.OffsetOf<ExtendedLimitInformationV2>(nameof(ExtendedLimitInformationV2.JobTotalMemoryLimit)));
        Assert.Equal(0x10, Unsafe.SizeOf<SiloObjectRootDirectory>());
        Assert.Equal(0x10, Unsafe.SizeOf<ServerSiloInitialize>());
        Assert.Equal(0x08, Marshal.OffsetOf<ServerSiloInitialize>(nameof(ServerSiloInitialize.Downlevel)));
        Assert.Equal(0x148, Unsafe.SizeOf<SmMessage>());
        Assert.Equal(0x28, Marshal.OffsetOf<SmMessage>(nameof(SmMessage.ApiNumber)));
        Assert.Equal(0x2c, Marshal.OffsetOf<SmMessage>(nameof(SmMessage.ReturnValue)));
        Assert.Equal(0x30, Marshal.OffsetOf<SmMessage>(nameof(SmMessage.Silo)));
        Assert.Equal(0x38, Marshal.OffsetOf<SmMessage>(nameof(SmMessage.Standalone)));
        Assert.Equal(0x220, Unsafe.SizeOf<CompartmentEntry>());
        Assert.Equal(0x218, Marshal.OffsetOf<CompartmentEntry>(nameof(CompartmentEntry.Flags)));
        Assert.Equal(0x20, Unsafe.SizeOf<WciLayerDescriptor>());
        Assert.Equal(0x18, Marshal.OffsetOf<WciLayerDescriptor>(nameof(WciLayerDescriptor.Path)));
        Assert.Equal(0x38, Unsafe.SizeOf<RegistryQueryEntry>());
        Assert.Equal(0x70, Unsafe.SizeOf<StartupInfoEx>());
        Assert.Equal(0x68, Marshal.OffsetOf<StartupInfoEx>(nameof(StartupInfoEx.Attributes)));
        Assert.Equal(0x18, Unsafe.SizeOf<global::Windows.Win32.System.Threading.PROCESS_INFORMATION>());
    }

    [Fact]
    public void NotificationAbiIncludesBothCompletionValueAndStatus()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var assign = typeof(NativeMethods).GetMethod(nameof(NativeMethods.TpAllocJobNotification), flags)!;
        var callback = assign.GetParameters()[2].ParameterType;
        Assert.True(callback.IsUnmanagedFunctionPointer);
        Type[] args = [typeof(nint), typeof(nint), typeof(nint), typeof(nuint), typeof(nint), typeof(int)];
        Assert.Equal(args, callback.GetFunctionPointerParameterTypes());
        Assert.Equal(args, typeof(SiloTerminationNotification).GetMethod("OnNativeNotification", flags)!.GetParameters().Select(param => param.ParameterType));
    }

    [Fact]
    public void BindSetupUsesDwordFlagsAndExceptionCount()
    {
        var method = typeof(NativeMethods).GetMethod(nameof(NativeMethods.BfSetupFilter), BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(uint), method.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(uint), method.GetParameters()[5].ParameterType);
    }

    [Theory]
    [InlineData(false, 4, 0, SiloTerminationReason.NormalExit)]
    [InlineData(true, 4, unchecked((int)0x80000031), null)]
    [InlineData(true, 1, unchecked((int)0xc00002eb), SiloTerminationReason.NormalExit)]
    [InlineData(true, 1, unchecked((int)0x80000031), SiloTerminationReason.Reboot)]
    [InlineData(true, 1, unchecked((int)0xc000000d), SiloTerminationReason.UnexpectedExit)]
    public void NotificationClassifiesTheLowDwordOfTheCompletionValue(bool server, int code, int value, SiloTerminationReason? reason)
        => Assert.Equal(reason, SiloTerminationNotification.Classify(server, (nuint)code, value));

    [Fact]
    public void RegIoctlBuffersCarryTheJobAndTightlyPackedUtf16()
    {
        var hive = new RegistryHive
        {
            FilePath = "FILE", ReadOnly = true, Immutable = true,
            InheritTrust = true, TrustedHive = true, WriteThrough = true, FileAccessToken = 0x89ABCDEF01234567
        };
        var stack = NativeApi.PackHive(RegistryJob, hive, "NEW", "OLD", 1);
        Assert.Equal(
            "1032547698BADCFE" + "010000000700000000610000060008000600000000000000" +
            "67452301EFCDAB89" + "4E0045005700460049004C0045004F004C004400",
            Convert.ToHexString(stack));

        var redirect = NativeApi.PackRegistryRedirection(RegistryJob, new RegistryRedirection("P") { AccessMask = 0x81234567, Trusted = true, ExitNode = true }, "QR");
        Assert.Equal("1032547698BADCFE020004000600000067452381500051005200", Convert.ToHexString(redirect));

        var runtime = NativeApi.PackRuntimeVirtualKeys(RegistryJob, [new RuntimeVirtualKey("A", "BC", 0x91), new RuntimeVirtualKey("D", "", 0xC0123456)]);
        Assert.Equal("1032547698BADCFE02000000" +
            "910000000200040000000000410042004300" +
            "563412C002000000000000004400", Convert.ToHexString(runtime));
        Assert.Equal("1032547698BADCFE00000000", Convert.ToHexString(NativeApi.PackRuntimeVirtualKeys(RegistryJob, [])));
    }

    [Fact]
    public void FixedRegistryRequestsPreserveFullWidthHandlesAndFlags()
    {
        var calls = new RegistryDevice();
        var api = new NativeApi(calls);
        api.ModifyRegistryFlags(RegistryJob, 0x81234567, 0xF2345678);
        var key = api.GetRegistryHandle(RegistryJob, 2);

        Assert.Equal(unchecked((nint)0x89ABCDEF01234567UL), key);
        Assert.Collection(calls.Requests, modify =>
        {
            Assert.Equal(0x220010u, modify.Code);
            Assert.Equal("1032547698BADCFE67452381785634F2", Convert.ToHexString(modify.Input));
            Assert.Equal(0, modify.OutputLength);
        }, get =>
        {
            Assert.Equal(0x22001cu, get.Code);
            Assert.Equal(16, get.Input.Length);
            // The final DWORD is alignment padding in the x64 request.
            Assert.Equal("1032547698BADCFE02000000", Convert.ToHexString(get.Input.AsSpan(0, 12)));
            Assert.Equal(8, get.OutputLength);
        });
    }

    private sealed class RegistryDevice : NativeApi.PlatformProvider
    {
        internal List<(uint Code, byte[] Input, int OutputLength)> Requests { get; } = [];

        internal override SafeFileHandle OpenRegistryDriver() => new SafeFileHandle(7, ownsHandle: false);

        internal override int DeviceControl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
        {
            Requests.Add((code, input.ToArray(), output.Length));
            if (code == 0x22001c)
                Convert.FromHexString("67452301EFCDAB89").CopyTo(output);
            return 0;
        }
    }

    [Fact]
    public void NativeRegistryWriteGuardDistinguishesMissingPayloadFromExplicitRegNone()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NativeApi.RequireRegistryValuePayload(RegistryValue.WithoutPayload("missing")));
        Assert.Contains("missing", error.Message);
        NativeApi.RequireRegistryValuePayload(new RegistryValue("none", 0, []));
        NativeApi.RequireRegistryValuePayload(RegistryValue.Dword("zero", 0));
    }

    [Fact]
    public void PipePacketOffsetsAreIndependentOfUtf16CodePointCount()
    {
        var packet = Pack.PipeSymlink("A\U0001F642", "B");
        Assert.Equal("0C000600120002000100000041003DD842DE4200", Convert.ToHexString(packet));
    }

#if !SILO2025
    [Fact]
    public void ServerVersionPolicyOnlyBypassesTheBuildComparison()
    {
        Assert.True(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(false, true, 0, 0), 20348, 10, 20348, 0));
        Assert.False(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(false, true, 0, 0), 20348, 10, 17763, 0));
        Assert.True(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(true, true, 0, 0), 20348, 10, 17763, 0));
        Assert.False(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(true, true, 0, 0), 20348, 10, 17762, 0));
        Assert.False(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(true, true, 0, 0), 20348, 10, 20349, 0));
        Assert.True(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(false, false, 100, 10), 20348, 10, 9999, 100));
        Assert.False(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(false, false, 100, 10), 20348, 10, 9999, 99));
        Assert.False(NativeApi.ImageAllowed(new NativeApi.ImagePolicy(false, false, 100, 10), 20348, 9, 9999, 100));
        Assert.Equal(20348u, NativeApi.ParseBuildNumber(" +20348.extra"));
        Assert.Equal(uint.MaxValue, NativeApi.ParseBuildNumber("-1"));
        Assert.Throws<SiloException>(() => NativeApi.ParseBuildNumber("2147483648"));
    }

#endif

    [Fact]
    public void WcHResultMappingRetainsUnknownNtStatusAndWin32Identity()
    {
        Assert.Equal(0, SiloException.MapNtStatus(0x103, 997));
        Assert.Equal(unchecked((int)0x8007000e), SiloException.MapNtStatus(unchecked((int)0xc0000017), 8));
        Assert.Equal(unchecked((int)0x80070005), SiloException.MapNtStatus(unchecked((int)0xc0000022), 5));
        Assert.Equal(unchecked((int)0xd0000509), SiloException.MapNtStatus(unchecked((int)0xc0000509), 0x13d));
    }
}
