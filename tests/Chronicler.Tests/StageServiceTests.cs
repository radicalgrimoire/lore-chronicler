using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class StageServiceTests
{
    [Fact]
    public async Task FolderOperationsIncludeStagedAndUnstagedChildrenButNotSiblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"stage-folder-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var first = Path.Combine(folder, "first.txt");
            var second = Path.Combine(folder, "second.txt");
            var outside = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            await File.WriteAllTextAsync(outside, "outside");
            var service = new LoreStageService();
            var status = new LoreFileStatusService();
            await service.StageAsync(root, first);
            await service.StageAsync(root, outside);
            await service.StageAsync(root, folder);
            var staged = await status.ScanAsync(root);
            Assert.Contains(first, staged.StagedPaths);
            Assert.Contains(second, staged.StagedPaths);
            Assert.Contains(outside, staged.StagedPaths);
            await service.UnstageAsync(root, [folder]);
            var unstaged = await status.ScanAsync(root);
            Assert.DoesNotContain(first, unstaged.StagedPaths);
            Assert.DoesNotContain(second, unstaged.StagedPaths);
            Assert.Contains(outside, unstaged.StagedPaths);
            Assert.Equal("first", await File.ReadAllTextAsync(first));
            Assert.Equal("second", await File.ReadAllTextAsync(second));
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

            var beforeStage = await status.ScanAsync(root);
            Assert.Contains(beforeStage.UnstagedEntries, entry => entry.FullPath == deleted && entry.Action == ChangeAction.Deleted);
            Assert.Contains(beforeStage.UnstagedEntries, entry => entry.FullPath == modified && entry.Action == ChangeAction.Modified);
            Assert.Contains(beforeStage.UnstagedEntries, entry => entry.FullPath == added && entry.Action == ChangeAction.Added);
            await stage.StageAsync(root, deleted);
            var deletion = await status.ScanAsync(root);
            Assert.Contains(deletion.StagedEntries, entry => entry.FullPath == deleted && entry.Action == ChangeAction.Deleted);
            Assert.DoesNotContain(deletion.UnstagedEntries, entry => entry.FullPath == deleted);

            await stage.StageAsync(root, folder);
            var staged = await status.ScanAsync(root);
            Assert.Equal(new[] { added, deleted, modified }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), staged.StagedPaths);
            Assert.Contains(staged.StagedEntries, entry => entry.FullPath == added && entry.Action == ChangeAction.Added);
            Assert.Contains(staged.StagedEntries, entry => entry.FullPath == modified && entry.Action == ChangeAction.Modified);
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
            Assert.Contains(staged.UnstagedEntries, entry => entry.FullPath == deleted && entry.Action == ChangeAction.Deleted);
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
