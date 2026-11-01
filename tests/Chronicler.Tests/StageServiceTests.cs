using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class StageServiceTests
{
    [Fact]
    public async Task StageAndUnstagePreserveEditsAndReportChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"stage-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var modified = Path.Combine(folder, "modified.txt");
            var deleted = Path.Combine(folder, "deleted.txt");
            await File.WriteAllTextAsync(modified, "original");
            await File.WriteAllTextAsync(deleted, "original");
            using (var args = new LoreFileStageArgs { Paths = [folder], Scan = true })
                await Lore.FileStage(globals, args).WaitAsync();
            using (var args = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, args).WaitAsync();
            var stage = new LoreStageService();
            var status = new LoreFileStatusService();
            await stage.StageAsync(root, modified);
            Assert.Empty((await status.ScanAsync(root)).StagedPaths);
            await File.WriteAllTextAsync(modified, "edited");
            File.Delete(deleted);
            var added = Path.Combine(folder, "added.txt");
            await File.WriteAllTextAsync(added, "new");
            var other = Path.Combine(root, "other.txt");
            await File.WriteAllTextAsync(other, "other");

            await stage.StageAsync(root, folder);
            var staged = await status.ScanAsync(root);
            Assert.Equal(new[] { added, deleted, modified }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), staged.StagedPaths);
            Assert.DoesNotContain(other, staged.StagedPaths);
            await File.WriteAllTextAsync(modified, "edited again");
            staged = await status.ScanAsync(root);
            Assert.Equal(FileChangeKind.Staged | FileChangeKind.Modified, staged.GetFileStatus(modified));
            await stage.UnstageAsync(root, [modified, added]);
            Assert.Equal("edited again", await File.ReadAllTextAsync(modified));
            Assert.Equal("new", await File.ReadAllTextAsync(added));
            Assert.False(File.Exists(deleted));
            staged = await status.ScanAsync(root);
            Assert.Equal([deleted], staged.StagedPaths);
            await stage.StageAsync(root, other);
            staged = await status.ScanAsync(root);
            Assert.Contains(other, staged.StagedPaths);
            await stage.UnstageAsync(root, [deleted]);
            Assert.False(File.Exists(deleted));
            staged = await status.ScanAsync(root);
            Assert.DoesNotContain(deleted, staged.StagedPaths);
            var nested = Path.Combine(root, "new-folder");
            Directory.CreateDirectory(nested);
            var nestedFile = Path.Combine(nested, "nested.txt");
            await File.WriteAllTextAsync(nestedFile, "nested");
            await stage.StageAsync(root, nested);
            staged = await status.ScanAsync(root);
            Assert.Contains(nestedFile, staged.StagedPaths);
            Assert.Contains(staged.StagedEntries, entry => entry.FullPath == nested && entry.IsDirectory);
            await stage.UnstageAsync(root, [nested, nestedFile]);
            Assert.Equal("nested", await File.ReadAllTextAsync(nestedFile));
            staged = await status.ScanAsync(root);
            Assert.DoesNotContain(nestedFile, staged.StagedPaths);
            await Assert.ThrowsAsync<InvalidDataException>(() => stage.StageAsync(root, Path.Combine(root, "..", "outside")));
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
