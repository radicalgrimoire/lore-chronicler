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
    Task<string> ReadServerUrlAsync(string path);
    Task<LocalRepositoryInfo> ReadLocalAsync(string path);
    Task<RemoteRepositoryInfo> ReadRemoteAsync(string path);
}

public sealed class ProjectRepositoryInfo : IProjectRepositoryInfo
{
    public async Task<string> ReadServerUrlAsync(string path)
    {
        var attributes = File.GetAttributes(Path.Combine(path, ".lore"));
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException("The selected folder does not contain a Lore repository.");
        }
        string? remote = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
        using var config = new LoreRepositoryConfigGetArgs { Key = "remote_url" };
        await Lore.RepositoryConfigGet(globals, config).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.REPOSITORY_CONFIG_GET)
            {
                remote = ev.GetData<LoreRepositoryConfigGetEventDataFFI>().Value;
            }
        }).WaitAsync();
        if (string.IsNullOrWhiteSpace(remote))
        {
            throw new InvalidOperationException("Could not verify the Lore server URL.");
        }
        return remote;
    }

    public async Task<LocalRepositoryInfo> ReadLocalAsync(string path)
    {
        var remote = await ReadServerUrlAsync(path);
        string? id = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
        using var status = new LoreRepositoryStatusArgs { RevisionOnly = true };
        await Lore.RepositoryStatus(globals, status).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
            {
                id = Convert.ToHexString(ev.GetData<LoreRepositoryStatusRevisionEventDataFFI>().Repository.Data);
            }
        }).WaitAsync();
        if (string.IsNullOrEmpty(id) || id.All(c => c == '0'))
        {
            throw new InvalidOperationException("Could not verify the Lore server URL or repository ID.");
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
        return result ?? throw new InvalidOperationException("Could not verify the repository on the server.");
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
            throw new InvalidOperationException("The server URL does not match the project. The endpoint may have changed.");
        }
        if (repositoryId is not null && !string.Equals(local.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The repository ID does not match the project registration.");
        }
    }

    public static void ValidateRemote(LocalRepositoryInfo local, RemoteRepositoryInfo remote)
    {
        if (!SameServer(local.RemoteUrl, remote.RemoteUrl)
            || !string.Equals(local.RepositoryId, remote.RepositoryId, StringComparison.OrdinalIgnoreCase)
            || !ProjectValidator.IsValidRepositoryName(remote.Name))
        {
            throw new InvalidOperationException("The server repository information does not match the local repository.");
        }
    }
}
