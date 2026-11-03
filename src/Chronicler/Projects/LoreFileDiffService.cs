using System.IO;
using System.Text;
using LoreVcs;
using LoreVcs.Types;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record FileDiff(string? Before, string? After, string BeforeLabel, string AfterLabel, bool IsText, string? Notice);

public interface IFileDiffService
{
    Task<FileDiff> ReadAsync(string path, string revision, RevisionChange change);
}

public sealed class LoreFileDiffService : IFileDiffService
{
    private const int MaximumBytes = 1_000_000;
    private const int MaximumLines = 2_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<FileDiff> ReadAsync(string path, string revision, RevisionChange change)
    {
        if (!change.IsFile) return new FileDiff(null, null, "Before", "After", false, "Folders cannot be compared.");
        var parent = await ReadParentAsync(path, revision);
        var beforePath = change.Action is ChangeAction.Moved or ChangeAction.Copied ? change.FromPath : change.Path;
        var before = change.Action == ChangeAction.Added ? null
            : await ReadVersionAsync(path, parent, beforePath);
        var after = change.Action == ChangeAction.Deleted ? null
            : await ReadVersionAsync(path, revision, change.Path);

        if (before is { Truncated: true } || after is { Truncated: true })
            return new FileDiff(null, null, "Before", "After", false, $"Files over {MaximumBytes:N0} bytes cannot be compared.");
        if (!TryDecode(before?.Bytes, out var beforeText) || !TryDecode(after?.Bytes, out var afterText))
            return new FileDiff(null, null, "Before", "After", false, "Only UTF-8 text files can be compared.");
        if (CountLines(beforeText) > MaximumLines || CountLines(afterText) > MaximumLines)
            return new FileDiff(null, null, "Before", "After", false, $"Files over {MaximumLines:N0} lines cannot be compared.");
        return new FileDiff(beforeText, afterText, beforePath ?? change.Path, change.Path, true, null);
    }

    private static async Task<string> ReadParentAsync(string path, string revision)
    {
        string? parent = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionInfoArgs { Revision = revision };
        await Lore.RevisionInfo(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag != LoreEventTag.REVISION_INFO) return;
            var data = ev.GetData<LoreRevisionInfoEventDataFFI>();
            if (!string.Equals(Convert.ToHexString(data.Revision.Data), revision, StringComparison.OrdinalIgnoreCase)) return;
            parent = data.Parent.FirstOrDefault(parent => parent.Any(value => value != 0)) is { } hash
                ? Convert.ToHexString(hash)
                : null;
        }).WaitAsync();
        return parent ?? string.Empty;
    }

    private static async Task<FileVersion?> ReadVersionAsync(string path, string revision, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(revision) || string.IsNullOrWhiteSpace(filePath)) return null;
        var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullPath = normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath
            : Path.Combine(root, normalizedPath);
        LoreAddress? address = null;
        byte[]? repositoryId = null;
        ulong size = 0;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreFileHistoryArgs { Path = fullPath, Revision = revision, Length = 1 };
        await Lore.FileHistory(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag != LoreEventTag.FILE_HISTORY || address is not null) return;
            var data = ev.GetData<LoreFileHistoryEventDataFFI>();
            if (data.Action == LoreFileAction.DELETE) return;
            address = data.Address.Clone();
            repositoryId = data.Repository.Data.ToArray();
            size = data.Size;
        }).WaitAsync();
        if (address is null || repositoryId is null) return null;
        using (address)
        {
            if (size > MaximumBytes) return new FileVersion(null, true);
            return new FileVersion(await ReadStorageAsync(path, address.Value, repositoryId), false);
        }
    }

    private static async Task<byte[]> ReadStorageAsync(string path, LoreAddress address, byte[] repositoryId)
    {
        ulong handle = 0;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var open = new LoreStorageOpenArgs { RepositoryPath = path };
        await Lore.StorageOpen(globals, open).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.STORAGE_OPENED)
                handle = ev.GetData<LoreStorageOpenedEventDataFFI>().HandleId;
        }).WaitAsync();
        if (handle == 0) throw new InvalidDataException("Could not open the repository content store.");

        try
        {
            var bytes = new List<byte>();
            using var store = new LoreStore(handle);
            using var item = new LoreStorageGetItem
            {
                Id = 1,
                Partition = new LorePartition(repositoryId),
                Address = address.Clone(),
                Length = MaximumBytes + 1
            };
            using var get = new LoreStorageGetArgs { Handle = store, Items = [item] };
            await Lore.StorageGet(globals, get).Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.STORAGE_GET_DATA)
                    bytes.AddRange(ev.GetData<LoreStorageGetDataEventDataFFI>().Bytes);
            }).WaitAsync();
            if (bytes.Count > MaximumBytes) throw new InvalidDataException($"Files over {MaximumBytes:N0} bytes cannot be compared.");
            return bytes.ToArray();
        }
        finally
        {
            using var store = new LoreStore(handle);
            using var close = new LoreStorageCloseArgs { Handle = store };
            await Lore.StorageClose(globals, close).WaitAsync();
        }
    }

    private static bool TryDecode(byte[]? bytes, out string? text)
    {
        text = null;
        if (bytes is null) return true;
        if (bytes.Contains((byte)0)) return false;
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int CountLines(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(character => character == '\n') + 1;

    private sealed record FileVersion(byte[]? Bytes, bool Truncated);
}
