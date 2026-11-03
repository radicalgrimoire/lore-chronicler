using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record RevisionChange(string Path, ChangeAction Action, bool IsFile, string? FromPath)
{
    public string ActionText => Action switch
    {
        ChangeAction.Added => "Added",
        ChangeAction.Deleted => "Deleted",
        ChangeAction.Modified => "Modified",
        ChangeAction.Moved => "Moved",
        ChangeAction.Copied => "Copied",
        _ => "Other change"
    };

    public string DisplayPath => (string.IsNullOrWhiteSpace(FromPath) ? Path : $"{FromPath} → {Path}")
        + (IsFile ? string.Empty : " (folder)");
}

public interface IRevisionChangesService
{
    Task<IReadOnlyList<RevisionChange>> ReadAsync(string path, string revision);
}

public sealed class LoreRevisionChangesService : IRevisionChangesService
{
    public async Task<IReadOnlyList<RevisionChange>> ReadAsync(string path, string revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
            throw new ArgumentException("A revision ID is required.", nameof(revision));

        var changes = new List<RevisionChange>();
        var sync = new object();
        string? error = null;
        var receivedInfo = false;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionInfoArgs { Revision = revision, Delta = true };
        await Lore.RevisionInfo(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.REVISION_INFO)
                {
                    var data = ev.GetData<LoreRevisionInfoEventDataFFI>();
                    if (string.Equals(Convert.ToHexString(data.Revision.Data), revision, StringComparison.OrdinalIgnoreCase))
                        receivedInfo = true;
                    else
                        error ??= "Lore returned a different revision.";
                }
                else if (ev.Tag == LoreEventTag.REVISION_INFO_DELTA)
                {
                    var data = ev.GetData<LoreRevisionInfoDeltaEventDataFFI>();
                    var action = data.Action switch
                    {
                        LoreFileAction.ADD => ChangeAction.Added,
                        LoreFileAction.DELETE => ChangeAction.Deleted,
                        LoreFileAction.KEEP => ChangeAction.Modified,
                        LoreFileAction.MOVE => ChangeAction.Moved,
                        LoreFileAction.COPY => ChangeAction.Copied,
                        _ => ChangeAction.Other
                    };
                    changes.Add(new RevisionChange(data.Path, action, data.FlagFile, data.FromPath));
                }
                else if (ev.Tag == LoreEventTag.ERROR)
                    error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
            }
        }).WaitAsync();

        if (error is not null) throw new InvalidDataException($"Could not retrieve revision changes: {error}");
        if (!receivedInfo) throw new InvalidDataException("Could not verify the selected revision.");
        return changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
