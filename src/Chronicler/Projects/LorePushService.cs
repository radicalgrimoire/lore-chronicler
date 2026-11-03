using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public interface IPushService
{
    Task<LatestStatus> PrepareAsync(string path);
    Task PushAsync(string path, LatestStatus expected, IProgress<string> progress);
    Task<bool> VerifyAsync(string path, LatestStatus expected);
}

public interface IPushBackend
{
    Task<LatestStatus> ReadStatusAsync(string path);
    Task<PushTarget> PushAsync(string path, string branch, IProgress<string> progress);
}

public sealed record PushTarget(string Branch, string LocalRevision);

public sealed class PushException(string message) : Exception(message);

public sealed class LorePushService(IPushBackend? backend = null) : IPushService
{
    private readonly IPushBackend backend = backend ?? new LorePushBackend();

    public async Task<LatestStatus> PrepareAsync(string path)
    {
        var status = await backend.ReadStatusAsync(path);
        Validate(status);
        return status;
    }

    public async Task PushAsync(string path, LatestStatus expected, IProgress<string> progress)
    {
        var current = await PrepareAsync(path);
        if (!string.Equals(current.Branch, expected.Branch, StringComparison.Ordinal)
            || !string.Equals(current.LocalRevision, expected.LocalRevision, StringComparison.OrdinalIgnoreCase))
            throw new PushException("The branch or local revision changed after confirmation. Push was cancelled.");

        var target = await backend.PushAsync(path, expected.Branch, progress);
        if (!string.Equals(target.Branch, expected.Branch, StringComparison.Ordinal)
            || !string.Equals(target.LocalRevision, expected.LocalRevision, StringComparison.OrdinalIgnoreCase))
            throw new PushException("The push target differs from the confirmed target. Check the server state.");
    }

    public async Task<bool> VerifyAsync(string path, LatestStatus expected)
    {
        var status = await backend.ReadStatusAsync(path);
        return status.RemoteAvailable && status.RemoteAuthorized && status.RemoteBranchExists
            && string.Equals(status.Branch, expected.Branch, StringComparison.Ordinal)
            && string.Equals(status.RemoteRevision, expected.LocalRevision, StringComparison.OrdinalIgnoreCase);
    }

    private static void Validate(LatestStatus status)
    {
        if (!status.RemoteAvailable || !status.RemoteAuthorized)
        {
            if (status.RemoteAvailable && !status.RemoteAuthorized)
                throw new RemoteAuthenticationRequiredException("Sign-in to the server is required.");
            throw new PushException("Could not verify server status. Check the connection, sign-in, and permissions.");
        }
        if (string.IsNullOrWhiteSpace(status.Branch) || status.Branch.Contains('@')
            || string.IsNullOrWhiteSpace(status.LocalRevision) || status.LocalRevision.All(c => c == '0'))
            throw new PushException("Could not verify the branch or revision to push.");
        if (status.RemoteBranchExists && status.RemoteAhead
            && !string.IsNullOrWhiteSpace(status.RemoteRevision) && !status.RemoteRevision.All(c => c == '0'))
            throw new PushException("The server history is ahead or has diverged. Push was cancelled; check the history.");
        if (status.RemoteBranchExists && (string.IsNullOrWhiteSpace(status.RemoteRevision)
            || (!status.RemoteRevision.All(c => c == '0') && !status.LocalAhead
                && !string.Equals(status.LocalRevision, status.RemoteRevision, StringComparison.OrdinalIgnoreCase))))
            throw new PushException("Could not determine the relationship between local and server histories. Push was cancelled.");
    }
}

public sealed class LorePushBackend : IPushBackend
{
    private readonly LoreLatestBackend statusBackend = new();

    public Task<LatestStatus> ReadStatusAsync(string path) => statusBackend.ReadStatusAsync(path);

    public async Task<PushTarget> PushAsync(string path, string branch, IProgress<string> progress)
    {
        PushTarget? target = null;
        string? error = null;
        var sync = new object();
        using var globals = new LoreGlobalArgs { RepositoryPath = path, Force = false };
        using var args = new LoreBranchPushArgs { Branch = branch, FastForwardMerge = false };
        await Lore.BranchPush(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.ERROR)
                    error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                else if (ev.Tag == LoreEventTag.BRANCH_PUSH)
                {
                    var data = ev.GetData<LoreBranchPushEventDataFFI>();
                    if (!data.FlagLink && !data.FlagLayer)
                        target = new PushTarget(data.BranchName, Convert.ToHexString(data.LocalRevision.Data));
                }
                else if (ev.Tag == LoreEventTag.BRANCH_PUSH_FRAGMENT_PROGRESS)
                {
                    var data = ev.GetData<LoreBranchPushFragmentProgressEventDataFFI>();
                    progress.Report($"Pushing… (data: {data.Complete}/{data.Count})");
                }
            }
        }).WaitAsync();
        if (error is not null) throw new InvalidDataException($"Could not complete push: {error}");
        return target ?? throw new InvalidDataException("Could not verify the push target.");
    }
}
