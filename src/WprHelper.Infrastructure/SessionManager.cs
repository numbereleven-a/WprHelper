using WprHelper.Contracts;
using WprHelper.Core;

namespace WprHelper.Infrastructure;

public sealed class SessionManager(
    ProfileValidator validator,
    IStoragePathResolver paths,
    ISessionRepository sessions,
    IElevatedWorkerClient worker,
    IWprCapabilityDetector capabilities,
    IFileTransferService transfer,
    IDiskSpaceService disk,
    IClock clock) : ISessionManager
{
    public async Task<CaptureResult> CaptureAsync(CaptureProfile profile, IProgress<CaptureProgress>? progress,
        CancellationToken captureCancellationToken, CancellationToken postProcessCancellationToken = default)
    {
        var machine = new CaptureStateMachine();
        machine.TransitionTo(CaptureState.Validating);
        var issues = validator.Validate(profile);
        var errors = issues.Where(x => !x.IsWarning).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(x => x.Message)));
        var detected = await capabilities.DetectAsync(profile.WprPath, captureCancellationToken);
        if (detected.Version == new Version(0, 0)) throw new InvalidOperationException("Unable to determine the Windows Performance Recorder version.");
        var selectedProfiles = profile.WprProfiles.Count > 0 ? profile.WprProfiles : [profile.WprProfile];
        var unknownProfile = selectedProfiles.FirstOrDefault(selected => !selected.Contains('!') && !detected.BuiltInProfiles.Contains(selected));
        if (unknownProfile is not null)
            throw new InvalidOperationException($"Unknown built-in WPR profile: {unknownProfile}.");
        var sessionId = Guid.NewGuid();
        var sessionDirectory = paths.CreateSessionDirectory(sessionId);
        var captureDirectory = Path.GetFullPath(NormalizeDestination(profile.LocalDirectory));
        Directory.CreateDirectory(captureDirectory);
        var captureTimestamp = clock.Now;
        var appName = profile.LaunchTargetApplication ? Path.GetFileNameWithoutExtension(profile.TargetPath) : "Capture";
        var initialContext = new FileNameContext(appName, profile.Name, sessionId, null, captureTimestamp);
        var baseName = FileNameTemplate.Expand(profile.FileNameTemplate, initialContext);
        // Write the WPR result directly to the selected local directory. Keeping the
        // final path from the start avoids a second read/copy through the session
        // directory, which can expose a short-lived file-system visibility gap on
        // virtual machines and redirected storage.
        var backingFile = FileNameTemplate.GetUniquePath(captureDirectory, baseName, ".etl");
        if (disk.GetFreeBytes(captureDirectory) <= profile.Stop.MinimumFreeBytes || disk.GetFreeBytes(sessionDirectory) <= profile.Stop.MinimumFreeBytes)
            throw new IOException("Available disk space is below the configured reserve.");
        machine.TransitionTo(CaptureState.Preparing);
        var record = new SessionRecord
        {
            SessionId = sessionId,
            State = CaptureState.Preparing,
            CreatedAt = clock.Now,
            UpdatedAt = clock.Now,
            SessionDirectory = sessionDirectory,
            BackingFile = backingFile,
            DestinationDirectory = profile.DestinationDirectory,
            Warnings = issues.Where(x => x.IsWarning).Select(x => x.Message).ToArray()
        };
        var logPath = Path.Combine(sessionDirectory, "logs", "capture.log");
        var targetDescription = profile.LaunchTargetApplication ? profile.TargetPath : "none";
        await AppendLogAsync(logPath, $"Session created. Wpr={profile.WprPath}; Target={targetDescription}; Backing={backingFile}; Output={captureDirectory}");
        await sessions.SaveAsync(record, captureCancellationToken);
        try
        {
            machine.TransitionTo(CaptureState.WaitingForElevation);
            record = record with { State = CaptureState.WaitingForElevation };
            await sessions.SaveAsync(record, captureCancellationToken);
            var progressPersistence = Task.CompletedTask;
            var progressGate = new object();
            var managedProgress = new SynchronousProgress<CaptureProgress>(update =>
            {
                progress?.Report(update);
                if (update.State == machine.State) return;
                if (update.State == CaptureState.Capturing && machine.State is CaptureState.StopRequested or CaptureState.StoppingWpr)
                    return;
                lock (progressGate)
                {
                    if (update.State == CaptureState.StoppingWpr && machine.State == CaptureState.Capturing)
                        machine.TransitionTo(CaptureState.StopRequested);
                    if (!machine.CanTransitionTo(update.State))
                    {
                        var ignoredLog = $"Ignored out-of-order progress state {update.State} while in {machine.State}.";
                        progressPersistence = PersistLogAfterAsync(progressPersistence, logPath, ignoredLog);
                        return;
                    }
                    machine.TransitionTo(update.State);
                    record = record with
                    {
                        State = update.State,
                        TargetPid = update.TargetPid ?? record.TargetPid,
                        StopReason = update.StopReason,
                        CaptureStartedAt = update.State == CaptureState.Capturing && record.CaptureStartedAt is null ? clock.Now : record.CaptureStartedAt
                    };
                    var snapshot = record;
                    var logMessage = $"State={update.State}; Elapsed={update.Elapsed}; EtlBytes={update.EtlBytes}; StopReason={update.StopReason}";
                    progressPersistence = PersistProgressAfterAsync(progressPersistence, sessions, snapshot, logPath, logMessage);
                }
            });
            var capture = await worker.CaptureAsync(sessionId, profile, backingFile, managedProgress, captureCancellationToken);
            Task pendingProgressPersistence;
            lock (progressGate) pendingProgressPersistence = progressPersistence;
            await pendingProgressPersistence;
            machine.TransitionTo(CaptureState.Finalizing);
            record = record with { State = CaptureState.Finalizing, TargetPid = capture.TargetPid, StopReason = capture.Reason, CaptureStartedAt = capture.CaptureStartedAt };
            await sessions.SaveAsync(record, CancellationToken.None);

            var files = new List<string> { await WaitForTraceAsync(backingFile) };
            var warnings = record.Warnings.ToList();
            if (!string.IsNullOrWhiteSpace(profile.DestinationDirectory))
            {
                machine.TransitionTo(CaptureState.Copying);
                try
                {
                    var destination = NormalizeDestination(profile.DestinationDirectory);
                    Directory.CreateDirectory(destination);
                    var copied = new List<string>();
                    for (var index = 0; index < files.Count; index++)
                    {
                        var source = files[index];
                        var suffix = files.Count > 1 ? $"_{index + 1:000}" : string.Empty;
                        var target = profile.OverwriteExisting
                            ? Path.Combine(destination, baseName + suffix + Path.GetExtension(source))
                            : FileNameTemplate.GetUniquePath(destination, baseName + suffix, Path.GetExtension(source));
                        var transferProgress = new Progress<FileTransferProgress>(x => progress?.Report(new(CaptureState.Copying, "Copying files", TimeSpan.Zero, 0, 0, capture.TargetPid, capture.Reason, x.Percent)));
                        copied.Add(await transfer.CopyAtomicAsync(source, target, profile.OverwriteExisting, transferProgress, postProcessCancellationToken));
                    }
                    files.AddRange(copied);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    warnings.Add(ex is OperationCanceledException ? "Optional destination copy was cancelled." : $"Optional destination copy failed: {ex.Message}");
                }
            }
            machine.TransitionTo(warnings.Count > 0 ? CaptureState.CompletedWithWarnings : CaptureState.Completed);
            record = record with { State = machine.State, CompletedAt = clock.Now, UpdatedAt = clock.Now, Warnings = warnings };
            await sessions.SaveAsync(record, CancellationToken.None);
            await AppendLogAsync(logPath, $"Completed. State={record.State}; StopReason={record.StopReason}; Files={string.Join(" | ", files)}");
            return new CaptureResult(record, files);
        }
        catch (Exception ex)
        {
            record = record with { State = CaptureState.Failed, Error = ex.Message, CompletedAt = clock.Now };
            await sessions.SaveAsync(record, CancellationToken.None);
            await AppendLogAsync(logPath, $"Failed: {ex}");
            throw;
        }
    }

    public static string NormalizeDestination(string path) => path.StartsWith("//", StringComparison.Ordinal) ? "\\\\" + path[2..].Replace('/', '\\') : path;

    private static async Task<string> WaitForTraceAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var length = new FileInfo(path).Length;
                    if (length > 0) return path;
                    lastError = new IOException("The ETL file exists but is still empty.");
                }
                else
                {
                    lastError = new FileNotFoundException("The ETL file has not appeared yet.", path);
                }
            }
            catch (IOException ex) { lastError = ex; }
            catch (UnauthorizedAccessException ex) { lastError = ex; }

            await Task.Delay(250);
        }

        throw new IOException("Windows Performance Recorder did not produce a readable non-empty ETL file.", lastError);
    }

    private static Task AppendLogAsync(string path, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static async Task PersistProgressAfterAsync(Task previous, ISessionRepository sessions, SessionRecord snapshot, string logPath, string logMessage)
    {
        await previous.ConfigureAwait(false);
        await sessions.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        await AppendLogAsync(logPath, logMessage).ConfigureAwait(false);
    }

    private static async Task PersistLogAfterAsync(Task previous, string logPath, string message)
    {
        await previous.ConfigureAwait(false);
        await AppendLogAsync(logPath, message).ConfigureAwait(false);
    }

}

internal sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
