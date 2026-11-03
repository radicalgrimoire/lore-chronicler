using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class FileStatusServiceTests
{
    [Fact]
    public void ActualDirectoryAndChangedDescendantsShareOneAggregatedDisplayEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "status-root");
        var snapshot = new FileStatusSnapshot(root,
            new Dictionary<string, FileChangeKind>
            {
                ["folder"] = FileChangeKind.Staged,
                ["folder\\child.txt"] = FileChangeKind.Untracked
            },
            stagedDirectories: ["folder"],
            actions: new Dictionary<string, ChangeAction> { ["folder"] = ChangeAction.Added });

        var folder = Assert.Single(snapshot.AllDisplayEntries, entry => entry.RelativePath == "folder");
        Assert.True(folder.IsDirectory && folder.IsStaged && folder.IsUnstaged);
        Assert.Equal(ChangeAction.Added, folder.Action);
        Assert.Equal("＋", folder.DirectoryActionIcon);
        Assert.Equal(1, snapshot.StagedCount);
        Assert.Single(snapshot.StagedEntries);
    }

    [Fact]
    public async Task ScanSeparatesOneStagedAddFromUnstagedDeletesAndAdds()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"status-mixed-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var deleted = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(folder, $"old-{index}.txt")).ToArray();
            foreach (var file in deleted) await File.WriteAllTextAsync(file, "tracked");
            using (var stage = new LoreFileStageArgs { Paths = [folder], Scan = true })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            foreach (var file in deleted) File.Delete(file);
            var added = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(folder, $"new-{index}.txt")).ToArray();
            foreach (var file in added) await File.WriteAllTextAsync(file, "new");
            await new LoreStageService().StageAsync(root, added[3]);

            var result = await new LoreFileStatusService().ScanAsync(root);
            Assert.Equal([added[3]], result.StagedPaths);
            Assert.Contains(result.StagedEntries, entry => entry.FullPath == added[3] && entry.Action == ChangeAction.Added);
            foreach (var file in deleted)
                Assert.Contains(result.UnstagedEntries, entry => entry.FullPath == file && entry.Action == ChangeAction.Deleted);
            foreach (var file in added.Take(3))
                Assert.Contains(result.UnstagedEntries, entry => entry.FullPath == file && entry.Action == ChangeAction.Added);
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
    public async Task ScanDistinguishesModifiedUntrackedAndStagedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"status-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var tracked = Path.Combine(path, "tracked.txt");
            var trackedStaged = Path.Combine(path, "tracked-staged.txt");
            var staged = Path.Combine(path, "staged.txt");
            var stagedClean = Path.Combine(path, "staged-clean.txt");
            await File.WriteAllTextAsync(tracked, "original");
            await File.WriteAllTextAsync(trackedStaged, "original");
            using (var stage = new LoreFileStageArgs { Paths = [tracked, trackedStaged] })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            await File.WriteAllTextAsync(tracked, "modified content");
            await File.WriteAllTextAsync(trackedStaged, "tracked modified");
            await File.WriteAllTextAsync(staged, "staged");
            await File.WriteAllTextAsync(stagedClean, "staged clean");
            using (var stage = new LoreFileStageArgs { Paths = [staged, stagedClean, trackedStaged] })
                await Lore.FileStage(globals, stage).WaitAsync();
            await File.WriteAllTextAsync(staged, "edited after staging");
            Directory.CreateDirectory(Path.Combine(path, "folder"));
            await File.WriteAllTextAsync(Path.Combine(path, "folder", "new.txt"), "new");
            await File.WriteAllTextAsync(Path.Combine(path, ".loreignore"), "ignored.txt\n");
            await File.WriteAllTextAsync(Path.Combine(path, "ignored.txt"), "ignored");

            var changes = await new LoreFileStatusService().ScanAsync(path);
            Assert.Equal(FileChangeKind.Modified, changes.GetFileStatus(tracked));
            Assert.Equal(FileChangeKind.Staged, changes.GetFileStatus(staged));
            Assert.Equal(FileChangeKind.Staged, changes.GetFileStatus(stagedClean));
            Assert.Equal(FileChangeKind.Staged | FileChangeKind.Modified, changes.GetFileStatus(trackedStaged));
            Assert.Equal(FileChangeKind.Untracked, changes.GetFileStatus(Path.Combine(path, "folder", "new.txt")));
            Assert.Equal(FileChangeKind.None, changes.GetFileStatus(Path.Combine(path, "ignored.txt")));
            Assert.Contains(changes.UnstagedEntries, entry => entry.FullPath == Path.Combine(path, "folder")
                && entry.IsDirectory && entry.Action == ChangeAction.Added);
            await File.WriteAllTextAsync(tracked, "original");
            File.Delete(Path.Combine(path, "folder", "new.txt"));
            var refreshed = await new LoreFileStatusService().ScanAsync(path);
            Assert.Equal(FileChangeKind.None, refreshed.GetFileStatus(tracked));
            Assert.Equal(FileChangeKind.None, refreshed.GetFileStatus(Path.Combine(path, "folder", "new.txt")));
            Assert.Equal(FileChangeKind.Staged, refreshed.GetFileStatus(staged));
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
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
    public void SnapshotRejectsPathsOutsideRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => new FileStatusSnapshot(root,
            new Dictionary<string, FileChangeKind> { ["..\\outside.txt"] = FileChangeKind.Untracked }));
        Assert.Throws<InvalidDataException>(() => new FileStatusSnapshot(root,
            new Dictionary<string, FileChangeKind> { [Path.GetFullPath(root)] = FileChangeKind.Modified }));
    }

    [Fact]
    public void StagedEntriesDistinguishDirectoriesAndExcludeUnresolvedConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        var snapshot = new FileStatusSnapshot(root, new Dictionary<string, FileChangeKind>
        {
            ["folder"] = FileChangeKind.Staged,
            ["folder\\file.txt"] = FileChangeKind.Staged | FileChangeKind.Modified,
            ["conflict.txt"] = FileChangeKind.Staged | FileChangeKind.Conflict
        }, ["folder"]);
        Assert.Equal(1, snapshot.UnresolvedConflictCount);
        Assert.Collection(snapshot.StagedEntries,
            folder =>
            {
                Assert.True(folder.IsDirectory);
                Assert.Equal("folder (folder)", folder.DisplayName);
            },
            file =>
            {
                Assert.False(file.IsDirectory);
                Assert.Equal("folder\\file.txt", file.DisplayName);
            });
    }
}
