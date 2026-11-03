using System.Collections.Concurrent;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record BranchEntry(string Id, string Name, bool IsLocal, bool IsRemote, bool IsCurrent);

public sealed record BranchListResult(IReadOnlyList<BranchEntry> Branches, bool RemoteAvailable);

public interface IBranchService
{
    Task<BranchListResult> ListAsync(string path);
    Task SwitchAsync(string path, BranchEntry branch);
}

public sealed class LoreBranchService : IBranchService
{
    public async Task<BranchListResult> ListAsync(string path)
    {
        var entries = new ConcurrentQueue<BranchEntry>();
        var remoteSeen = 0;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreBranchListArgs { Archived = false };
        await Lore.BranchList(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.BRANCH_LIST_END)
            {
                var end = ev.GetData<LoreBranchListEndEventDataFFI>();
                if (end.Location == LoreBranchLocation.REMOTE) Interlocked.Exchange(ref remoteSeen, 1);
            }
            else if (ev.Tag == LoreEventTag.BRANCH_LIST_ENTRY)
            {
                var data = ev.GetData<LoreBranchListEntryEventDataFFI>();
                entries.Enqueue(new BranchEntry(Convert.ToHexString(data.Id.Data), data.Name,
                    data.Location == LoreBranchLocation.LOCAL, data.Location == LoreBranchLocation.REMOTE, data.IsCurrent));
            }
        }).WaitAsync();
        var branches = entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new BranchEntry(group.Key, group.First().Name, group.Any(entry => entry.IsLocal),
                group.Any(entry => entry.IsRemote), group.Any(entry => entry.IsCurrent)))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (branches.Any(entry => string.IsNullOrWhiteSpace(entry.Name) || entry.Id.All(c => c == '0')))
            throw new InvalidOperationException("Could not verify branch information.");
        return new BranchListResult(branches, remoteSeen != 0);
    }

    public async Task SwitchAsync(string path, BranchEntry branch)
    {
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Local = branch.IsLocal };
        using var args = new LoreBranchSwitchArgs { Branch = branch.Id, Reset = false, Bare = false };
        var completed = false;
        var conflict = false;
        await Lore.BranchSwitch(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.BRANCH_SWITCH_END)
            {
                var data = ev.GetData<LoreBranchSwitchEndEventDataFFI>();
                completed = string.Equals(Convert.ToHexString(data.Branch.Id.Data), branch.Id, StringComparison.OrdinalIgnoreCase);
            }
            else if (ev.Tag == LoreEventTag.REVISION_SYNC_REVISION)
            {
                conflict |= ev.GetData<LoreRevisionSyncRevisionEventDataFFI>().FlagConflict;
            }
        }).WaitAsync();
        if (!completed) throw new InvalidOperationException("Could not verify the branch ID after switching.");
        if (conflict) throw new InvalidOperationException("A conflict occurred while switching. Check the local state.");
    }
}
