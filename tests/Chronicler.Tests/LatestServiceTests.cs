using System.IO;
using Chronicler.Projects;
using LoreVcs;
using LoreVcs.Types.Args;

namespace Chronicler.Tests;

public sealed class LatestServiceTests
{
    private static readonly string Local = new('1', 64);
    private static readonly string Remote = new('2', 64);
    private static readonly IProgress<string> Progress = new Progress<string>();
    private static LatestStatus SafeStatus => new("main", Local, Remote, true, true, true, false, true);

    [Fact]
    public async Task SyncPinsVerifiedRemoteRevision()
    {
        var backend = new FakeBackend { Status = SafeStatus, Outcome = new("main", Remote, true, true, false) };
        var result = await new LoreLatestService(backend).GetLatestAsync("repo", Progress);
        Assert.Equal(SafeStatus, result);
        Assert.Equal($"main@{Remote}", backend.Target);
        Assert.Equal("repo", backend.Path);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RefusesUnverifiedOrDivergentHistory(bool remoteAuthorized, bool localAhead)
    {
        var backend = new FakeBackend { Status = SafeStatus with { RemoteAuthorized = remoteAuthorized, LocalAhead = localAhead } };
        await Assert.ThrowsAsync<LatestSyncException>(() => new LoreLatestService(backend).GetLatestAsync("repo", Progress));
        Assert.Null(backend.Target);
    }

    [Fact]
    public async Task RefusesMissingOrInconsistentRemoteInformation()
    {
        var statuses = new[]
        {
            SafeStatus with { RemoteAvailable = false },
            SafeStatus with { RemoteBranchExists = false },
            SafeStatus with { Branch = "" },
            SafeStatus with { Branch = "bad@branch" },
            SafeStatus with { RemoteRevision = new string('0', 64) },
            SafeStatus with { RemoteAhead = false }
        };
        foreach (var status in statuses)
        {
            var backend = new FakeBackend { Status = status };
            await Assert.ThrowsAsync<LatestSyncException>(() => new LoreLatestService(backend).GetLatestAsync("repo", Progress));
            Assert.Null(backend.Target);
        }
    }

    [Fact]
    public async Task RefusesUnverifiedOrConflictedSyncResults()
    {
        var outcomes = new[]
        {
            new LatestSyncOutcome("main", Remote, false, true, false),
            new LatestSyncOutcome("main", Remote, true, false, false),
            new LatestSyncOutcome("other", Remote, true, true, false),
            new LatestSyncOutcome("main", Local, true, true, false),
            new LatestSyncOutcome("main", Remote, true, true, true)
        };
        foreach (var outcome in outcomes)
        {
            var backend = new FakeBackend { Status = SafeStatus, Outcome = outcome };
            await Assert.ThrowsAsync<LatestSyncException>(() => new LoreLatestService(backend).GetLatestAsync("repo", Progress));
            Assert.NotNull(backend.Target);
        }
    }

    [Fact]
    public async Task SdkSyncsExplicitRevisionInOfflineRepository()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var globals = new LoreGlobalArgs { RepositoryPath = directory, Offline = true };
            using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"test-{Guid.NewGuid():N}" };
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var file = System.IO.Path.Combine(directory, "file.txt");
            await File.WriteAllTextAsync(file, "first");
            using var stage = new LoreFileStageArgs { Paths = [file] };
            using var commit = new LoreRevisionCommitArgs { Message = "first" };
            await Lore.FileStage(globals, stage).WaitAsync();
            await Lore.RevisionCommit(globals, commit).WaitAsync();
            var backend = new LoreLatestBackend();
            var first = await backend.ReadStatusAsync(directory);
            await Assert.ThrowsAsync<LatestSyncException>(() => new LoreLatestService().GetLatestAsync(directory, Progress));
            await File.WriteAllTextAsync(file, "second");
            await Lore.FileStage(globals, stage).WaitAsync();
            await Lore.RevisionCommit(globals, commit).WaitAsync();
            var second = await backend.ReadStatusAsync(directory);
            Assert.NotEqual(first.LocalRevision, second.LocalRevision);

            var outcome = await backend.SyncAsync(directory, $"{first.Branch}@{first.LocalRevision}");
            Assert.Equal(first.LocalRevision, outcome.Revision);
            Assert.Equal("first", await File.ReadAllTextAsync(file));
            await File.WriteAllTextAsync(file, "local edits");
            await Assert.ThrowsAsync<LoreError>(() => backend.SyncAsync(directory, $"{second.Branch}@{second.LocalRevision}"));
            Assert.Equal("local edits", await File.ReadAllTextAsync(file));
            await File.WriteAllTextAsync(file, "first");
            outcome = await backend.SyncAsync(directory, $"{second.Branch}@{second.LocalRevision}");
            Assert.Equal(second.LocalRevision, outcome.Revision);
            Assert.Equal("second", await File.ReadAllTextAsync(file));
            Assert.False(outcome.RemoteAuthorized);
            outcome = await backend.SyncAsync(directory, $"{second.Branch}@{second.LocalRevision}");
            Assert.Equal(second.LocalRevision, outcome.Revision);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SdkReadsLocalRepositoryIdAndRemoteWithoutConnecting()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var globals = new LoreGlobalArgs { RepositoryPath = directory, Offline = true };
                using var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"lore://localhost:41337/test-{Guid.NewGuid():N}" };
                await Lore.RepositoryCreate(globals, create).WaitAsync();
                Assert.Equal("lore://localhost:41337", await new ProjectRepositoryInfo().ReadServerUrlAsync(directory));
                var info = await new ProjectRepositoryInfo().ReadLocalAsync(directory);
                Assert.Equal("lore://localhost:41337", info.RemoteUrl);
                Assert.Equal(32, info.RepositoryId.Length);
                Assert.NotEqual(new string('0', 32), info.RepositoryId);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    private sealed class FakeBackend : ILatestBackend
    {
        public LatestStatus Status { get; init; } = SafeStatus;
        public LatestSyncOutcome Outcome { get; init; } = new("main", Remote, true, true, false);
        public string? Path { get; private set; }
        public string? Target { get; private set; }

        public Task<LatestStatus> ReadStatusAsync(string path) => Task.FromResult(Status);

        public Task<LatestSyncOutcome> SyncAsync(string path, string revision)
        {
            Path = path;
            Target = revision;
            return Task.FromResult(Outcome);
        }
    }
}
