using System.Diagnostics;
using System.Security.Principal;
using WprHelper.Contracts;

namespace WprHelper.Infrastructure;

public sealed class WprHealthChecker : IWprHealthChecker
{
    private const int DetailsLimit = 400;

    public async Task<WprHealthReport> CheckAsync(string executablePath, CancellationToken cancellationToken)
    {
        var executableFound = File.Exists(executablePath) &&
            string.Equals(Path.GetFileName(executablePath), "wpr.exe", StringComparison.OrdinalIgnoreCase);
        if (!executableFound)
            return new WprHealthReport(false, null, false, 0, null, false, false, null, null);

        string? fileVersion = null;
        try { fileVersion = FileVersionInfo.GetVersionInfo(executablePath).FileVersion; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var profiles = await RunAsync(executablePath, ["-profiles"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var profileCount = profiles.ExitCode == 0 ? WprProfileCatalog.Parse(profiles.Output).Count : 0;
        var profilesError = profiles.ExitCode == 0 ? null : Summarize(profiles.Output);

        if (!IsElevated())
            return new WprHealthReport(true, fileVersion, profiles.ExitCode == 0, profileCount, profilesError,
                SmokeTestAttempted: false, SmokeTestPassed: false, SmokeTestError: null, SmokeTestDuration: null);

        var smokeWatch = Stopwatch.StartNew();
        var instanceName = $"WprHelperCheck-{Guid.NewGuid():N}";
        WprRunResult start;
        var cancelled = false;
        string? cancelError = null;
        try
        {
            start = await RunAsync(executablePath,
                ["-start", "CPU", "-filemode", "-instancename", instanceName],
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (start.ExitCode == 0) await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The named test recording may have started even if the command was interrupted.
            for (var attempt = 0; attempt < 3 && !cancelled; attempt++)
            {
                var cancel = await RunAsync(executablePath, ["-cancel", "-instancename", instanceName],
                    TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                cancelled = cancel.ExitCode == 0;
                cancelError = cancelled ? null : Summarize(cancel.Output);
                if (!cancelled && attempt < 2) await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
        if (start.ExitCode != 0)
        {
            smokeWatch.Stop();
            return new WprHealthReport(true, fileVersion, profiles.ExitCode == 0, profileCount, profilesError,
                true, false, Summarize(start.Output), smokeWatch.Elapsed);
        }

        smokeWatch.Stop();
        return new WprHealthReport(true, fileVersion, profiles.ExitCode == 0, profileCount, profilesError,
            true, cancelled, cancelError, smokeWatch.Elapsed);
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Summarize(string details)
    {
        var trimmed = details.Trim();
        return trimmed.Length <= DetailsLimit ? trimmed : trimmed[..DetailsLimit] + "…";
    }

    private static async Task<WprRunResult> RunAsync(string executablePath, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return await WprProcessRunner.RunAsync(executablePath, arguments, timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new WprRunResult(-1, $"wpr {string.Join(' ', arguments)} timed out after {timeout.TotalSeconds:0} seconds.");
        }
    }
}

internal sealed record WprRunResult(int ExitCode, string Output);
