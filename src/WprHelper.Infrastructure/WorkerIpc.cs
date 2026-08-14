using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WprHelper.Contracts;
using WprHelper.Core;

namespace WprHelper.Infrastructure;

internal static class PipeJson
{
    private const int MaximumMessageCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static async Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken token)
    {
        var json = value is WorkerCommand command
            ? JsonSerializer.Serialize<WorkerCommand>(command, Options)
            : JsonSerializer.Serialize(value, Options);
        await writer.WriteLineAsync(json.AsMemory(), token);
        await writer.FlushAsync(token);
    }
    public static async Task<T?> ReadAsync<T>(StreamReader reader, CancellationToken token)
    {
        var line = await ReadBoundedLineAsync(reader, token);
        return line is null ? default : JsonSerializer.Deserialize<T>(line, Options);
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), token);
            if (read == 0) return result.Length == 0 ? null : result.ToString();
            if (character[0] == '\n') return result.ToString();
            if (character[0] != '\r') result.Append(character[0]);
            if (result.Length > MaximumMessageCharacters)
                throw new InvalidDataException("Worker IPC message exceeds the 1 MiB limit.");
        }
    }
}

internal sealed class PipeStreamWriter(Stream stream) : StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
{
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        catch (IOException) when (disposing) { }
    }
}

public sealed class ElevatedWorkerHost(IWprController wpr, IDiskSpaceService disk, StopConditionEvaluator evaluator, IClock clock,
    ProfileValidator validator)
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(15);

    public async Task<int> RunAsync(string pipeName, Guid expectedSessionId, CancellationToken cancellationToken)
    {
        // The server ACL already limits access to this Windows user. CurrentUserOnly on the
        // client additionally validates the server owner and rejects a legitimate UAC boundary.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(20_000, cancellationToken);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new PipeStreamWriter(pipe) { AutoFlush = true };
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task SendEventAsync(WorkerEvent evt, CancellationToken token)
        {
            await writeGate.WaitAsync(token);
            try { await PipeJson.WriteAsync(writer, evt, token); }
            finally { writeGate.Release(); }
        }
        StartCaptureCommand? start = null;
        var wprStarted = false;
        var reason = StopReason.None;
        string? error = null;
        try
        {
            start = await PipeJson.ReadAsync<StartCaptureCommand>(reader, cancellationToken)
                ?? throw new InvalidOperationException("Worker did not receive a start command.");
            if (start.SessionId != expectedSessionId) throw new InvalidOperationException("Worker session identifier mismatch.");
            var validationErrors = validator.Validate(start.Profile).Where(x => !x.IsWarning).ToArray();
            if (validationErrors.Length > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors.Select(x => x.Message)));
            ValidateBackingPath(start.BackingFile);
            DateTimeOffset? startedAt = null;
            DateTimeOffset? targetExitedAt = null;
            int? targetPid = null;
            DateTimeOffset? targetStartedAt = null;
            var lastClientContact = Stopwatch.GetTimestamp();
            Task? progressWrite = null;
            await wpr.StartAsync(start.Profile, TimeSpan.FromSeconds(30), cancellationToken);
            wprStarted = true;
            lastClientContact = Stopwatch.GetTimestamp();
            startedAt = clock.Now;
            await SendEventAsync(new WorkerEvent("started", start.SessionId, "Windows Performance Recorder started."), cancellationToken);
            await SendEventAsync(new WorkerEvent("ready", start.SessionId, "Windows Performance Recorder is recording."), cancellationToken);
            var readTask = PipeJson.ReadAsync<WorkerCommand>(reader, cancellationToken);
            while (reason == StopReason.None)
            {
                var delay = Task.Delay(500, cancellationToken);
                var completed = await Task.WhenAny(readTask, delay);
                if (completed == readTask)
                {
                    var command = await readTask;
                    if (command is null) { reason = StopReason.ConnectionLost; break; }
                    if (command.SessionId != start.SessionId) throw new InvalidOperationException("IPC session identifier mismatch.");
                    lastClientContact = Stopwatch.GetTimestamp();
                    switch (command)
                    {
                        case SetTargetPidCommand target:
                            targetPid = target.TargetPid;
                            targetStartedAt = target.TargetStartedAt;
                            break;
                        case StopCaptureCommand stop: reason = stop.Reason; break;
                        case HeartbeatCommand: break;
                    }
                    readTask = PipeJson.ReadAsync<WorkerCommand>(reader, cancellationToken);
                }

                if (Stopwatch.GetElapsedTime(lastClientContact) > ClientTimeout)
                {
                    reason = StopReason.ConnectionLost;
                    break;
                }
                long freeBytes;
                try
                {
                    var sessionFreeBytes = disk.GetFreeBytes(Path.GetDirectoryName(start.BackingFile)!);
                    var outputFreeBytes = disk.GetFreeBytes(start.Profile.LocalDirectory);
                    freeBytes = Math.Min(sessionFreeBytes, outputFreeBytes);
                }
                catch (IOException ex)
                {
                    reason = StopReason.Error;
                    error = $"Capture storage could not be checked: {ex.Message}";
                    break;
                }
                var targetExited = targetPid is { } pid && !IsRunning(pid, targetStartedAt);
                if (targetExited && targetExitedAt is null) targetExitedAt = clock.Now;
                var elapsed = startedAt is null ? TimeSpan.Zero : clock.Now - startedAt.Value;
                reason = reason == StopReason.None
                    ? evaluator.Evaluate(start.Profile.Stop, elapsed, 0, freeBytes, targetExited, targetExitedAt is null ? null : clock.Now - targetExitedAt)
                    : reason;
                if (progressWrite is null || progressWrite.IsCompleted)
                {
                    var progressMessage = start.Profile.LaunchTargetApplication && targetPid is null
                        ? "Waiting for target application."
                        : "Recording performance trace";
                    progressWrite = SendEventAsync(new WorkerEvent("progress", start.SessionId, progressMessage, reason, 0, freeBytes), cancellationToken);
                }
            }

            if (progressWrite is not null)
            {
                try { await progressWrite; }
                catch (IOException) { reason = StopReason.ConnectionLost; }
            }
        }
        catch (IOException) { reason = StopReason.ConnectionLost; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { reason = StopReason.ConnectionLost; }
        catch (Exception ex)
        {
            reason = StopReason.Error;
            error = ex.Message;
        }
        finally
        {
            if (wprStarted && start is not null)
            {
                try { await SendEventAsync(new WorkerEvent("stopping", expectedSessionId, "Stopping Windows Performance Recorder and saving ETL.", reason), CancellationToken.None); }
                catch (IOException) { }
                try { await wpr.StopAsync(start.Profile.WprPath, start.BackingFile, start.Profile.SkipPdbGeneration, Timeout.InfiniteTimeSpan, CancellationToken.None); }
                catch (Exception stopException)
                {
                    reason = StopReason.Error;
                    error = error is null
                        ? $"Windows Performance Recorder could not save the trace: {stopException.Message}"
                        : $"{error} Windows Performance Recorder could not save the trace: {stopException.Message}";
                    try
                    {
                        await wpr.CancelAsync(start.Profile.WprPath, TimeSpan.FromSeconds(30), CancellationToken.None);
                    }
                    catch (Exception cancelException)
                    {
                        error = $"{error} Emergency cleanup also failed: {cancelException.Message}";
                    }
                }
            }
        }

        var kind = error is null ? "stopped" : "error";
        var message = error ?? "Windows Performance Recorder stopped and saved the ETL file.";
        try { await SendEventAsync(new WorkerEvent(kind, expectedSessionId, message, reason), CancellationToken.None); }
        catch (IOException) { }
        return error is null ? 0 : 1;
    }

    private static bool IsRunning(int pid, DateTimeOffset? expectedStartedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (expectedStartedAt is null) return true;
            var actualStartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return Math.Abs((actualStartedAt - expectedStartedAt.Value.ToUniversalTime()).TotalSeconds) < 1;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static void ValidateBackingPath(string backingFile)
    {
        var backing = Path.GetFullPath(backingFile);
        var directory = Path.GetDirectoryName(backing) ?? throw new InvalidOperationException("Backing ETL directory is missing.");
        if (!Path.IsPathFullyQualified(backingFile) || !string.Equals(Path.GetExtension(backing), ".etl", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backing file must be an absolute .etl path.");
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var directoryWithSeparator = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (protectedRoots.Any(root => directoryWithSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Backing files cannot be written below a protected system directory.");
        RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
    }

    private static void RejectReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Backing-file directory cannot contain links or reparse points.");
            current = current.Parent;
        }
    }
}

public sealed class ElevatedWorkerClient(ITargetProcessLauncher targetLauncher, string? workerExecutablePath = null) : IElevatedWorkerClient
{
    public async Task<ElevatedCaptureResult> CaptureAsync(
        Guid sessionId, CaptureProfile profile, string backingFile,
        IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
    {
        var pipeName = $"WprHelper-{sessionId:N}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))}";
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("Unable to determine the current Windows user.");
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetOwner(userSid);
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(administratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            0, 0, pipeSecurity, HandleInheritability.None);
        var executable = workerExecutablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the application executable.");
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        startInfo.ArgumentList.Add("--elevated-worker");
        startInfo.ArgumentList.Add("--pipe"); startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--session"); startInfo.ArgumentList.Add(sessionId.ToString("D"));
        using var worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the elevated worker.");
        var connected = pipe.WaitForConnectionAsync(cancellationToken);
        var exited = worker.WaitForExitAsync(cancellationToken);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), connectTimeout.Token);
        var first = await Task.WhenAny(connected, exited, timeoutTask);
        connectTimeout.Cancel();
        if (first == exited)
            throw new InvalidOperationException($"Elevated worker exited before connecting (code {worker.ExitCode}).");
        if (first != connected)
            throw new TimeoutException("Elevated worker did not connect within 30 seconds.");
        await connected;
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new PipeStreamWriter(pipe) { AutoFlush = true };
        await PipeJson.WriteAsync(writer, new StartCaptureCommand(sessionId, profile, backingFile), cancellationToken);

        WorkerEvent evt;
        while (true)
        {
            evt = await PipeJson.ReadAsync<WorkerEvent>(reader, cancellationToken) ?? throw new IOException("Elevated worker disconnected before Windows Performance Recorder became ready.");
            if (evt.Kind == "error") throw new InvalidOperationException(evt.Message);
            if (evt.Kind == "stopped") throw new InvalidOperationException("Elevated worker stopped before Windows Performance Recorder became ready.");
            if (evt.Kind == "started") progress?.Report(new(CaptureState.StartingWpr, evt.Message, TimeSpan.Zero, 0, 0, null));
            if (evt.Kind == "ready") break;
        }

        var started = DateTimeOffset.Now;
        progress?.Report(new(CaptureState.WaitingForWpr, evt.Message, TimeSpan.Zero, 0, 0, null));
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task SendAsync(WorkerCommand command)
        {
            await writeGate.WaitAsync(CancellationToken.None);
            try { await PipeJson.WriteAsync(writer, command, CancellationToken.None); }
            finally { writeGate.Release(); }
        }
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCts.IsCancellationRequested)
            {
                await Task.Delay(1000, heartbeatCts.Token);
                await SendAsync(new HeartbeatCommand(sessionId));
            }
        }, heartbeatCts.Token);
        TrackedTargetProcess? target = null;
        try
        {
            if (profile.LaunchTargetApplication)
            {
                progress?.Report(new(CaptureState.LaunchingTarget, "Launching target application.", TimeSpan.Zero, 0, 0, null));
                try
                {
                    target = await Task.Run(() => targetLauncher.LaunchAsync(profile, cancellationToken), CancellationToken.None);
                    await SendAsync(new SetTargetPidCommand(sessionId, target.Pid, target.StartedAt));
                }
                catch
                {
                    try { await SendAsync(new StopCaptureCommand(sessionId, StopReason.Error)); } catch (IOException) { }
                    throw;
                }
            }
            progress?.Report(new(CaptureState.Capturing, "Capturing", TimeSpan.Zero, 0, 0, target?.Pid));
            var stopSent = false;
            var read = PipeJson.ReadAsync<WorkerEvent>(reader, CancellationToken.None);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested && !stopSent)
                {
                    stopSent = true;
                    try { await SendAsync(new StopCaptureCommand(sessionId, StopReason.Manual)); }
                    catch (IOException)
                    {
                        // The worker may already be finalizing for another reason. Consume its queued
                        // stopping/stopped event instead of replacing a valid capture with Pipe is broken.
                    }
                }
                var delayTask = Task.Delay(100, CancellationToken.None);
                var completed = await Task.WhenAny(read, delayTask);
                if (completed != read)
                {
                    continue;
                }
                evt = await read ?? throw new IOException("Elevated worker disconnected.");
                if (evt.Kind == "error") throw new InvalidOperationException(evt.Message);
                if (evt.Kind == "stopping")
                {
                    progress?.Report(new(CaptureState.StoppingWpr, evt.Message, DateTimeOffset.Now - started, evt.EtlBytes, evt.FreeBytes, target?.Pid, evt.StopReason));
                    read = PipeJson.ReadAsync<WorkerEvent>(reader, CancellationToken.None);
                    continue;
                }
                if (evt.Kind == "stopped")
                    return new(target?.Pid, evt.StopReason, started);
                progress?.Report(new(CaptureState.Capturing, evt.Message, DateTimeOffset.Now - started, evt.EtlBytes, evt.FreeBytes, target?.Pid, evt.StopReason));
                read = PipeJson.ReadAsync<WorkerEvent>(reader, CancellationToken.None);
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
