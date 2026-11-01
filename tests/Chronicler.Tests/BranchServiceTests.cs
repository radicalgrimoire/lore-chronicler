using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class BranchServiceTests
{
    [Fact]
    public async Task SdkListsAndSwitchesBranchesWithoutDiscardingUnrelatedEdits()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"branch-test-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = Path.Combine(path, "tracked.txt");
            var unrelated = Path.Combine(path, "other.txt");
            await File.WriteAllTextAsync(file, "main");
            await File.WriteAllTextAsync(unrelated, "original");
            using var stage = new LoreFileStageArgs { Paths = [file, unrelated] };
            using var commit = new LoreRevisionCommitArgs { Message = "initial" };
            await Lore.FileStage(globals, stage).WaitAsync();
            await Lore.RevisionCommit(globals, commit).WaitAsync();
            using var createBranch = new LoreBranchCreateArgs { Branch = "feature" };
            await Lore.BranchCreate(globals, createBranch).WaitAsync();
            var service = new LoreBranchService();
            var branches = await service.ListAsync(path);
            Assert.False(branches.RemoteAvailable);
            var main = Assert.Single(branches.Branches, branch => branch.Name == "main");
            var feature = Assert.Single(branches.Branches, branch => branch.Name == "feature");
            await service.SwitchAsync(path, feature);
            await File.WriteAllTextAsync(file, "feature");
            using var stageFile = new LoreFileStageArgs { Paths = [file] };
            using var featureCommit = new LoreRevisionCommitArgs { Message = "feature" };
            await Lore.FileStage(globals, stageFile).WaitAsync();
            await Lore.RevisionCommit(globals, featureCommit).WaitAsync();
            await service.SwitchAsync(path, main);
            Assert.Equal("main", await File.ReadAllTextAsync(file));
            await File.WriteAllTextAsync(unrelated, "local edits");
            await service.SwitchAsync(path, feature);
            Assert.Equal("feature", await File.ReadAllTextAsync(file));
            Assert.Equal("local edits", await File.ReadAllTextAsync(unrelated));
            Assert.Equal("feature", Assert.Single((await service.ListAsync(path)).Branches, b => b.IsCurrent).Name);
            await File.WriteAllTextAsync(file, "conflicting local edits");
            await Assert.ThrowsAsync<LoreError>(() => service.SwitchAsync(path, main));
            Assert.Equal("conflicting local edits", await File.ReadAllTextAsync(file));
            Assert.Equal("local edits", await File.ReadAllTextAsync(unrelated));
            Assert.Equal("feature", Assert.Single((await service.ListAsync(path)).Branches, b => b.IsCurrent).Name);
            await File.WriteAllTextAsync(file, "feature");
            using var stageOther = new LoreFileStageArgs { Paths = [unrelated] };
            await Lore.FileStage(globals, stageOther).WaitAsync();
            await Assert.ThrowsAsync<LoreError>(() => service.SwitchAsync(path, main));
            Assert.Equal("local edits", await File.ReadAllTextAsync(unrelated));
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
}
