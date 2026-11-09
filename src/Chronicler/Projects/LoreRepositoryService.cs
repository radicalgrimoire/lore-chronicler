using System.Collections.Concurrent;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public interface IRepositoryService
{
    Task<IReadOnlyList<string>> ListAsync(string serverUrl);
    Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress);
}

public sealed class LoreRepositoryService : IRepositoryService
{
    public async Task<IReadOnlyList<string>> ListAsync(string serverUrl)
    {
        var names = new ConcurrentQueue<string>();
        using var globals = new LoreGlobalArgs { Remote = true };
        using var args = new LoreRepositoryListArgs { Url = serverUrl };
        await Lore.RepositoryList(globals, args)
            .Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.REPOSITORY_LIST_ENTRY)
                {
                    names.Enqueue(ev.GetData<LoreRepositoryListEntryEventDataFFI>().Name);
                }
            }).WaitAsync();
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
    {
        using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRepositoryCloneArgs { RepositoryUrl = ProjectValidator.RepositoryUrl(serverUrl, repositoryName) };
        await Lore.RepositoryClone(globals, args)
            .Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.REPOSITORY_CLONE_PROGRESS)
                {
                    progress.Report("Cloning. Please wait until completion.");
                }
            }).WaitAsync();
    }
}
