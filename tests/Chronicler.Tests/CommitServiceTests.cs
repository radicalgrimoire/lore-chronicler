using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class CommitServiceTests
{
    [Fact]
    public async Task CommitsOnlyStagedFilesAndRecordsMultilineMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"commit-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var stagedFile = Path.Combine(root, "staged.txt");
            var other = Path.Combine(root, "other.txt");
            await File.WriteAllTextAsync(stagedFile, "initial");
            await File.WriteAllTextAsync(other, "original");
            using (var stage = new LoreFileStageArgs { Paths = [stagedFile, other] })
                await Lore.FileStage(globals, stage).WaitAsync();
            var commits = new LoreCommitService();
            await Assert.ThrowsAsync<ArgumentException>(async () => await commits.CommitAsync(root, " \n "));
            await commits.CommitAsync(root, "initial");
            var backend = new LoreLatestBackend();
            var first = await backend.ReadStatusAsync(root);
            await File.WriteAllTextAsync(stagedFile, "staged content");
            using (var stage = new LoreFileStageArgs { Paths = [stagedFile] })
                await Lore.FileStage(globals, stage).WaitAsync();
            await File.WriteAllTextAsync(stagedFile, "edited after staging");
            await File.WriteAllTextAsync(other, "not staged");
            await commits.CommitAsync(root, "title\n\nbody");
            var second = await backend.ReadStatusAsync(root);
            var history = await new LoreRevisionHistoryService().ReadAsync(root);
            Assert.Equal("title\n\nbody", history[0].Message);
            Assert.NotEqual(first.LocalRevision, second.LocalRevision);
            Assert.Equal("not staged", await File.ReadAllTextAsync(other));
            await Assert.ThrowsAsync<LoreError>(() => commits.CommitAsync(root, "nothing"));

            var status = await new LoreFileStatusService().ScanAsync(root);
            Assert.Equal(FileChangeKind.None, status.GetFileStatus(stagedFile));
            Assert.Equal(FileChangeKind.Modified, status.GetFileStatus(other));
            await backend.SyncAsync(root, $"{first.Branch}@{first.LocalRevision}");
            Assert.Equal("initial", await File.ReadAllTextAsync(stagedFile));
            await backend.SyncAsync(root, $"{second.Branch}@{second.LocalRevision}");
            Assert.Equal("edited after staging", await File.ReadAllTextAsync(stagedFile));
            Assert.Equal("not staged", await File.ReadAllTextAsync(other));
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
}
