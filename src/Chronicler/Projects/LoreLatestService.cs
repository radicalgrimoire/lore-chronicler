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
            throw new LatestSyncException("現在のブランチがサーバーに存在しません。");
        }
        if (status.LocalAhead)
        {
            throw new LatestSyncException(status.RemoteAhead
                ? "ローカルとサーバーの履歴が分岐しているため、取得を中止しました。自動マージは行いません。"
                : "ローカルの履歴がサーバーより進んでいるため、取得を中止しました。");
        }
        if (string.IsNullOrWhiteSpace(status.Branch) || status.Branch.Contains('@')
            || string.IsNullOrEmpty(status.RemoteRevision) || status.RemoteRevision.All(c => c == '0'))
        {
            throw new LatestSyncException("サーバーの更新対象を特定できません。");
        }
        if (!string.Equals(status.LocalRevision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase)
            && !status.RemoteAhead)
        {
            throw new LatestSyncException("ローカルとサーバーの履歴の関係を確認できないため、取得を中止しました。");
        }

        progress.Report("サーバーで確認したリビジョンに更新しています…");
        var result = await backend.SyncAsync(path, $"{status.Branch}@{status.RemoteRevision}");
        if (!result.RemoteAvailable || !result.RemoteAuthorized)
        {
            throw new LatestSyncException("更新中にサーバーのリビジョンを確認できませんでした。ファイルが一部変更された可能性があります。");
        }
        if (!string.Equals(result.Branch, status.Branch, StringComparison.Ordinal)
            || !string.Equals(result.Revision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new LatestSyncException("確認したリビジョンと更新対象が異なります。ファイルが変更された可能性があります。");
        }
        if (result.Conflict)
        {
            throw new LatestSyncException("更新中に競合が発生しました。ローカルの状態を確認してください。");
        }
        return status;
    }

    private static void ValidateRemoteAccess(LatestStatus status)
    {
        if (!status.RemoteAvailable)
            throw new LatestSyncException("サーバーのリビジョンを確認できません。接続、ログイン状態、アクセス権を確認してください。");
        if (!status.RemoteAuthorized)
            throw new RemoteAuthenticationRequiredException("サーバーへのログインが必要です。");
    }
}

public sealed class LoreLatestBackend : ILatestBackend
{
    public async Task<LatestStatus> ReadStatusAsync(string path)
    {
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
        return status ?? throw new LatestSyncException("リポジトリの状態を取得できませんでした。");
    }

    public async Task<LatestSyncOutcome> SyncAsync(string path, string revision)
    {
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
            ? throw new LatestSyncException("更新結果を確認できませんでした。ファイルが一部変更された可能性があります。")
            : target with { Conflict = conflict };
    }
}
