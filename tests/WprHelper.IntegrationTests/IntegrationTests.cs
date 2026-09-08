using System.Diagnostics;
using System.Security.Principal;
using WprHelper.Contracts;
using WprHelper.Core;
using WprHelper.Infrastructure;

namespace WprHelper.IntegrationTests;

public sealed class CapabilityIntegrationTests
{
    [Fact]
    public async Task MissingWprFailsWithoutLaunchingAnything()
    {
        var detector = new WprCapabilityDetector();
        await Assert.ThrowsAsync<FileNotFoundException>(() => detector.DetectAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), CancellationToken.None));
    }

    [Fact]
    public void WindowsWprPathIsFullyQualified()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpr.exe");
        Assert.True(Path.IsPathFullyQualified(path));
    }
}

public sealed class RealWprSmokeTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedWorkerProducesEtl(bool launchTarget)
    {
        var executable = Environment.GetEnvironmentVariable("WPRHELPER_WORKER_EXE");
        Skip.If(Environment.GetEnvironmentVariable("WPRHELPER_REAL_SMOKE") != "1" || string.IsNullOrWhiteSpace(executable),
            "Set WPRHELPER_REAL_SMOKE=1 and WPRHELPER_WORKER_EXE to test the published executable.");
        var wprPath = Environment.GetEnvironmentVariable("WPR_EXE") ?? WprExecutableLocator.FindPreferred();
        var root = Path.Combine(Path.GetTempPath(), "WprHelperPublishedSmoke", Guid.NewGuid().ToString("N"));
        var paths = new StoragePathResolver(root);
        var clock = new SystemClock();
        var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, clock),
            new ElevatedWorkerClient(new TargetProcessLauncher(), executable), new WprCapabilityDetector(),
            new FileTransferService(), new DiskSpaceService(), clock);
        var profile = new CaptureProfile
        {
            WprPath = wprPath,
            LaunchTargetApplication = launchTarget,
            TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            TargetArguments = "/d /c exit 0",
            LocalDirectory = Path.Combine(root, "captures"),
            FileNameTemplate = "smoke_{SessionId}",
            Stop = new StopOptions
            {
                StopAfterTargetExit = launchTarget,
                MaximumDuration = TimeSpan.FromSeconds(launchTarget ? 30 : 2)
            }
        };
        var result = await manager.CaptureAsync(profile, null, CancellationToken.None);
        Assert.Equal(CaptureState.Completed, result.Session.State);
        Assert.Equal(launchTarget ? StopReason.TargetExited : StopReason.DurationReached, result.Session.StopReason);
        Assert.Equal(launchTarget, result.Session.TargetPid.HasValue);
        Assert.True(new FileInfo(Assert.Single(result.Files)).Length > 0);
        Directory.Delete(root, recursive: true);
    }

    [SkippableFact]
    public async Task MultipleProfilesProduceEtl_WhenExplicitlyEnabled()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.If(Environment.GetEnvironmentVariable("WPRHELPER_REAL_SMOKE") != "1",
            "Set WPRHELPER_REAL_SMOKE=1 and run elevated to enable this test.");

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.True(principal.IsInRole(WindowsBuiltInRole.Administrator), "The WPR smoke test must run elevated.");

        var wprPath = Environment.GetEnvironmentVariable("WPR_EXE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpr.exe");
        Assert.True(File.Exists(wprPath));

        var root = Path.Combine(Path.GetTempPath(), "WprHelperSmoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var etl = Path.Combine(root, "cpu-smoke.etl");
        var profile = new CaptureProfile { WprPath = wprPath, WprProfile = "CPU", WprProfiles = ["CPU", "DiskIO"], FileMode = true };
        var controller = new WprController(new WprCommandBuilder());

        try
        {
            await controller.StartAsync(profile, TimeSpan.FromSeconds(30), CancellationToken.None);
            await Task.Delay(1500);
            await controller.StopAsync(wprPath, etl, true, TimeSpan.FromMinutes(2), CancellationToken.None);
        }
        catch
        {
            try { await controller.CancelAsync(wprPath, TimeSpan.FromSeconds(30), CancellationToken.None); } catch { }
            throw;
        }

        Assert.True(File.Exists(etl));
        Assert.True(new FileInfo(etl).Length > 0);
        Directory.Delete(root, recursive: true);
    }
}
