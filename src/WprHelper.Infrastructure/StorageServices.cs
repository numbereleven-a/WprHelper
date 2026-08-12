using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using WprHelper.Contracts;

namespace WprHelper.Infrastructure;

public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.Now; }

public sealed class StoragePathResolver : IStoragePathResolver
{
    public StoragePathResolver(string? applicationDirectory = null)
    {
        applicationDirectory ??= AppContext.BaseDirectory;
        var portable = Path.Combine(applicationDirectory, "Data");
        DataRoot = CanWrite(applicationDirectory) ? portable : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WprHelper");
        SessionsRoot = Path.Combine(DataRoot, "Sessions");
        ProfilesRoot = Path.Combine(DataRoot, "Profiles");
        LogsRoot = Path.Combine(DataRoot, "Logs");
        Directory.CreateDirectory(SessionsRoot);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string DataRoot { get; }
    public string SessionsRoot { get; }
    public string ProfilesRoot { get; }
    public string LogsRoot { get; }

    public string CreateSessionDirectory(Guid sessionId)
    {
        var root = Path.Combine(SessionsRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(root);
        GrantSessionAccess(root);
        Directory.CreateDirectory(Path.Combine(root, "wpr"));
        Directory.CreateDirectory(Path.Combine(root, "export"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void GrantSessionAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("Unable to determine the current Windows user.");
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.FullControl, inheritance,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(path, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }
}

public sealed partial class DiskSpaceService : IDiskSpaceService
{
    public long GetFreeBytes(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (GetDiskFreeSpaceEx(full, out var available, out _, out _))
                return available > long.MaxValue ? long.MaxValue : (long)available;
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Unable to read free space for '{full}'.", new System.ComponentModel.Win32Exception(error));
        }
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("Unable to determine drive.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}

public sealed class HashService : IHashService
{
    public async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}

public sealed class JsonSessionRepository(IStoragePathResolver paths, IClock clock) : ISessionRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task SaveAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var path = Path.Combine(session.SessionDirectory, "session.json");
        var updated = session with { UpdatedAt = clock.Now };
        await WriteAtomicAsync(path, updated, cancellationToken);
        var sessionsRoot = Path.GetFullPath(paths.SessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(session.SessionDirectory).StartsWith(sessionsRoot, StringComparison.OrdinalIgnoreCase))
        {
            var indexPath = Path.Combine(paths.SessionsRoot, session.SessionId.ToString("N"), "session.json");
            await WriteAtomicAsync(indexPath, updated, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<SessionRecord>> FindRecoverableAsync(CancellationToken cancellationToken)
    {
        var terminal = new[] { CaptureState.Completed, CaptureState.CompletedWithWarnings, CaptureState.Failed };
        var result = new List<SessionRecord>();
        foreach (var path in Directory.EnumerateFiles(paths.SessionsRoot, "session.json", SearchOption.AllDirectories))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var session = await JsonSerializer.DeserializeAsync<SessionRecord>(stream, Options, cancellationToken);
                if (session is not null && !terminal.Contains(session.State)) result.Add(session);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static async Task WriteAtomicAsync(string path, SessionRecord session, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, session, Options, token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, true);
    }
}

public sealed class JsonProfileRepository(IStoragePathResolver paths) : IProfileRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public async Task<IReadOnlyList<CaptureProfile>> LoadAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<CaptureProfile>();
        foreach (var file in Directory.EnumerateFiles(paths.ProfilesRoot, "*.json"))
            try { result.Add(await ReadAsync(file, cancellationToken)); } catch (JsonException) { }
        return result;
    }
    public async Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        var existing = await FindPathByNameAsync(profile.Name, cancellationToken);
        var path = existing ?? Path.Combine(paths.ProfilesRoot, $"profile-{Guid.NewGuid():N}.json");
        await WriteAtomicAsync(profile, path, cancellationToken);
    }
    public async Task RenameAsync(string oldName, CaptureProfile renamed, CancellationToken cancellationToken)
    {
        var path = await FindPathByNameAsync(oldName, cancellationToken) ?? throw new FileNotFoundException("The selected profile no longer exists.");
        var collision = await FindPathByNameAsync(renamed.Name, cancellationToken);
        if (collision is not null && !string.Equals(collision, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A profile with that name already exists.");
        await WriteAtomicAsync(renamed, path, cancellationToken);
    }
    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var path = await FindPathByNameAsync(name, cancellationToken);
        if (path is not null) File.Delete(path);
    }
    public Task<CaptureProfile> ImportAsync(string path, CancellationToken cancellationToken) => ReadAsync(path, cancellationToken);
    public Task ExportAsync(CaptureProfile profile, string path, CancellationToken cancellationToken) => WriteAtomicAsync(profile, path, cancellationToken);

    private async Task<string?> FindPathByNameAsync(string name, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFiles(paths.ProfilesRoot, "*.json"))
        {
            try
            {
                var profile = await ReadAsync(path, token);
                if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)) return path;
            }
            catch (JsonException) { }
        }
        return null;
    }
    private static async Task<CaptureProfile> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        var profile = await JsonSerializer.DeserializeAsync<CaptureProfile>(stream, Options, token) ?? throw new JsonException("Profile is empty.");
        if (profile.Stop is null)
            throw new JsonException("Profile is missing required stop settings.");
        return profile.SchemaVersion switch
        {
            1 or 2 => profile with
            {
                SchemaVersion = 3,
                WprProfiles = string.IsNullOrWhiteSpace(profile.WprProfile) ? [] : [profile.WprProfile]
            },
            3 => profile,
            _ => throw new JsonException($"Unsupported profile schema {profile.SchemaVersion}.")
        };
    }
    private static async Task WriteAtomicAsync(CaptureProfile profile, string path, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, profile, Options, token);
            await stream.FlushAsync(token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, true);
    }
}

public sealed class FileTransferService(IHashService? hashService = null) : IFileTransferService
{
    public async Task<string> CopyAtomicAsync(string source, string destination, bool overwrite, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && !overwrite) throw new IOException("Destination file already exists.");
        var partial = destination + ".partial";
        var verifyHash = hashService is not null && IsNetworkPath(destination);
        using var sourceHash = verifyHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sourceHash?.AppendData(buffer, 0, read);
                copied += read;
                progress?.Report(new(copied, input.Length));
            }
            await output.FlushAsync(cancellationToken);
            if (output.Length != input.Length) throw new IOException("Copied file size does not match the source.");
            output.Close();
            input.Close();
            if (verifyHash)
            {
                var expectedHash = Convert.ToHexString(sourceHash!.GetHashAndReset());
                var destinationHash = await hashService!.Sha256Async(partial, cancellationToken);
                if (!string.Equals(expectedHash, destinationHash, StringComparison.Ordinal))
                    throw new IOException("Copied file hash does not match the source.");
            }
            File.Move(partial, destination, overwrite);
            return destination;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
