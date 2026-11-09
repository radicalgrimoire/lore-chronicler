using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record LatestStatus(
    string Branch, string LocalRevision, string RemoteRevision,
    bool RemoteAvailable, bool RemoteAuthorized, bool RemoteBranchExists,
    bool LocalAhead, bool RemoteAhead);

public sealed record LatestSyncOutcome(
    string Branch, string Revision, bool RemoteAvailable, bool RemoteAuthorized, bool Conflict);

public interface ILatestBackend
{
    Task<LatestStatus> ReadStatusAsync(string path);
    Task<LatestSyncOutcome> SyncAsync(string path, string revision);
}

public interface ILatestService
{
    Task EnsureRemoteAuthorizedAsync(string path);
    Task<LatestStatus> GetLatestAsync(string path, IProgress<string> progress);
}

public sealed class LatestSyncException(string message) : Exception(message);

public sealed class LoreLatestService(ILatestBackend? backend = null) : ILatestService
{
    private readonly ILatestBackend backend = backend ?? new LoreLatestBackend();

    public async Task EnsureRemoteAuthorizedAsync(string path)
    {
        var status = await backend.ReadStatusAsync(path);
        ValidateRemoteAccess(status);
    }

    public async Task<LatestStatus> GetLatestAsync(string path, IProgress<string> progress)
    {
        var status = await backend.ReadStatusAsync(path);
        ValidateRemoteAccess(status);
        if (!status.RemoteBranchExists)
        {
            throw new LatestSyncException("The current branch does not exist on the server.");
        }
        if (status.LocalAhead)
        {
            throw new LatestSyncException(status.RemoteAhead
                ? "Local and server histories have diverged. The update was cancelled; no automatic merge was performed."
                : "Local history is ahead of the server. The update was cancelled.");
        }
        if (string.IsNullOrWhiteSpace(status.Branch) || status.Branch.Contains('@')
            || string.IsNullOrEmpty(status.RemoteRevision) || status.RemoteRevision.All(c => c == '0'))
        {
            throw new LatestSyncException("Could not identify the server revision to update to.");
        }
        if (!string.Equals(status.LocalRevision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase)
            && !status.RemoteAhead)
        {
            throw new LatestSyncException("Could not determine the relationship between local and server histories. The update was cancelled.");
        }

        progress.Report("Updating to the revision verified on the server…");
        var result = await backend.SyncAsync(path, $"{status.Branch}@{status.RemoteRevision}");
        if (!result.RemoteAvailable || !result.RemoteAuthorized)
        {
            throw new LatestSyncException("Could not verify the server revision during the update. Some files may have changed.");
        }
        if (!string.Equals(result.Branch, status.Branch, StringComparison.Ordinal)
            || !string.Equals(result.Revision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new LatestSyncException("The update target differs from the verified revision. Some files may have changed.");
        }
        if (result.Conflict)
        {
            throw new LatestSyncException("A conflict occurred during the update. Check local files.");
        }
        return status;
    }

    private static void ValidateRemoteAccess(LatestStatus status)
    {
        if (!status.RemoteAvailable)
            throw new LatestSyncException("Could not verify the server revision. Check the connection, sign-in, and permissions.");
        if (!status.RemoteAuthorized)
            throw new RemoteAuthenticationRequiredException("Sign-in to the server is required.");
    }
}

public sealed class LoreLatestBackend : ILatestBackend
{
    public async Task<LatestStatus> ReadStatusAsync(string path)
    {
        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        LatestStatus? status = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRepositoryStatusArgs { RevisionOnly = true };
        await Lore.RepositoryStatus(globals, args)
            .Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
                {
                    var data = ev.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                    status = new LatestStatus(data.BranchName, Convert.ToHexString(data.RevisionLocal.Data),
                        Convert.ToHexString(data.RevisionRemote.Data), data.RemoteAvailable,
                        data.RemoteAuthorized, data.RemoteBranchExist, data.IsLocalAhead, data.IsRemoteAhead);
                }
            }).WaitAsync();
        return status ?? throw new LatestSyncException("Could not retrieve repository status.");
    }

    public async Task<LatestSyncOutcome> SyncAsync(string path, string revision)
    {
        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        LatestSyncOutcome? target = null;
        var conflict = false;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionSyncArgs { Revision = revision, Reset = false, ForwardChanges = false };
        await Lore.RevisionSync(globals, args)
            .Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.REVISION_SYNC_TARGET)
                {
                    var data = ev.GetData<LoreRevisionSyncTargetEventDataFFI>();
                    target = new LatestSyncOutcome(data.BranchName, Convert.ToHexString(data.TargetRevision.Data),
                        data.RemoteAvailable, data.RemoteAuthorized, false);
                }
                else if (ev.Tag == LoreEventTag.REVISION_SYNC_REVISION)
                {
                    conflict = ev.GetData<LoreRevisionSyncRevisionEventDataFFI>().FlagConflict;
                }
            }).WaitAsync();
        return target is null
            ? throw new LatestSyncException("Could not verify the update result. Some files may have changed.")
            : target with { Conflict = conflict };
    }
}
