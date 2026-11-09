using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record UnpushedCommitCount(int Count, bool IsExact);

public interface IUnpushedCommitService
{
    Task<UnpushedCommitCount> ReadAsync(string path, LatestStatus status);
}

public sealed class LoreUnpushedCommitService : IUnpushedCommitService
{
    public const uint MaximumEntries = 100;

    public async Task<UnpushedCommitCount> ReadAsync(string path, LatestStatus status)
    {
        if (!status.LocalAhead) return new UnpushedCommitCount(0, true);

        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        var revisions = new List<string>();
        string? error = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionHistoryArgs { Length = MaximumEntries };
        await Lore.RevisionHistory(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.ERROR)
                error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
            else if (ev.Tag == LoreEventTag.REVISION_HISTORY_ENTRY)
                revisions.Add(Convert.ToHexString(ev.GetData<LoreRevisionHistoryEntryEventDataFFI>().Revision.Data));
        }).WaitAsync();

        if (error is not null) throw new InvalidDataException($"Could not read local history: {error}");

        if (!status.RemoteBranchExists)
            return new UnpushedCommitCount(revisions.Count, revisions.Count < MaximumEntries);

        var remoteIndex = revisions.FindIndex(revision =>
            string.Equals(revision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase));
        return remoteIndex >= 0
            ? new UnpushedCommitCount(remoteIndex, true)
            : new UnpushedCommitCount(revisions.Count, false);
    }
}
