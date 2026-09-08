using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WprHelper.Contracts;

namespace WprHelper.Infrastructure;

public sealed class WprCommandBuilder : IWprCommandBuilder
{
    public IReadOnlyList<string> BuildStart(CaptureProfile profile)
    {
        var profiles = GetProfiles(profile);
        var args = new List<string>();
        foreach (var selected in profiles)
        {
            args.Add("-start");
            args.Add(selected);
        }
        if (profile.FileMode) args.Add("-filemode");
        args.AddRange(WindowsCommandLineParser.Parse(profile.WprStartArguments));
        return args;
    }

    public string FormatStart(CaptureProfile profile) => string.Join(' ',
        new[] { profile.WprPath }.Concat(BuildStart(profile)).Select(QuoteArgument));

    private static IReadOnlyList<string> GetProfiles(CaptureProfile profile) => profile.WprProfiles.Count > 0
        ? profile.WprProfiles
        : string.IsNullOrWhiteSpace(profile.WprProfile) ? [] : [profile.WprProfile];

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (!argument.Any(character => char.IsWhiteSpace(character) || character == '"')) return argument;
        var result = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public IReadOnlyList<string> BuildStop(string etlPath, bool skipPdbGeneration = true) => skipPdbGeneration
        ? ["-stop", etlPath, "-skipPdbGen"]
        : ["-stop", etlPath];
    public IReadOnlyList<string> BuildCancel() => ["-cancel"];
}

internal static class WprProfileCatalog
{
    private static readonly HashSet<string> HeaderTokens = new(StringComparer.OrdinalIgnoreCase) { "Microsoft", "Copyright", "Usage:" };

    public static IReadOnlySet<string> Parse(string output)
    {
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && char.IsLetter(columns[0][0]) && !HeaderTokens.Contains(columns[0]))
                profiles.Add(columns[0]);
        }
        return profiles;
    }
}

public sealed class WprCapabilityDetector : IWprCapabilityDetector
{
    private static readonly HashSet<string> BuiltInProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "GeneralProfile", "CPU", "DiskIO", "FileIO", "Registry", "Network", "Heap", "Pool",
        "VirtualAllocation", "Audio", "Video", "Power", "EdgeBrowser", "Minifilter", "GPU",
        "Handle", "XAMLActivity", "DesktopComposition", "DotNET"
    };

    public async Task<WprCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Windows Performance Recorder was not found.", executablePath);
        if (!string.Equals(Path.GetFileName(executablePath), "wpr.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected executable must be wpr.exe.");
        var info = FileVersionInfo.GetVersionInfo(executablePath);
        var version = Version.TryParse(info.FileVersion?.Split(' ').FirstOrDefault(), out var parsed) ? parsed : new Version(0, 0);
        var profiles = new HashSet<string>(BuiltInProfiles, StringComparer.OrdinalIgnoreCase);
        var result = await WprProcessRunner.RunAsync(executablePath, ["-profiles"], TimeSpan.FromSeconds(20), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Windows Performance Recorder could not list profiles: {result.Output}");
        profiles.UnionWith(WprProfileCatalog.Parse(result.Output));
        return new WprCapabilities(version, profiles);
    }
}

public static class WprExecutableLocator
{
    public static string FindPreferred()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Windows Kits", "10", "Windows Performance Toolkit", "wpr.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Windows Kits", "10", "Windows Performance Toolkit", "wpr.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wpr.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }
}

public sealed class WprController(IWprCommandBuilder commandBuilder) : IWprController
{
    public Task StartAsync(CaptureProfile profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return RunCheckedAsync(profile.WprPath, commandBuilder.BuildStart(profile), timeout,
            "Windows Performance Recorder could not start the trace.", cancellationToken);
    }

    public Task StopAsync(string executablePath, string etlPath, bool skipPdbGeneration, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(etlPath)!);
        return RunCheckedAsync(executablePath, commandBuilder.BuildStop(etlPath, skipPdbGeneration), timeout,
            "Windows Performance Recorder could not stop and save the trace.", cancellationToken);
    }

    public Task CancelAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return RunCheckedAsync(executablePath, commandBuilder.BuildCancel(), timeout,
            "Windows Performance Recorder could not cancel the trace.", cancellationToken);
    }

    public async Task<WprStatusReport> GetStatusAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(executablePath, ["-status"], timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Launched || result.ExitCode != 0) return new WprStatusReport(false, false, result.Output);
        return new WprStatusReport(true, !result.Output.Contains("not recording", StringComparison.OrdinalIgnoreCase), result.Output);
    }

    private static async Task RunCheckedAsync(string executablePath, IEnumerable<string> arguments, TimeSpan timeout,
        string failureMessage, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(executablePath, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Launched) throw new InvalidOperationException($"Unable to start Windows Performance Recorder: {result.Output}");
        if (result.ExitCode != 0)
        {
            var troubleshooting = result.Output.Contains("0x80010106", StringComparison.OrdinalIgnoreCase)
                ? Environment.NewLine + "The installed WPR failed while finalizing the trace. Try a newer wpr.exe from Windows Performance Toolkit, or repair/update Windows, then select that executable in WPR Helper."
                : string.Empty;
            throw new InvalidOperationException($"{failureMessage} Exit code: {result.ExitCode}.{(result.Output.Length > 0 ? Environment.NewLine + result.Output : string.Empty)}{troubleshooting}");
        }
    }

    private static async Task<(bool Launched, int ExitCode, string Output)> ExecuteAsync(string executablePath,
        IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await WprProcessRunner.RunAsync(executablePath, arguments, timeout, cancellationToken).ConfigureAwait(false);
        return (true, result.ExitCode, result.Output);
    }
}

internal static class WprProcessRunner
{
    public static async Task<WprRunResult> RunAsync(string executablePath, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException("wpr.exe could not be launched.");
        var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The command timed out.");
        }
        var details = string.Join(Environment.NewLine, new[] { await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false) }
            .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return new WprRunResult(process.ExitCode, details);
    }
}

public sealed class TargetProcessLauncher : ITargetProcessLauncher
{
    public Task<TrackedTargetProcess> LaunchAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(profile.TargetPath)
        {
            UseShellExecute = profile.RunTargetElevated,
            Verb = profile.RunTargetElevated ? "runas" : string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? Path.GetDirectoryName(profile.TargetPath)!
                : profile.WorkingDirectory
        };
        foreach (var argument in WindowsCommandLineParser.Parse(profile.TargetArguments)) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start target process.");
        DateTimeOffset? startedAt = null;
        try { startedAt = process.StartTime.ToUniversalTime(); } catch (InvalidOperationException) { }
        return Task.FromResult(new TrackedTargetProcess(process.Id, startedAt));
    }
}

internal static class WindowsCommandLineParser
{
    public static IReadOnlyList<string> Parse(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var commandLine = "WprHelperTarget.exe " + arguments;
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new InvalidOperationException("Target arguments could not be parsed.");
        try
        {
            var result = new List<string>(Math.Max(0, count - 1));
            for (var index = 1; index < count; index++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty);
            return result;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
