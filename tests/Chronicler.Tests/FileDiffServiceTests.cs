using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class FileDiffServiceTests
{
    [Fact]
    public async Task ReadsUtf8TextBeforeAndAfterARevision()
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
            var diff = await new LoreFileDiffService().ReadAsync(root, revision.Revision, change);

            Assert.True(diff.IsText, diff.Notice);
            Assert.Equal("first\nsame\n", diff.Before);
            Assert.Equal("second\nsame\n", diff.After);
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
