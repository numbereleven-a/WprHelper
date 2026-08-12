using WprHelper.Contracts;

namespace WprHelper.Core;

public sealed class ProfileValidator
{
    public IReadOnlyList<ValidationIssue> Validate(CaptureProfile profile)
    {
        var issues = new List<ValidationIssue>();
        var stop = profile.Stop;
        if (stop is null)
        {
            issues.Add(new(nameof(profile.Stop), "Stop settings are missing from the profile."));
            return issues;
        }
        if (!File.Exists(profile.WprPath))
            issues.Add(new(nameof(profile.WprPath), "Windows Performance Recorder executable does not exist."));
        else if (!string.Equals(Path.GetFileName(profile.WprPath), "wpr.exe", StringComparison.OrdinalIgnoreCase))
            issues.Add(new(nameof(profile.WprPath), "Select the executable named wpr.exe."));
        var wprProfiles = profile.WprProfiles.Count > 0
            ? profile.WprProfiles
            : string.IsNullOrWhiteSpace(profile.WprProfile) ? [] : [profile.WprProfile];
        if (wprProfiles.Count == 0)
            issues.Add(new(nameof(profile.WprProfile), "Select a built-in WPR profile or enter a .wprp profile path."));
        if (wprProfiles.Count > 64)
            issues.Add(new(nameof(profile.WprProfiles), "WPR supports no more than 64 profiles in one recording."));
        foreach (var wprProfile in wprProfiles)
        {
            if (string.IsNullOrWhiteSpace(wprProfile))
                issues.Add(new(nameof(profile.WprProfiles), "WPR profile names cannot be empty."));
            else if (wprProfile.LastIndexOf('!') is var separator && separator >= 0)
            {
                var profilePath = wprProfile[..separator].Trim('"');
                var profileName = wprProfile[(separator + 1)..];
                if (!string.Equals(Path.GetExtension(profilePath), ".wprp", StringComparison.OrdinalIgnoreCase) || !File.Exists(profilePath))
                    issues.Add(new(nameof(profile.WprProfiles), "The WPR profile file does not exist or is not a .wprp file."));
                if (string.IsNullOrWhiteSpace(profileName))
                    issues.Add(new(nameof(profile.WprProfiles), "The profile name after '!' cannot be empty."));
            }
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(profile.WprStartArguments ?? string.Empty,
                @"(^|\s)-(start|stop|cancel)(\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            issues.Add(new(nameof(profile.WprStartArguments), "Additional arguments cannot contain -start, -stop, or -cancel."));

        if (!File.Exists(profile.TargetPath))
            issues.Add(new(nameof(profile.TargetPath), "Target executable does not exist."));
        else if (!string.Equals(Path.GetExtension(profile.TargetPath), ".exe", StringComparison.OrdinalIgnoreCase) || !IsPortableExecutable(profile.TargetPath))
            issues.Add(new(nameof(profile.TargetPath), "Target must be a Windows PE executable (.exe), not a script, shortcut, or document."));
        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory) && !Directory.Exists(profile.WorkingDirectory))
            issues.Add(new(nameof(profile.WorkingDirectory), "Working directory does not exist."));
        var localDirectory = NormalizeNetworkPath(profile.LocalDirectory);
        var destinationDirectory = NormalizeNetworkPath(profile.DestinationDirectory);
        if (!string.IsNullOrWhiteSpace(localDirectory) && !Path.IsPathFullyQualified(localDirectory))
            issues.Add(new(nameof(profile.LocalDirectory), "The local capture directory must be an absolute path."));
        if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Path.IsPathFullyQualified(destinationDirectory))
            issues.Add(new(nameof(profile.DestinationDirectory), "The additional copy directory must be an absolute local or UNC path."));
        if (stop.MaximumDuration is { } duration && duration <= TimeSpan.Zero)
            issues.Add(new("Stop.MaximumDuration", "Maximum duration must be greater than zero."));
        if (stop.TargetExitDelay < TimeSpan.Zero)
            issues.Add(new("Stop.TargetExitDelay", "Target-exit delay cannot be negative."));
        if (stop.MinimumFreeBytes < 64L * 1024 * 1024)
            issues.Add(new("Stop.MinimumFreeBytes", "Free-space reserve must be at least 64 MB."));
        if (!stop.StopAfterTargetExit && stop.MaximumDuration is null)
            issues.Add(new("Stop", "Only manual stop is enabled; the session has no automatic safety limit.", true));
        if (string.IsNullOrWhiteSpace(profile.LocalDirectory))
            issues.Add(new(nameof(profile.LocalDirectory), "Select a local capture directory."));
        if (string.IsNullOrWhiteSpace(profile.FileNameTemplate))
            issues.Add(new(nameof(profile.FileNameTemplate), "File name template cannot be empty."));

        return issues;
    }

    public static string NormalizeProcessName(string name)
    {
        var value = Path.GetFileName(name.Trim());
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
    }

    private static string NormalizeNetworkPath(string path) => path.StartsWith("//", StringComparison.Ordinal)
        ? "\\\\" + path[2..].Replace('/', '\\')
        : path;

    public static bool IsValidProcessName(string name)
    {
        var original = name.Trim();
        if (!string.Equals(original, Path.GetFileName(original), StringComparison.Ordinal)) return false;
        var normalized = NormalizeProcessName(name);
        return normalized.Length > 4 && normalized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(normalized, Path.GetFileName(normalized), StringComparison.Ordinal);
    }

    public static bool IsPortableExecutable(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset < 64 || peOffset > stream.Length - 4) return false;
            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550;
        }
        catch { return false; }
    }
}
