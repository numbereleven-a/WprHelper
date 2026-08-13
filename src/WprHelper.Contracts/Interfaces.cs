namespace WprHelper.Contracts;

public interface IWprCommandBuilder
{
    IReadOnlyList<string> BuildStart(CaptureProfile profile);
    string FormatStart(CaptureProfile profile);
    IReadOnlyList<string> BuildStop(string etlPath, bool skipPdbGeneration = true);
    IReadOnlyList<string> BuildCancel();
}

public interface IWprCapabilityDetector
{
    Task<WprCapabilities> DetectAsync(string executablePath, CancellationToken cancellationToken);
}

public interface IWprController
{
    Task StartAsync(CaptureProfile profile, TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync(string executablePath, string etlPath, bool skipPdbGeneration, TimeSpan timeout, CancellationToken cancellationToken);
    Task CancelAsync(string executablePath, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface ITargetProcessLauncher
{
    Task<TrackedTargetProcess> LaunchAsync(CaptureProfile profile, CancellationToken cancellationToken);
}

public interface IElevatedWorkerClient
{
    Task<ElevatedCaptureResult> CaptureAsync(Guid sessionId, CaptureProfile profile, string backingFile,
        IProgress<CaptureProgress>? progress, CancellationToken cancellationToken);
}

public interface ISessionManager
{
    Task<CaptureResult> CaptureAsync(CaptureProfile profile, IProgress<CaptureProgress>? progress,
        CancellationToken captureCancellationToken, CancellationToken postProcessCancellationToken = default);
}

public interface ISessionRepository
{
    Task SaveAsync(SessionRecord session, CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionRecord>> FindRecoverableAsync(CancellationToken cancellationToken);
}

public interface IProfileRepository
{
    Task<IReadOnlyList<CaptureProfile>> LoadAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken);
    Task RenameAsync(string oldName, CaptureProfile renamed, CancellationToken cancellationToken);
    Task DeleteAsync(string name, CancellationToken cancellationToken);
    Task<CaptureProfile> ImportAsync(string path, CancellationToken cancellationToken);
    Task ExportAsync(CaptureProfile profile, string path, CancellationToken cancellationToken);
}

public interface IFileTransferService
{
    Task<string> CopyAtomicAsync(string source, string destination, bool overwrite, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken);
}

public interface IStoragePathResolver
{
    string DataRoot { get; }
    string SessionsRoot { get; }
    string ProfilesRoot { get; }
    string LogsRoot { get; }
    string CreateSessionDirectory(Guid sessionId);
}

public interface IDiskSpaceService { long GetFreeBytes(string path); }
public interface IHashService { Task<string> Sha256Async(string path, CancellationToken cancellationToken); }
public interface IClock { DateTimeOffset Now { get; } }
