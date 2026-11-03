using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class RevisionChangesServiceTests
{
    [Fact]
    public async Task ReadsFirstRevisionAndChangesAgainstDirectParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"changes-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var modified = Path.Combine(folder, "modified.txt");
            var deleted = Path.Combine(folder, "deleted.txt");
            await File.WriteAllTextAsync(modified, "original");
            await File.WriteAllTextAsync(deleted, "original");
            using (var stage = new LoreFileStageArgs { Paths = [folder], Scan = true })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();

            var history = new LoreRevisionHistoryService(new NoUserNames());
            var firstRevision = Assert.Single(await history.ReadAsync(root)).Revision;
            var service = new LoreRevisionChangesService();
            var first = await service.ReadAsync(root, firstRevision);
            Assert.Contains(first, entry => entry.IsFile && entry.Path.EndsWith("modified.txt") && entry.Action == ChangeAction.Added);
            Assert.Contains(first, entry => entry.IsFile && entry.Path.EndsWith("deleted.txt") && entry.Action == ChangeAction.Added);

            await File.WriteAllTextAsync(modified, "changed");
            File.Delete(deleted);
            var added = Path.Combine(folder, "added.txt");
            await File.WriteAllTextAsync(added, "new");
            using (var stage = new LoreFileStageArgs { Paths = [folder], Scan = true })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "next" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            var secondRevision = (await history.ReadAsync(root))[0].Revision;
            var second = await service.ReadAsync(root, secondRevision);
            Assert.Contains(second, entry => entry.IsFile && entry.Path.EndsWith("modified.txt") && entry.Action == ChangeAction.Modified);
            Assert.Contains(second, entry => entry.IsFile && entry.Path.EndsWith("deleted.txt") && entry.Action == ChangeAction.Deleted);
            Assert.Contains(second, entry => entry.IsFile && entry.Path.EndsWith("added.txt") && entry.Action == ChangeAction.Added);
            Assert.Equal(3, second.Count(entry => entry.IsFile));
            var failure = await Record.ExceptionAsync(() => service.ReadAsync(root, new string('F', 64)));
            Assert.True(failure is LoreError or InvalidDataException);
        }

        finally
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
    }

    [Fact]
    public void MovedPathsAndFoldersAreClearlyDisplayed()
    {
        var moved = new RevisionChange("new/file.txt", ChangeAction.Moved, true, "old/file.txt");
        var folder = new RevisionChange("folder", ChangeAction.Deleted, false, null);
        Assert.Equal("Moved", moved.ActionText);
        Assert.Equal("old/file.txt → new/file.txt", moved.DisplayPath);
        Assert.Equal("folder (folder)", folder.DisplayPath);
    }

    private sealed class NoUserNames : IUserNameService
    {
        public Task<UserNameResult> ResolveAsync(string path, IReadOnlyList<string> userIds) =>
            Task.FromResult(new UserNameResult(new Dictionary<string, string>(), null));
    }
}
