using Chronicler.Projects;

namespace Chronicler.Tests;

public sealed class PushServiceTests
{
    private static readonly string Local = new('1', 64);
    private static readonly string Remote = new('2', 64);
    private static readonly IProgress<string> Progress = new Progress<string>();
    private static LatestStatus Safe => new("main", Local, Remote, true, true, true, true, false);

    [Fact]
    public async Task PushesConfirmedBranchAndVerifiesRemote()
    {
        var backend = new FakeBackend();
        var service = new LorePushService(backend);
        var expected = await service.PrepareAsync("repo");
        await service.PushAsync("repo", expected, Progress);
        Assert.Equal(("repo", "main"), Assert.Single(backend.Pushes));
        backend.Status = Safe with { RemoteRevision = Local, LocalAhead = false };
        Assert.True(await service.VerifyAsync("repo", expected));
    }

    [Fact]
    public async Task AllowsNewOrRestoredBranchAndAlreadyPushedBranch()
    {
        foreach (var status in new[]
        {
            Safe with { RemoteBranchExists = false, RemoteRevision = new string('0', 64), LocalAhead = false },
            Safe with { RemoteRevision = new string('0', 64), LocalAhead = false },
            Safe with { RemoteRevision = new string('0', 64), RemoteAhead = true },
            Safe with { RemoteRevision = Local, LocalAhead = false }
        })
        {
            var backend = new FakeBackend { Status = status };
            var service = new LorePushService(backend);
            await service.PushAsync("repo", await service.PrepareAsync("repo"), Progress);
            Assert.Single(backend.Pushes);
        }
    }

    [Fact]
    public async Task RejectsUnavailableUnauthorizedAheadAndAmbiguousStates()
    {
        foreach (var status in new[]
        {
            Safe with { RemoteAvailable = false },
            Safe with { RemoteAuthorized = false },
            Safe with { RemoteAhead = true },
            Safe with { RemoteAhead = true, LocalAhead = false },
            Safe with { Branch = "" },
            Safe with { Branch = "branch@revision" },
            Safe with { LocalRevision = new string('0', 64) },
            Safe with { RemoteRevision = "" },
            Safe with { LocalAhead = false }
        })
        {
            var backend = new FakeBackend { Status = status };
            await Assert.ThrowsAsync<PushException>(() => new LorePushService(backend).PrepareAsync("repo"));
            Assert.Empty(backend.Pushes);
        }
    }

    [Fact]
    public async Task RechecksTargetAndRemoteHistoryAfterConfirmation()
    {
        foreach (var changed in new[]
        {
            Safe with { Branch = "other" },
            Safe with { LocalRevision = new string('3', 64) },
            Safe with { RemoteAhead = true }
        })
        {
            var backend = new FakeBackend();
            var service = new LorePushService(backend);
            var expected = await service.PrepareAsync("repo");
            backend.Status = changed;
            await Assert.ThrowsAsync<PushException>(() => service.PushAsync("repo", expected, Progress));
            Assert.Empty(backend.Pushes);
        }
    }

    [Fact]
    public async Task ReportsChangedActualTargetAndUnverifiedResult()
    {
        var backend = new FakeBackend { Target = new("main", Remote) };
        var service = new LorePushService(backend);
        var expected = await service.PrepareAsync("repo");
        var error = await Assert.ThrowsAsync<PushException>(() => service.PushAsync("repo", expected, Progress));
        Assert.Contains("サーバーの状態を確認", error.Message);
        Assert.Single(backend.Pushes);
        Assert.False(await service.VerifyAsync("repo", expected));
        backend.Status = Safe with { RemoteRevision = Local, RemoteAvailable = false };
        Assert.False(await service.VerifyAsync("repo", expected));
        backend.Status = Safe with { RemoteRevision = Local, Branch = "other" };
        Assert.False(await service.VerifyAsync("repo", expected));
    }

    private sealed class FakeBackend : IPushBackend
    {
        public LatestStatus Status { get; set; } = Safe;
        public PushTarget Target { get; set; } = new("main", Local);
        public List<(string Path, string Branch)> Pushes { get; } = [];

        public Task<LatestStatus> ReadStatusAsync(string path) => Task.FromResult(Status);
        public Task<PushTarget> PushAsync(string path, string branch, IProgress<string> progress)
        {
            Pushes.Add((path, branch));
            return Task.FromResult(Target);
        }
    }
}
