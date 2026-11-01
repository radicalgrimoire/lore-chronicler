using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

[Flags]
public enum FileChangeKind
{
    None = 0,
    Modified = 1,
    Untracked = 2,
    Staged = 4,
    Changed = 8,
    Conflict = 16
}

public interface IFileStatusService
{
    Task<FileStatusSnapshot> ScanAsync(string path);
}

public sealed record StagedEntry(string FullPath, string RelativePath, bool IsDirectory)
{
    public string DisplayName => IsDirectory ? $"{RelativePath} (フォルダー)" : RelativePath;
}

public sealed class FileStatusSnapshot
{
    private readonly Dictionary<string, FileChangeKind> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> stagedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;

    public FileStatusSnapshot(string root, IReadOnlyDictionary<string, FileChangeKind> changes,
        IEnumerable<string>? stagedDirectories = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.root = fullRoot;
        if (stagedDirectories is not null)
            foreach (var path in stagedDirectories)
                this.stagedDirectories.Add(LoreFileStatusService.ResolvePath(fullRoot, path));
        foreach (var (relativePath, kind) in changes)
        {
            if (kind == FileChangeKind.None) continue;
            var fullPath = LoreFileStatusService.ResolvePath(fullRoot, relativePath);
            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
            files.TryGetValue(fullPath, out var previous);
            files[fullPath] = previous | kind;
            var parent = Path.GetDirectoryName(fullPath);
            while (parent is not null && !string.Equals(parent, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!directories.Add(parent)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }
        UnresolvedConflictCount = files.Values.Count(kind => (kind & FileChangeKind.Conflict) != 0);
    }

    public FileChangeKind GetFileStatus(string path) => files.GetValueOrDefault(path);
    public bool HasDirectoryChanges(string path) => directories.Contains(path) || files.ContainsKey(path);
    public IReadOnlyList<string> StagedPaths => files
        .Where(entry => (entry.Value & (FileChangeKind.Staged | FileChangeKind.Conflict)) == FileChangeKind.Staged)
        .Select(entry => entry.Key)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<StagedEntry> StagedEntries => StagedPaths
        .Select(path => new StagedEntry(path, Path.GetRelativePath(root, path), stagedDirectories.Contains(path)))
        .ToArray();
    public int UnresolvedConflictCount { get; }
}

public sealed class LoreFileStatusService : IFileStatusService
{
    public Task<FileStatusSnapshot> ScanAsync(string path) =>
        Task.Run(() => ScanCoreAsync(path));

    private static async Task<FileStatusSnapshot> ScanCoreAsync(string path)
    {
        var changes = new Dictionary<string, FileChangeKind>(StringComparer.OrdinalIgnoreCase);
        var stagedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        string? error = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
        using var args = new LoreRepositoryStatusArgs { Scan = true, Staged = true };
        await Lore.RepositoryStatus(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.ERROR)
            {
                var message = ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                lock (sync) error ??= message;
                return;
            }
            if (ev.Tag != LoreEventTag.REPOSITORY_STATUS_FILE) return;
            var data = ev.GetData<LoreRepositoryStatusFileEventDataFFI>();
            var change = data.FlagConflictUnresolved
                ? FileChangeKind.Conflict
                : data.FlagStaged
                    ? FileChangeKind.Staged | (data.Action == LoreFileAction.KEEP ? FileChangeKind.Modified : FileChangeKind.None)
                    : data.Action == LoreFileAction.ADD ? FileChangeKind.Untracked
                    : data.Action == LoreFileAction.KEEP ? FileChangeKind.Modified : FileChangeKind.Changed;
            if (data.FlagStaged && !data.FlagConflictUnresolved) change |= FileChangeKind.Staged;
            lock (sync)
            {
                changes.TryGetValue(data.Path, out var previous);
                changes[data.Path] = previous | change;
                if (data.FlagStaged && !data.FlagConflictUnresolved && data.Type == LoreNodeType.DIRECTORY)
                    stagedDirectories.Add(data.Path);
            }
        }).WaitAsync();
        if (error is not null) throw new InvalidDataException($"Loreの変更状態を完全に取得できません: {error}");
        return new FileStatusSnapshot(path, changes, stagedDirectories);
    }

    public static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Loreから不正なファイルパスが返されました。");
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Loreからプロジェクト外のパスが返されました。");
        return fullPath;
    }
}
