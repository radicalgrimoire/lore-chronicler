using System.IO;
using System.Windows;
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

public enum ChangeAction
{
    Deleted,
    Added,
    Modified,
    Moved,
    Copied,
    Other,
    Folder
}

public sealed record StagedEntry(string FullPath, string RelativePath, bool IsDirectory,
    ChangeAction Action = ChangeAction.Other, bool IsStaged = false, bool IsUnstaged = true)
{
    public int Depth { get; init; }
    public bool HasChildren { get; init; }
    public bool IsExpanded { get; init; }
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string TreeName => Path.GetFileName(FullPath);
    public string ExpandMarker => IsExpanded ? "▼" : "▶";
    public string ExpandAccessibleName => $"{RelativePath} {(IsExpanded ? "collapse" : "expand")}";
    public string DisplayName => IsDirectory ? $"{RelativePath} (folder)" : RelativePath;
    public string ActionText => Action switch
    {
        ChangeAction.Deleted => "Delete",
        ChangeAction.Added => "Added",
        ChangeAction.Modified => "Modified",
        ChangeAction.Moved => "Moved",
        ChangeAction.Copied => "Copied",
        ChangeAction.Folder => "Folder (descendant changes)",
        _ => "Other change"
    };
    public string Icon => IsDirectory ? "📁" : ActionIcon;
    public string DirectoryActionIcon => IsDirectory && Action != ChangeAction.Folder ? ActionIcon : string.Empty;
    private string ActionIcon => Action switch
    {
        ChangeAction.Deleted => "−",
        ChangeAction.Added => "＋",
        ChangeAction.Modified => "✎",
        ChangeAction.Moved => "→",
        ChangeAction.Copied => "⧉",
        ChangeAction.Folder => "▸",
        _ => "●"
    };
    public string StateMarker => IsStaged && IsUnstaged ? "✓・" : IsStaged ? "✓" : "・";
    public string StateText => (IsDirectory ? "Folder or descendants: " : string.Empty)
        + (IsStaged && IsUnstaged ? "staged and unstaged" : IsStaged ? "staged" : "unstaged");
    public string Background => IsStaged ? "#FFE4F5E8" : "#FFFFF2C6";
    public string StateColor => IsStaged ? "#FF288348" : "#FFE5A000";
    public string AccessibleName => $"{ActionText} {DisplayName} {StateText}";
}

public sealed class FileStatusSnapshot
{
    private readonly Dictionary<string, FileChangeKind> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> stagedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> changeDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChangeAction> actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;

    public FileStatusSnapshot(string root, IReadOnlyDictionary<string, FileChangeKind> changes,
        IEnumerable<string>? stagedDirectories = null, IReadOnlyDictionary<string, ChangeAction>? actions = null,
        IEnumerable<string>? changeDirectories = null)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.root = fullRoot;
        if (stagedDirectories is not null)
            foreach (var path in stagedDirectories)
                this.stagedDirectories.Add(LoreFileStatusService.ResolvePath(fullRoot, path));
        if (changeDirectories is not null)
            foreach (var path in changeDirectories)
                this.changeDirectories.Add(LoreFileStatusService.ResolvePath(fullRoot, path));
        if (actions is not null)
            foreach (var (path, action) in actions)
                this.actions[LoreFileStatusService.ResolvePath(fullRoot, path)] = action;
        foreach (var (relativePath, kind) in changes)
        {
            if (kind == FileChangeKind.None) continue;
            var fullPath = LoreFileStatusService.ResolvePath(fullRoot, relativePath);
            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
            files.TryGetValue(fullPath, out var previous);
            files[fullPath] = previous | kind;
        }
        UnresolvedConflictCount = files.Values.Count(kind => (kind & FileChangeKind.Conflict) != 0);
        StagedCount = files.Values.Count(kind => (kind & (FileChangeKind.Staged | FileChangeKind.Conflict)) == FileChangeKind.Staged);
    }

    public FileChangeKind GetFileStatus(string path) => files.GetValueOrDefault(path);
    public IReadOnlyList<string> StagedPaths => files
        .Where(entry => (entry.Value & (FileChangeKind.Staged | FileChangeKind.Conflict)) == FileChangeKind.Staged)
        .Select(entry => entry.Key)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public IReadOnlyList<StagedEntry> StagedEntries => StagedPaths
        .Select(path => CreateEntry(path, stagedDirectories.Contains(path)))
        .ToArray();
    public IReadOnlyList<StagedEntry> StagedDisplayEntries => WithParentFolders(StagedEntries, true);
    public IReadOnlyList<StagedEntry> UnstagedEntries => WithParentFolders(files
        .Where(entry => (entry.Value & (FileChangeKind.Staged | FileChangeKind.Conflict)) == 0)
        .Select(entry => CreateEntry(entry.Key, changeDirectories.Contains(entry.Key))), false);
    public IReadOnlyList<StagedEntry> AllDisplayEntries
    {
        get
        {
            var combined = StagedDisplayEntries.Concat(UnstagedEntries).ToArray();
            var entries = new Dictionary<string, StagedEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in combined)
            {
                if (!entries.TryGetValue(entry.FullPath, out var previous))
                {
                    entries.Add(entry.FullPath, entry);
                    continue;
                }
                var preferred = previous.Action == ChangeAction.Folder ? entry : previous;
                entries[entry.FullPath] = preferred with
                {
                    IsStaged = previous.IsStaged || entry.IsStaged,
                    IsUnstaged = previous.IsUnstaged || entry.IsUnstaged
                };
            }
            return entries.Values.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private IReadOnlyList<StagedEntry> WithParentFolders(IEnumerable<StagedEntry> changes, bool staged)
    {
        var entries = changes.ToList();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.FullPath);
            while (parent is not null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                if (!folders.Add(parent)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }
        var entryPaths = entries.Select(entry => entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        entries.AddRange(folders.Where(folder => !entryPaths.Contains(folder))
            .Select(folder => new StagedEntry(folder, Path.GetRelativePath(root, folder), true, ChangeAction.Folder, staged, !staged)));
        return entries.OrderBy(entry => entry.Action).ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private StagedEntry CreateEntry(string path, bool isDirectory)
    {
        var kind = files[path];
        var action = actions.GetValueOrDefault(path, (kind & FileChangeKind.Untracked) != 0
            ? ChangeAction.Added : (kind & FileChangeKind.Modified) != 0 ? ChangeAction.Modified : ChangeAction.Other);
        var staged = (kind & FileChangeKind.Staged) != 0;
        return new StagedEntry(path, Path.GetRelativePath(root, path), isDirectory, action, staged, !staged);
    }
    public int UnresolvedConflictCount { get; }
    public int StagedCount { get; }
}

public sealed class LoreFileStatusService : IFileStatusService
{
    public Task<FileStatusSnapshot> ScanAsync(string path) =>
        Task.Run(() => ScanCoreAsync(path));

    private static async Task<FileStatusSnapshot> ScanCoreAsync(string path)
    {
        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        var changes = new Dictionary<string, FileChangeKind>(StringComparer.OrdinalIgnoreCase);
        var stagedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = new Dictionary<string, ChangeAction>(StringComparer.OrdinalIgnoreCase);
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
                if (!actions.ContainsKey(data.Path) || data.FlagStaged)
                    actions[data.Path] = data.Action switch
                    {
                        LoreFileAction.ADD => ChangeAction.Added,
                        LoreFileAction.DELETE => ChangeAction.Deleted,
                        LoreFileAction.KEEP => ChangeAction.Modified,
                        LoreFileAction.MOVE => ChangeAction.Moved,
                        LoreFileAction.COPY => ChangeAction.Copied,
                        _ => ChangeAction.Other
                    };
                if (data.Type == LoreNodeType.DIRECTORY) changeDirectories.Add(data.Path);
                if (data.FlagStaged && !data.FlagConflictUnresolved && data.Type == LoreNodeType.DIRECTORY)
                    stagedDirectories.Add(data.Path);
            }
        }).WaitAsync();
        if (error is not null) throw new InvalidDataException($"Could not retrieve all Lore changes: {error}");
        return new FileStatusSnapshot(path, changes, stagedDirectories, actions, changeDirectories);
    }

    public static string ResolvePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Lore returned an invalid file path.");
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Lore returned a path outside the project.");
        return fullPath;
    }
}
