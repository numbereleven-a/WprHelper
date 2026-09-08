using System.Diagnostics;
using WprHelper.App;
using WprHelper.Contracts;
using WprHelper.Infrastructure;

namespace WprHelper.Infrastructure.Tests;

public sealed class ReviewRegressionTests
{
    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task FailedStatusCommandIsNotAnActiveRecording()
    {
        var status = await new WprController(new WprCommandBuilder()).GetStatusAsync(
            PowerShellPath, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(status.QuerySucceeded);
        Assert.False(status.RecordingActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCommandTerminatesItsProcess(bool timeOut)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"WprHelperProcess-{Guid.NewGuid():N}.txt");
        using var cancellation = new CancellationTokenSource();
        var controller = new WprController(new WaitingCommandBuilder(marker));
        var command = controller.StartAsync(new CaptureProfile { WprPath = PowerShellPath },
            TimeSpan.FromSeconds(timeOut ? 3 : 30), cancellation.Token);
        Process? child = null;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
            Assert.True(File.Exists(marker));
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
            if (timeOut) await Assert.ThrowsAsync<TimeoutException>(() => command);
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            }
            Assert.True(child.HasExited, "Cancelling a command left its process running.");
        }
        finally
        {
            cancellation.Cancel();
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
                child.Dispose();
            }
            File.Delete(marker);
        }
    }

    [Fact]
    public void ChangingLanguagePreservesCompletedEtlPathAndWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(), "WprHelperViewModel", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePathResolver(root);
            var vm = new MainWindowViewModel(new CompletedSession(), new JsonProfileRepository(paths), paths,
                new WprCommandBuilder(), new WprHealthChecker());
            vm.StartCommand.Execute(null);
            Assert.False(vm.IsRunning);
            var etl = vm.SavePathText;
            Assert.EndsWith("result.etl", etl, StringComparison.Ordinal);
            Assert.Contains("Optional copy failed", vm.StatusMessage, StringComparison.Ordinal);
            vm.Language = LanguagePreference.English;
            Assert.Equal(etl, vm.SavePathText);
            Assert.Contains("Optional copy failed", vm.StatusMessage, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class WaitingCommandBuilder(string marker) : IWprCommandBuilder
    {
        public IReadOnlyList<string> BuildStart(CaptureProfile profile) =>
            ["-NoProfile", "-NonInteractive", "-Command", $"[IO.File]::WriteAllText('{marker.Replace("'", "''", StringComparison.Ordinal)}', [string]$PID); Start-Sleep -Seconds 30"];
        public string FormatStart(CaptureProfile profile) => string.Empty;
        public IReadOnlyList<string> BuildStop(string etlPath, bool skipPdbGeneration = true) => [];
        public IReadOnlyList<string> BuildCancel() => [];
    }

    private sealed class CompletedSession : ISessionManager
    {
        public Task<int> MarkInterruptedSessionsAsync(CancellationToken token) => Task.FromResult(0);
        public Task<CaptureResult> CaptureAsync(CaptureProfile profile, IProgress<CaptureProgress>? progress,
            CancellationToken captureCancellationToken, CancellationToken postProcessCancellationToken = default) =>
            Task.FromResult(new CaptureResult(new SessionRecord
            {
                State = CaptureState.CompletedWithWarnings,
                Warnings = ["Optional copy failed"]
            }, [Path.Combine(profile.LocalDirectory, "result.etl")]));
    }
}
