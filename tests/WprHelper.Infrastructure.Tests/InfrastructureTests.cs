using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using WprHelper.Contracts;
using WprHelper.Core;
using WprHelper.Infrastructure;

namespace WprHelper.Infrastructure.Tests;

public sealed class WprCommandBuilderTests
{
    private readonly WprCommandBuilder _builder = new();

    [Fact]
    public void StartBuildsCpuFileModeCommand()
    {
        var args = _builder.BuildStart(new CaptureProfile { WprProfile = "CPU", FileMode = true });
        Assert.Equal(new[] { "-start", "CPU", "-filemode" }, args);
    }

    [Fact]
    public void StartBuildsOneStartArgumentPerSelectedProfile()
    {
        var args = _builder.BuildStart(new CaptureProfile { WprProfiles = ["CPU", "DiskIO", "FileIO"], FileMode = true });
        Assert.Equal(new[] { "-start", "CPU", "-start", "DiskIO", "-start", "FileIO", "-filemode" }, args);
    }

    [Fact]
    public void FormattedStartMatchesExecutableAndGeneratedArguments()
    {
        var command = _builder.FormatStart(new CaptureProfile
        {
            WprPath = @"C:\Program Files\Windows Performance Toolkit\wpr.exe",
            WprProfiles = ["CPU", "DiskIO"],
            FileMode = true,
            WprStartArguments = "-instancename TestCapture"
        });
        Assert.Equal("\"C:\\Program Files\\Windows Performance Toolkit\\wpr.exe\" -start CPU -start DiskIO -filemode -instancename TestCapture", command);
    }

    [Fact]
    public void StartPreservesQuotedAdditionalArgumentsAsTokens()
    {
        var args = _builder.BuildStart(new CaptureProfile
        {
            WprProfile = @"C:\profiles\custom profile.wprp!Scenario",
            WprStartArguments = "-onoffscenario \"My Scenario\""
        });
        Assert.Equal(new[] { "-start", @"C:\profiles\custom profile.wprp!Scenario", "-filemode", "-onoffscenario", "My Scenario" }, args);
    }

    [Fact]
    public void StopAndCancelUseWprSyntax()
    {
        Assert.Equal(new[] { "-stop", @"C:\logs\trace.etl" }, _builder.BuildStop(@"C:\logs\trace.etl"));
        Assert.Equal(new[] { "-cancel" }, _builder.BuildCancel());
    }
}

public sealed class ProfileValidationTests
{
    [Fact]
    public void MissingStopSettingsAreReportedInsteadOfThrowing()
    {
        var issues = new ProfileValidator().Validate(new CaptureProfile { Stop = null! });
        Assert.Contains(issues, issue => issue.Field == nameof(CaptureProfile.Stop));
    }

    [Theory]
    [InlineData("-stop trace.etl")]
    [InlineData("-cancel")]
    [InlineData("-start DiskIO")]
    public void ConflictingWprCommandsAreRejected(string arguments)
    {
        var issues = new ProfileValidator().Validate(new CaptureProfile { WprStartArguments = arguments });
        Assert.Contains(issues, issue => issue.Field == nameof(CaptureProfile.WprStartArguments));
    }

    [Fact]
    public void MissingCustomWprProfileIsRejected()
    {
        var issues = new ProfileValidator().Validate(new CaptureProfile { WprProfile = @"C:\missing\trace.wprp!Scenario" });
        Assert.Contains(issues, issue => issue.Field == nameof(CaptureProfile.WprProfiles));
    }

    [Fact]
    public void UncCaptureAndDestinationDirectoriesAreAccepted()
    {
        var issues = new ProfileValidator().Validate(new CaptureProfile
        {
            LocalDirectory = @"\\server\share\captures",
            DestinationDirectory = "//server/share/copy"
        });
        Assert.DoesNotContain(issues, issue => issue.Field == nameof(CaptureProfile.LocalDirectory));
        Assert.DoesNotContain(issues, issue => issue.Field == nameof(CaptureProfile.DestinationDirectory));
    }
}

public sealed class LocalizationResourceTests
{
    [Fact]
    public void EnglishAndRussianResourceKeysMatch()
    {
        var root = FindRepositoryRoot();
        var english = LoadKeys(Path.Combine(root, "src", "WprHelper.App", "Resources", "Strings.en-US.xaml"));
        var russian = LoadKeys(Path.Combine(root, "src", "WprHelper.App", "Resources", "Strings.ru-RU.xaml"));
        Assert.Equal(english, russian);
        Assert.Contains("TargetNotSelected", english);
        Assert.Contains("FinalizationStillRunning", english);
    }

    private static string[] LoadKeys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Descendants().Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => key is not null).Cast<string>().Order(StringComparer.Ordinal).ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WprHelper.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WprHelperTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CopyIsAtomicAndComplete()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.bin");
        var target = Path.Combine(_root, "out", "target.bin");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 10000).Select(x => (byte)x).ToArray());
        var result = await new FileTransferService().CopyAtomicAsync(source, target, false, null, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(result));
        Assert.False(File.Exists(target + ".partial"));
    }

    [SkippableFact]
    public void DiskSpaceCheckSupportsDirectoryPathsThroughWindowsApi()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Directory.CreateDirectory(_root);
        Assert.True(new DiskSpaceService().GetFreeBytes(_root) > 0);
    }

    [Fact]
    public async Task ProfileRoundTripsWprSettings()
    {
        var paths = new StoragePathResolver(_root);
        var repository = new JsonProfileRepository(paths);
        var profile = new CaptureProfile { Name = "Sample", WprProfile = "CPU", WprProfiles = ["CPU", "DiskIO"], FileMode = true, WprStartArguments = "-strict" };
        await repository.SaveAsync(profile, CancellationToken.None);
        var loaded = Assert.Single(await repository.LoadAllAsync(CancellationToken.None));
        Assert.Equal("CPU", loaded.WprProfile);
        Assert.Equal(["CPU", "DiskIO"], loaded.WprProfiles);
        Assert.True(loaded.FileMode);
        Assert.Equal("-strict", loaded.WprStartArguments);
    }

    [Fact]
    public async Task VersionOneProfileMigratesToCurrentSchema()
    {
        var paths = new StoragePathResolver(_root);
        var path = Path.Combine(_root, "old-profile.json");
        await File.WriteAllTextAsync(path, """{"SchemaVersion":1,"Name":"Old","Stop":{}}""");
        var loaded = await new JsonProfileRepository(paths).ImportAsync(path, CancellationToken.None);
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal(["CPU"], loaded.WprProfiles);
    }

    [Fact]
    public async Task ProfileRenameKeepsStableFileAndPreservesSettings()
    {
        var paths = new StoragePathResolver(_root);
        var repository = new JsonProfileRepository(paths);
        var original = new CaptureProfile { Name = "old:name", WprProfile = "GeneralProfile" };
        await repository.SaveAsync(original, CancellationToken.None);
        var before = Directory.GetFiles(paths.ProfilesRoot, "*.json").Single();
        await repository.RenameAsync(original.Name, original with { Name = "old_name" }, CancellationToken.None);
        var after = Directory.GetFiles(paths.ProfilesRoot, "*.json").Single();
        var loaded = Assert.Single(await repository.LoadAllAsync(CancellationToken.None));
        Assert.Equal(before, after);
        Assert.Equal("old_name", loaded.Name);
        Assert.Equal("GeneralProfile", loaded.WprProfile);
    }

    [SkippableFact]
    public void SessionDirectoryAllowsElevatedAdministratorWorker()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var paths = new StoragePathResolver(_root);
        var sessionDirectory = paths.CreateSessionDirectory(Guid.NewGuid());
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = new DirectoryInfo(Path.Combine(sessionDirectory, "wpr")).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.Contains(rules, rule => administratorsSid.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    [Theory]
    [InlineData("//server/share/logs", "\\\\server\\share\\logs")]
    [InlineData("C:\\logs", "C:\\logs")]
    public void DestinationNormalizationIsSafe(string input, string expected) =>
        Assert.Equal(expected, SessionManager.NormalizeDestination(input));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class WorkerProtocolTests
{
    [SkippableFact]
    public async Task StartupFailureIsReturnedInsteadOfBrokenPipe()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var sessionId = Guid.NewGuid();
        var pipeName = $"WprHelper-Test-{sessionId:N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var host = new ElevatedWorkerHost(new FailingWprController(), new FixedDisk(), new StopConditionEvaluator(), new SystemClock(), new ProfileValidator());
        var hostTask = host.RunAsync(pipeName, sessionId, CancellationToken.None);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        var root = Path.Combine(Path.GetTempPath(), "WprHelperProtocol", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Environment.ProcessPath!;
        var wprPath = Path.Combine(root, "wpr.exe");
        var targetPath = Path.Combine(root, "target.exe");
        File.Copy(source, wprPath);
        File.Copy(source, targetPath);
        var profile = new CaptureProfile { WprPath = wprPath, WprProfile = "CPU", TargetPath = targetPath, WorkingDirectory = root, LocalDirectory = root };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new StartCaptureCommand(sessionId, profile, Path.Combine(root, "capture.etl"))));
        var response = JsonSerializer.Deserialize<WorkerEvent>((await reader.ReadLineAsync())!);
        Assert.NotNull(response);
        Assert.Equal("error", response!.Kind);
        Assert.Contains("root cause", response.Message);
        Assert.Equal(1, await hostTask);
        Directory.Delete(root, true);
    }

    private sealed class FailingWprController : IWprController
    {
        public Task StartAsync(CaptureProfile profile, TimeSpan timeout, CancellationToken token) =>
            throw new InvalidOperationException("expected root cause");
        public Task StopAsync(string path, string etl, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
        public Task CancelAsync(string path, TimeSpan timeout, CancellationToken token) => Task.CompletedTask;
    }
}

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WprHelperSessionTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedWorkerProducesFinalEtlWithoutRegressingState()
    {
        Directory.CreateDirectory(_root);
        var source = Environment.ProcessPath!;
        var wpr = Path.Combine(_root, "wpr.exe");
        var target = Path.Combine(_root, "target.exe");
        File.Copy(source, wpr);
        File.Copy(source, target);
        var paths = new StoragePathResolver(Path.Combine(_root, "app"));
        var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, new SystemClock()),
            new CompletingWorker(), new FixedCapabilities(), new FileTransferService(), new FixedDisk(), new SystemClock());
        var profile = new CaptureProfile
        {
            WprPath = wpr,
            WprProfile = "CPU",
            TargetPath = target,
            WorkingDirectory = _root,
            LocalDirectory = Path.Combine(_root, "captures"),
            Stop = new StopOptions { StopAfterTargetExit = false, MaximumDuration = TimeSpan.FromSeconds(1), MinimumFreeBytes = 64 * 1024 * 1024 }
        };

        var result = await manager.CaptureAsync(profile, null, CancellationToken.None);
        Assert.Equal(CaptureState.Completed, result.Session.State);
        Assert.Single(result.Files.Where(x => Path.GetExtension(x).Equals(".etl", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ManualCancellationStillFinalizesEtl()
    {
        Directory.CreateDirectory(_root);
        var source = Environment.ProcessPath!;
        var wpr = Path.Combine(_root, "wpr.exe");
        var target = Path.Combine(_root, "target.exe");
        File.Copy(source, wpr);
        File.Copy(source, target);
        var paths = new StoragePathResolver(Path.Combine(_root, "app"));
        var worker = new ManualStopWorker();
        var manager = new SessionManager(new ProfileValidator(), paths, new JsonSessionRepository(paths, new SystemClock()),
            worker, new FixedCapabilities(), new FileTransferService(), new FixedDisk(), new SystemClock());
        var profile = new CaptureProfile
        {
            WprPath = wpr,
            WprProfile = "CPU",
            TargetPath = target,
            WorkingDirectory = _root,
            LocalDirectory = Path.Combine(_root, "captures"),
            Stop = new StopOptions { StopAfterTargetExit = false, MaximumDuration = TimeSpan.FromMinutes(1), MinimumFreeBytes = 64 * 1024 * 1024 }
        };
        using var cancellation = new CancellationTokenSource();
        var capture = manager.CaptureAsync(profile, null, cancellation.Token);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var result = await capture;

        Assert.Equal(CaptureState.Completed, result.Session.State);
        Assert.Equal(StopReason.Manual, result.Session.StopReason);
        Assert.Single(result.Files.Where(path => path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class CompletingWorker : IElevatedWorkerClient
    {
        public Task<ElevatedCaptureResult> CaptureAsync(Guid id, CaptureProfile profile, string backingFile, IProgress<CaptureProgress>? progress, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backingFile)!);
            File.WriteAllBytes(backingFile, [1, 2, 3]);
            progress?.Report(new(CaptureState.StartingWpr, "started", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.WaitingForWpr, "ready", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.LaunchingTarget, "launching", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.Capturing, "capturing", TimeSpan.Zero, 0, long.MaxValue, 123));
            progress?.Report(new(CaptureState.StoppingWpr, "stopping", TimeSpan.FromSeconds(1), 3, long.MaxValue, 123, StopReason.DurationReached));
            progress?.Report(new(CaptureState.Capturing, "late final event", TimeSpan.FromSeconds(1), 3, long.MaxValue, 123, StopReason.DurationReached));
            return Task.FromResult(new ElevatedCaptureResult(123, StopReason.DurationReached, DateTimeOffset.Now));
        }
    }

    private sealed class ManualStopWorker : IElevatedWorkerClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ElevatedCaptureResult> CaptureAsync(Guid id, CaptureProfile profile, string backingFile,
            IProgress<CaptureProgress>? progress, CancellationToken token)
        {
            progress?.Report(new(CaptureState.StartingWpr, "started", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.WaitingForWpr, "ready", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.LaunchingTarget, "launching", TimeSpan.Zero, 0, long.MaxValue, null));
            progress?.Report(new(CaptureState.Capturing, "capturing", TimeSpan.Zero, 0, long.MaxValue, 321));
            Started.SetResult();
            while (!token.IsCancellationRequested) await Task.Delay(10, CancellationToken.None);
            progress?.Report(new(CaptureState.StoppingWpr, "stopping", TimeSpan.Zero, 0, long.MaxValue, 321, StopReason.Manual));
            Directory.CreateDirectory(Path.GetDirectoryName(backingFile)!);
            await File.WriteAllBytesAsync(backingFile, [1, 2, 3], CancellationToken.None);
            return new ElevatedCaptureResult(321, StopReason.Manual, DateTimeOffset.Now);
        }
    }

    private sealed class FixedCapabilities : IWprCapabilityDetector
    {
        public Task<WprCapabilities> DetectAsync(string path, CancellationToken token) =>
            Task.FromResult(new WprCapabilities(new Version(10, 0), new HashSet<string> { "CPU" }));
    }
}

file sealed class FixedDisk : IDiskSpaceService
{
    public long GetFreeBytes(string path) => long.MaxValue;
}
