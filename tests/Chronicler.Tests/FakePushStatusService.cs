using Chronicler.Projects;

namespace Chronicler.Tests;

internal sealed class FakePushStatusService : IPushService
{
    private static readonly LatestStatus Status = new(
        "main", new string('1', 64), new string('1', 64), true, true, true, false, false);

    public Task<LatestStatus> PrepareAsync(string path) => Task.FromResult(Status);
    public Task PushAsync(string path, LatestStatus expected, IProgress<string> progress) =>
        throw new InvalidOperationException("Unexpected push");
    public Task<bool> VerifyAsync(string path, LatestStatus expected) =>
        throw new InvalidOperationException("Unexpected verification");
}
