using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public interface ICommitService
{
    Task CommitAsync(string path, string message);
}

public sealed class LoreCommitService : ICommitService
{
    public Task CommitAsync(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Enter a commit message.", nameof(message));
        return Task.Run(async () =>
        {
            using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
            string? error = null;
            var revisionCreated = false;
            var sync = new object();
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var args = new LoreRevisionCommitArgs { Message = message };
            await Lore.RevisionCommit(globals, args).Callback((ev, _) =>
            {
                lock (sync)
                {
                    if (ev.Tag == LoreEventTag.ERROR) error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                    if (ev.Tag == LoreEventTag.REVISION_COMMIT_REVISION) revisionCreated = true;
                }
            }).WaitAsync();
            if (error is not null) throw new InvalidDataException($"Could not complete commit: {error}");
            if (!revisionCreated) throw new InvalidDataException("Could not verify the committed revision.");
        });
    }
}
