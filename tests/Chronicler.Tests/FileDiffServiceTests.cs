using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Tests;

public sealed class FileDiffServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsUtf8TextBeforeAndAfterARevision(bool configureRemote)
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"diff-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(file, "first\nsame\n");
            using (var stage = new LoreFileStageArgs { Paths = [file] })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            await File.WriteAllTextAsync(file, "second\nsame\n");
            using (var stage = new LoreFileStageArgs { Paths = [file] })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "updated" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();

            var history = await new LoreRevisionHistoryService(new NoUserNames()).ReadAsync(root);
            var revision = history[0];
            var change = Assert.Single(await new LoreRevisionChangesService().ReadAsync(root, revision.Revision),
                change => change.IsFile && change.Path.EndsWith("note.txt"));
            if (configureRemote)
                await File.WriteAllTextAsync(Path.Combine(root, ".lore", "config.toml"),
                    "remote_url = \"lore://127.0.0.1:1\"\nidentity = \"chronicler-diff-test\"\n");
            var diff = await new LoreFileDiffService().ReadAsync(root, revision.Revision, change);

            Assert.True(diff.IsText, diff.Notice);
            Assert.Equal("first\nsame\n", diff.Before);
            Assert.Equal("second\nsame\n", diff.After);
        }
        finally
        {
            await DeleteRepositoryAsync(root);
        }
    }

    [Fact]
    public async Task MissingStoredContentUsesConfiguredRemoteAndPropagatesFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"diff-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(file, "content only available on the server\n");
            using (var stage = new LoreFileStageArgs { Paths = [file] })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();

            var history = await new LoreRevisionHistoryService(new NoUserNames()).ReadAsync(root);
            var revision = history[0].Revision;
            await RemoveStoredContentAsync(globals, root, file, revision);
            // An unsupported transport fails immediately without requiring a live server or credentials.
            const string remoteUrl = "diff-test-unsupported://127.0.0.1";
            await File.WriteAllTextAsync(Path.Combine(root, ".lore", "config.toml"),
                $"remote_url = \"{remoteUrl}\"\nidentity = \"chronicler-diff-test\"\n");

            var error = await Assert.ThrowsAsync<LoreError>(() =>
                new LoreFileDiffService().ReadAsync(root, revision,
                    new RevisionChange("note.txt", ChangeAction.Added, true, null)));
            Assert.Contains(remoteUrl, error.Message);
            Assert.Contains("protocol diff-test-unsupported was not recognized", error.Message);
            Assert.DoesNotContain("Address not found", error.Message);
        }
        finally
        {
            await DeleteRepositoryAsync(root);
        }
    }

    private static async Task RemoveStoredContentAsync(LoreGlobalArgs globals, string root, string file, string revision)
    {
        LoreAddress? address = null;
        byte[]? repositoryId = null;
        using var history = new LoreFileHistoryArgs { Path = file, Revision = revision, Length = 1 };
        await Lore.FileHistory(globals, history).Callback((ev, _) =>
        {
            if (ev.Tag != LoreEventTag.FILE_HISTORY) return;
            var data = ev.GetData<LoreFileHistoryEventDataFFI>();
            address = data.Address.Clone();
            repositoryId = data.Repository.Data.ToArray();
        }).WaitAsync();
        Assert.NotNull(address);
        Assert.NotNull(repositoryId);
        using (address)
        {
            ulong handle = 0;
            using var open = new LoreStorageOpenArgs { RepositoryPath = root };
            await Lore.StorageOpen(globals, open).Callback((ev, _) =>
            {
                if (ev.Tag == LoreEventTag.STORAGE_OPENED)
                    handle = ev.GetData<LoreStorageOpenedEventDataFFI>().HandleId;
            }).WaitAsync();
            Assert.NotEqual(0UL, handle);
            using var store = new LoreStore(handle);
            try
            {
                using var item = new LoreStorageObliterateItem
                {
                    Id = 1,
                    Partition = new LorePartition(repositoryId),
                    Address = address.Value.Clone()
                };
                using var args = new LoreStorageObliterateArgs { Handle = store, Items = [item] };
                await Lore.StorageObliterate(globals, args).WaitAsync();
            }
            finally
            {
                using var close = new LoreStorageCloseArgs { Handle = store };
                await Lore.StorageClose(globals, close).WaitAsync();
            }
        }
    }

    private static async Task DeleteRepositoryAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task DoesNotTreatBinaryFilesAsText()
    {
        var diff = await new LoreFileDiffService().ReadAsync("invalid", "", new RevisionChange("folder", ChangeAction.Folder, false, null));
        Assert.False(diff.IsText);
        Assert.Contains("Folders", diff.Notice);
    }

    [Fact]
    public void FormatsTextDifferencesWithAddedRemovedAndSharedLines()
    {
        var text = TextDiffFormatter.Format(new FileDiff("first\nsame\n", "second\nsame\n", "before.txt", "after.txt", true, null));
        Assert.Equal("--- before.txt\n+++ after.txt\n-first\n+second\n same\n", text);
    }

    private sealed class NoUserNames : IUserNameService
    {
        public Task<UserNameResult> ResolveAsync(string path, IReadOnlyList<string> userIds) =>
            Task.FromResult(new UserNameResult(new Dictionary<string, string>(), null));
    }
}
