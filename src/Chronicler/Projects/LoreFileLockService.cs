using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record FileLockOwner(string Id, string? Name, string Branch);
public sealed record FileLockResult(IReadOnlyDictionary<string, IReadOnlyList<FileLockOwner>> Files, string? Warning);
public sealed record MyFileLock(string FullPath, string RelativePath, string Branch, string OwnerId);

public interface IFileLockService
{
    Task<FileLockResult> QueryAsync(string path);
}

public interface IFileLockActionsService
{
    Task<IReadOnlyList<MyFileLock>> QueryMineAsync(string path);
    Task AcquireAsync(string path, string file);
    Task ReleaseAsync(string path, MyFileLock fileLock);
}

public sealed class LoreFileLockService : IFileLockService, IFileLockActionsService
{
    public Task<FileLockResult> QueryAsync(string path) => Task.Run(() => QueryCoreAsync(path));

    private static async Task<FileLockResult> QueryCoreAsync(string path)
    {
        var sync = new object();
        using var globals = new LoreGlobalArgs { RepositoryPath = path, WorkingDirectory = Path.GetFullPath(path) };
        var locks = await QueryEntriesAsync(globals);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string? warning = null;
        var owners = locks.Select(entry => entry.Owner).Where(owner => !string.IsNullOrWhiteSpace(owner))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Length > 0)
        {
            try
            {
                string? authError = null;
                using var args = new LoreAuthUserInfoArgs { UserIds = owners };
                await Lore.AuthUserInfo(globals, args).Callback((ev, _) =>
                {
                    if (ev.Tag == LoreEventTag.AUTH_USER_INFO)
                    {
                        var data = ev.GetData<LoreAuthUserInfoEventDataFFI>();
                        lock (sync) names[data.Id] = data.Name;
                    }
                    else if (ev.Tag == LoreEventTag.ERROR)
                    {
                        var message = ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                        lock (sync) authError ??= message;
                    }
                }).WaitAsync();
                if (authError is not null) warning = $"Could not resolve some lock owners: {authError}";
            }
            catch (LoreError ex)
            {
                warning = $"Could not resolve lock owners (Lore error {ex.ReturnCode}): {ex.Message}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                warning = $"Could not resolve lock owners: {ex.Message}";
            }
        }

        var files = new Dictionary<string, List<FileLockOwner>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in locks)
        {
            var fullPath = LoreFileStatusService.ResolvePath(path, entry.Path);
            if (!files.TryGetValue(fullPath, out var list)) files.Add(fullPath, list = []);
            names.TryGetValue(entry.Owner, out var name);
            list.Add(new FileLockOwner(entry.Owner, name, entry.Branch));
        }
        if (warning is null && locks.Any(entry => !names.TryGetValue(entry.Owner, out var name) || string.IsNullOrWhiteSpace(name)))
            warning = "Some lock owners could not be resolved; user IDs are shown.";
        return new FileLockResult(files.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyList<FileLockOwner>)entry.Value.ToArray(), StringComparer.OrdinalIgnoreCase), warning);
    }

    private static async Task<List<(string Path, string Owner, string Branch)>> QueryEntriesAsync(
        LoreGlobalArgs globals, string? owner = null)
    {
        var locks = new List<(string Path, string Owner, string Branch)>();
        var sync = new object();
        string? queryError = null;
        ulong? expectedCount = null;
        using var query = new LoreLockFileQueryArgs { Owner = owner ?? string.Empty };
        await Lore.LockFileQuery(globals, query).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.LOCK_FILE_QUERY_BEGIN)
            {
                var count = ev.GetData<LoreLockFileQueryBeginEventDataFFI>().Count;
                lock (sync) expectedCount = count;
            }
            else if (ev.Tag == LoreEventTag.LOCK_FILE_QUERY)
            {
                var data = ev.GetData<LoreLockFileQueryEventDataFFI>();
                lock (sync) locks.Add((data.Path, data.Owner, Convert.ToHexString(data.Branch.Data)));
            }
            else if (ev.Tag == LoreEventTag.ERROR)
            {
                var message = ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                lock (sync) queryError ??= message;
            }
        }).WaitAsync();
        if (queryError is not null) throw new InvalidDataException($"Could not retrieve all locks: {queryError}");
        if (expectedCount is null || expectedCount != (ulong)locks.Count)
            throw new InvalidDataException("Lore did not return the complete lock list.");

        return locks;
    }

    public Task<IReadOnlyList<MyFileLock>> QueryMineAsync(string path) => Task.Run(async () =>
    {
        using var globals = new LoreGlobalArgs { RepositoryPath = path, WorkingDirectory = Path.GetFullPath(path) };
        var owner = await ReadCurrentOwnerAsync(globals);
        var entries = await QueryEntriesAsync(globals, owner);
        if (entries.Any(entry => entry.Owner != owner))
            throw new InvalidDataException("The lock list contains another user's lock.");
        return (IReadOnlyList<MyFileLock>)entries.Select(entry =>
        {
            var fullPath = LoreFileStatusService.ResolvePath(path, entry.Path);
            if (string.IsNullOrWhiteSpace(entry.Branch) || entry.Branch.All(c => c == '0'))
                throw new InvalidDataException("Could not verify the lock's branch.");
            return new MyFileLock(fullPath, Path.GetRelativePath(path, fullPath), entry.Branch, owner);
        }).OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Branch).ToArray();
    });

    private static async Task<string> ReadCurrentOwnerAsync(LoreGlobalArgs globals)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        string? error = null;
        var sync = new object();
        using var args = new LoreAuthLocalUserInfoArgs { WithAccessToken = true };
        await Lore.AuthLocalUserInfo(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.AUTH_IDENTITY)
                    owners.Add(ev.GetData<LoreAuthIdentityEventDataFFI>().UserId);
                else if (ev.Tag == LoreEventTag.ERROR)
                    error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
            }
        }).WaitAsync();
        if (error is not null || owners.Count != 1 || string.IsNullOrWhiteSpace(owners.Single()))
            throw new InvalidOperationException($"Could not identify the signed-in user. Sign in again.\n{error}");
        return owners.Single();
    }

    public Task AcquireAsync(string path, string file) => Task.Run(async () =>
    {
        var fullPath = ResolveLockTarget(path, file);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Only regular files can be locked.");
        using var globals = new LoreGlobalArgs { RepositoryPath = path, WorkingDirectory = Path.GetFullPath(path) };
        using var args = new LoreLockFileAcquireArgs { Paths = [fullPath] };
        string? error = null;
        var completed = new List<string>();
        var sync = new object();
        await Lore.LockFileAcquire(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.ERROR) error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                else if (ev.Tag == LoreEventTag.LOCK_FILE_ACQUIRE)
                    completed.Add(ev.GetData<LoreLockFileAcquireEventDataFFI>().Path);
            }
        }).WaitAsync();
        if (error is not null || completed.Count != 1
            || !string.Equals(fullPath, LoreFileStatusService.ResolvePath(path, completed[0]), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not verify lock acquisition. Refresh the lock list.\n{error}");
    });

    public Task ReleaseAsync(string path, MyFileLock fileLock) => Task.Run(async () =>
    {
        var fullPath = ResolveLockTarget(path, fileLock.FullPath);
        var mine = await QueryMineAsync(path);
        if (!mine.Any(entry => entry.OwnerId == fileLock.OwnerId && entry.Branch == fileLock.Branch
            && string.Equals(entry.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected lock does not belong to the current user. Refresh the lock list.");
        using var globals = new LoreGlobalArgs { RepositoryPath = path, WorkingDirectory = Path.GetFullPath(path) };
        using var args = new LoreLockFileReleaseArgs
        {
            Paths = [fullPath], Branch = fileLock.Branch, OwnerId = fileLock.OwnerId
        };
        string? error = null;
        var completed = new List<string>();
        var sync = new object();
        await Lore.LockFileRelease(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.ERROR) error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                else if (ev.Tag == LoreEventTag.LOCK_FILE_RELEASE)
                    completed.Add(ev.GetData<LoreLockFileReleaseEventDataFFI>().Path);
            }
        }).WaitAsync();
        if (error is not null || completed.Count != 1
            || !string.Equals(fullPath, LoreFileStatusService.ResolvePath(path, completed[0]), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not verify lock release. Refresh the lock list.\n{error}");
    });

    private static string ResolveLockTarget(string root, string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var relative = Path.IsPathFullyQualified(file) ? Path.GetRelativePath(root, file) : file;
        var fullPath = LoreFileStatusService.ResolvePath(root, relative);
        var segments = Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".lore", StringComparison.OrdinalIgnoreCase)) || relative == ".")
            throw new InvalidDataException("The repository root and Lore metadata cannot be locked.");
        var current = Path.GetFullPath(root);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Paths containing links cannot be used for lock operations.");
        }
        return fullPath;
    }
}
