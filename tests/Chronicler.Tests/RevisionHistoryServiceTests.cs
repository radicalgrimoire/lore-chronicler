using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class RevisionHistoryServiceTests
{
    [Fact]
    public async Task ResolvesDistinctUserAuthorsAndPreservesOtherMetadata()
    {
        var users = new FakeUserNames(new UserNameResult(new Dictionary<string, string>
        {
            ["User:abc"] = "Alice",
            ["User:def"] = "User:def"
        }, "directory unavailable"));
        var resolver = new LoreRevisionHistoryService.RevisionHistoryAuthorResolver(users);
        RevisionHistoryEntry[] entries =
        [
            new(3, "r3", "third", "User:abc", null),
            new(2, "r2", "second", "User:abc", null),
            new(1, "r1", "first", "User:def", null),
            new(0, "r0", "other", "Local author", null),
            new(0, "r0", "missing", null, null),
            new(0, "r0", "empty id", "User:", null)
        ];

        var result = await resolver.ResolveAsync("repo", entries);

        Assert.Equal("repo", users.Path);
        Assert.Equal(["User:abc", "User:def", "Local author", "User:"], users.Ids);
        Assert.Equal("Alice (User:abc)", result[0].DisplayAuthor);
        Assert.Equal("Alice", result[0].DisplayAuthorName);
        Assert.Equal("Alice (User:abc)", result[1].DisplayAuthor);
        Assert.Equal("directory unavailable", result[0].AuthorResolutionWarning);
        Assert.Equal("User:def", result[2].DisplayAuthor);
        Assert.Equal("directory unavailable", result[2].AuthorResolutionWarning);
        Assert.Equal("Local author", result[3].DisplayAuthor);
        Assert.Equal("Unknown author", result[4].DisplayAuthor);
        Assert.Equal("User:", result[5].DisplayAuthor);
        Assert.Equal(entries.Select(entry => entry.Revision), result.Select(entry => entry.Revision));
    }

    [Fact]
    public async Task UnresolvedUsersRetainIdsAndReportWarning()
    {
        var users = new FakeUserNames(new UserNameResult(new Dictionary<string, string>(), null));
        var result = await new LoreRevisionHistoryService.RevisionHistoryAuthorResolver(users).ResolveAsync("repo",
            [new RevisionHistoryEntry(1, "rev", "message", "User:abc", null)]);

        Assert.Equal("User:abc", result[0].DisplayAuthor);
        Assert.Equal("Some authors could not be resolved.", result[0].AuthorResolutionWarning);
    }

    [Fact]
    public async Task DoesNotContactUserServiceWhenThereAreNoUserIds()
    {
        var users = new FakeUserNames(new UserNameResult(new Dictionary<string, string>(), null));
        var entries = new[] { new RevisionHistoryEntry(1, "rev", "message", null, null) };

        var result = await new LoreRevisionHistoryService.RevisionHistoryAuthorResolver(users).ResolveAsync("repo", entries);

        Assert.Same(entries, result);
        Assert.Null(users.Path);
    }

    private sealed class FakeUserNames(UserNameResult result) : IUserNameService
    {
        public string? Path { get; private set; }
        public IReadOnlyList<string> Ids { get; private set; } = [];

        public Task<UserNameResult> ResolveAsync(string path, IReadOnlyList<string> userIds)
        {
            Path = path;
            Ids = userIds;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task HistoryOfNewLocalRepositoryCanBeRead()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"empty-log-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var history = await new LoreRevisionHistoryService().ReadAsync(path);
            Assert.Empty(history);
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
    public async Task HistoryFollowsCurrentLocalBranch()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"branch-log-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = Path.Combine(path, "tracked.txt");
            await File.WriteAllTextAsync(file, "main");
            using var stage = new LoreFileStageArgs { Paths = [file] };
            using var commit = new LoreRevisionCommitArgs { Message = "main commit" };
            await Lore.FileStage(globals, stage).WaitAsync();
            await Lore.RevisionCommit(globals, commit).WaitAsync();
            using var createBranch = new LoreBranchCreateArgs { Branch = "feature" };
            await Lore.BranchCreate(globals, createBranch).WaitAsync();
            using var switchBranch = new LoreBranchSwitchArgs { Branch = "feature" };
            await Lore.BranchSwitch(globals, switchBranch).WaitAsync();
            await File.WriteAllTextAsync(file, "feature");
            using var featureCommit = new LoreRevisionCommitArgs { Message = "feature commit" };
            await Lore.FileStage(globals, stage).WaitAsync();
            await Lore.RevisionCommit(globals, featureCommit).WaitAsync();

            var service = new LoreRevisionHistoryService();
            var feature = await service.ReadAsync(path);
            Assert.Equal("feature commit", feature[0].Message);
            Assert.Contains(feature, entry => entry.Message == "main commit");
            using var switchMain = new LoreBranchSwitchArgs { Branch = "main" };
            await Lore.BranchSwitch(globals, switchMain).WaitAsync();
            var main = await service.ReadAsync(path);
            Assert.Equal("main commit", main[0].Message);
            Assert.DoesNotContain(main, entry => entry.Message == "feature commit");
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
    public async Task ReadsNewestHundredLocalRevisionsWithMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"lore://localhost:41337/log-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = Path.Combine(path, "tracked.txt");
            var start = DateTimeOffset.UtcNow.AddSeconds(-1);
            for (var i = 0; i < 101; i++)
            {
                await File.WriteAllTextAsync(file, $"content {i}");
                using var stage = new LoreFileStageArgs { Paths = [file] };
                using var commit = new LoreRevisionCommitArgs { Message = $"message {i}" };
                await Lore.FileStage(globals, stage).WaitAsync();
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            }

            var history = await new LoreRevisionHistoryService().ReadAsync(path);
            Assert.Equal(100, history.Count);
            Assert.Equal(Enumerable.Range(1, 100).Reverse().Select(i => $"message {i}"),
                history.Select(entry => entry.Message));
            Assert.All(history, entry => Assert.Equal(64, entry.Revision.Length));
            Assert.True(history[0].Number > history[^1].Number);
            Assert.NotNull(history[0].Timestamp);
            Assert.InRange(history[0].Timestamp!.Value, start, DateTimeOffset.UtcNow.AddSeconds(1));
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
