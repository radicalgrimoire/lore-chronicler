using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;
using System.IO;

namespace Chronicler.Projects;

public sealed record LocalRepositoryInfo(string RemoteUrl, string RepositoryId);
public sealed record RemoteRepositoryInfo(string RemoteUrl, string RepositoryId, string Name);

public interface IProjectRepositoryInfo
{
    Task<LocalRepositoryInfo> ReadLocalAsync(string path);
    Task<RemoteRepositoryInfo> ReadRemoteAsync(string path);
}

public sealed class ProjectRepositoryInfo : IProjectRepositoryInfo
{
    public async Task<LocalRepositoryInfo> ReadLocalAsync(string path)
    {
        var attributes = File.GetAttributes(Path.Combine(path, ".lore"));
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException("選択したフォルダーにLoreリポジトリがありません。");
        }
        string? remote = null;
        string? id = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
        using var config = new LoreRepositoryConfigGetArgs { Key = "remote_url" };
        await Lore.RepositoryConfigGet(globals, config).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.REPOSITORY_CONFIG_GET)
            {
                remote = ev.GetData<LoreRepositoryConfigGetEventDataFFI>().Value;
            }
        }).WaitAsync();
        using var status = new LoreRepositoryStatusArgs { RevisionOnly = true };
        await Lore.RepositoryStatus(globals, status).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
            {
                id = Convert.ToHexString(ev.GetData<LoreRepositoryStatusRevisionEventDataFFI>().Repository.Data);
            }
        }).WaitAsync();
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrEmpty(id) || id.All(c => c == '0'))
        {
            throw new InvalidOperationException("Loreの接続先またはリポジトリIDを確認できません。");
        }
        return new LocalRepositoryInfo(remote, id);
    }

    public async Task<RemoteRepositoryInfo> ReadRemoteAsync(string path)
    {
        RemoteRepositoryInfo? result = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRepositoryInfoArgs();
        await Lore.RepositoryInfo(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.REPOSITORY_DATA)
            {
                var data = ev.GetData<LoreRepositoryDataEventDataFFI>();
                result = new RemoteRepositoryInfo(data.RemoteUrl, Convert.ToHexString(data.Id.Data), data.Name);
            }
        }).WaitAsync();
        return result ?? throw new InvalidOperationException("サーバー上のリポジトリ情報を確認できません。");
    }

    public static bool SameServer(string a, string b)
    {
        return Uri.TryCreate(a, UriKind.Absolute, out var first)
            && Uri.TryCreate(b, UriKind.Absolute, out var second)
            && string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
            && first.Port == second.Port
            && first.AbsolutePath.TrimEnd('/') == second.AbsolutePath.TrimEnd('/')
            && first.UserInfo == second.UserInfo && first.Query == second.Query && first.Fragment == second.Fragment;
    }

    public static void Validate(LocalRepositoryInfo local, string serverUrl, string? repositoryId = null)
    {
        if (!SameServer(local.RemoteUrl, serverUrl))
        {
            throw new InvalidOperationException("接続先がプロジェクトのサーバーと一致しません。接続先が変わっている可能性があります。");
        }
        if (repositoryId is not null && !string.Equals(local.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("リポジトリIDがプロジェクトの登録情報と一致しません。");
        }
    }

    public static void ValidateRemote(LocalRepositoryInfo local, RemoteRepositoryInfo remote)
    {
        if (!SameServer(local.RemoteUrl, remote.RemoteUrl)
            || !string.Equals(local.RepositoryId, remote.RepositoryId, StringComparison.OrdinalIgnoreCase)
            || !ProjectValidator.IsValidRepositoryName(remote.Name))
        {
            throw new InvalidOperationException("サーバー上のリポジトリ情報がローカルと一致しません。");
        }
    }
}
