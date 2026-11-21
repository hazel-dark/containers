using System.Buffers.Binary;
using Containers.Windows.Silos.DescriptionModel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Containers.Windows.Silos.Tests;

public sealed class ServerLifecycle2025Tests
{
    [Theory]
    [InlineData(false, 20348, 11, 21, true)]
    [InlineData(false, 26100, 11, 21, true)]
    [InlineData(false, 26000, 11, 21, false)]
    [InlineData(true, 26000, 11, 21, true)]
    [InlineData(true, 26100, 10, 21, false)]
    [InlineData(true, 20348, 11, 20, false)]
    public void BootPolicyChecksBothRevisionMinima(bool skip, uint build, uint imageUbr, uint hostUbr, bool expected)
    {
        Assert.Equal(expected, NativeApi.ImageAllowed(skip, 11, 21, new NativeApi.ImageVersion(build, imageUbr), new NativeApi.ImageVersion(26100, hostUbr)));
    }

    [Fact]
    public void ServerModeProbeAndTerminationStatusUseThe2025Contracts()
    {
        Assert.Equal(40, NativeJobConstants.ServerSiloBasicInformationSize);
        const int systemProcessTerminated = unchecked((int)0xc000021a);
        Assert.Equal(SiloTerminationReason.Terminated, SiloTerminationNotification.Classify(true, 13, systemProcessTerminated));
        Assert.Equal(SiloTerminationReason.NormalExit, SiloTerminationNotification.Classify(false, 13, systemProcessTerminated));
        Assert.Null(SiloTerminationNotification.Classify(true, 4, systemProcessTerminated));
    }

    [Fact]
    public void OnlyBootValidatesAndCarriesTheImageBuildIntoClass40()
    {
        var api = new BootCalls();
        using var silo = WindowsSilo.Create(new SiloDescription(), server: true, api);
        Assert.DoesNotContain("validate-boot-image", api.Calls);
        using var deleteEvent = new SafeWaitHandle(0x4321, ownsHandle: false);
        api.Calls.Clear();
        api.FailValidation = true;
        Assert.Throws<InvalidDataException>(() => silo.BootServer(deleteEvent));
        Assert.Equal(["id", "validate-boot-image"], api.Calls);

        api.Calls.Clear();
        api.FailValidation = false;
        silo.BootServer(deleteEvent);
        Assert.Equal(["id", "validate-boot-image", "set:40", "id", "boot"], api.Calls);
        var initialize = Assert.Single(api.JobWrites, write => write.Class == 40);
        Assert.Equal("21430000000000007C4F000000000000", Convert.ToHexString(initialize.Bytes));
    }

    [Fact]
    public void FirstBootUsesLifetimeCountAndServiceSessionPreservesUnassignedValue()
    {
        var api = new BootCalls();
        using var silo = WindowsSilo.Create(new SiloDescription(), server: false, api);
        Assert.True(silo.IsFirstBoot);
        api.TotalProcesses = 9;
        Assert.False(silo.IsFirstBoot);
        Assert.Equal(uint.MaxValue, silo.ServiceSessionId);
        api.QueryStatus = unchecked((int)0xc0000022);
        Assert.Equal(api.QueryStatus, Assert.Throws<SiloException>(() => silo.IsFirstBoot).NtStatus);
        Assert.Equal(api.QueryStatus, Assert.Throws<SiloException>(() => silo.ServiceSessionId).NtStatus);
    }

    private sealed class BootCalls : SiloLifecycleTests.RecordingApi
    {
        internal bool FailValidation;
        internal uint TotalProcesses;

        public override uint ValidateBootImage(JobSafeHandle job)
        {
            Calls.Add("validate-boot-image");
            if (FailValidation)
                throw new InvalidDataException();
            return 20348;
        }

        public override int QueryJob(JobSafeHandle job, int informationClass, Span<byte> data)
        {
            Assert.Equal(CreatedJob, job.DangerousGetHandle());
            switch (informationClass)
            {
                case 1:
                    Assert.Equal(48, data.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[36..], TotalProcesses);
                    break;
                case 39:
                    Assert.Equal(0x270, data.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(data, uint.MaxValue);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected job information class {informationClass}.");
            }
            return QueryStatus;
        }
    }
}
