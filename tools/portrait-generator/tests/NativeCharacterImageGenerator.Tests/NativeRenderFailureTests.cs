using Xunit;
using System.Diagnostics;

namespace Bannerlord.NativeCharacterImageGenerator.Tests;

public class NativeRenderFailureTests
{
    [Fact]
    public void ExpiredOwnedPortraitAttemptCannotCreateAJob()
    {
        Assert.Throws<TimeoutException>(() => new OwnedPortraitRenderJob(DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void OwnedPortraitLaunchUsesOneJobAndConfirmsEmptyClosure()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var job = new OwnedPortraitRenderJob(DateTimeOffset.UtcNow.AddSeconds(20));
        var inert = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe"))
        {
            WorkingDirectory = Environment.SystemDirectory
        };
        inert.ArgumentList.Add("/?");

        using var child = job.Start(inert);
        Assert.Throws<InvalidOperationException>(() => job.Start(inert));
        Assert.True(child.WaitForExit(5000));
        Assert.Equal(0, child.ExitCode);
        Assert.True(job.CloseAndConfirm());
        Assert.True(job.CloseAndConfirm());
    }

    [Fact]
    public void NativeCrashIsNotReportedAsSuccessfulProgress()
    {
        var error = NativeEngineRenderService.DescribeRenderFailure("initializing", "Prepared 14 appearances.",
            unchecked((int)0xC0000005), "job/status.json");
        Assert.StartsWith("Native portrait worker exited", error);
        Assert.Contains("0xC0000005", error);
        Assert.Contains("job/status.json", error);
        Assert.Contains("Last progress: Prepared 14", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExitWithoutOutputIsFailureEvenWithZeroExitCode(int code)
    {
        Assert.Contains("exited before producing", NativeEngineRenderService.DescribeRenderFailure(null, null, code, "status.json"));
    }

    [Fact]
    public void ExplicitWorkerFailureIsPreserved()
    {
        Assert.Equal("Invalid body key", NativeEngineRenderService.DescribeRenderFailure("failed", "Invalid body key", 1, "status.json"));
    }

    [Fact]
    public void MissingStatusDoesNotMasqueradeAsProgress()
    {
        Assert.StartsWith("No completed native source", NativeEngineRenderService.DescribeRenderFailure(null, null, null, "status.json"));
    }
}
