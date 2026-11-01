using System.IO;
using System.Windows.Controls;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class PushWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();
    private static readonly LatestStatus Status = new("main", new string('1', 64), new string('2', 64), true, true, true, true, false);

    [Fact]
    public void ConfirmsTargetDisablesActionsDuringPushAndVerifies()
    {
        RunSta(() =>
        {
            var pending = new TaskCompletionSource();
            var service = new FakePushService { Pending = pending.Task };
            string? confirmation = null;
            var window = CreateWindow(service, (message, _) => { confirmation = message; return true; });
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Contains("main", confirmation);
            Assert.Contains(Status.LocalRevision, confirmation);
            Assert.Contains("リンク先", confirmation);
            Assert.Contains("未コミット", confirmation);
            Assert.Contains("作成または復元", confirmation);
            Assert.Equal(1, service.Pushes);
            Assert.False(Control<Button>(window, "PushButton").IsEnabled);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            pending.SetResult();
            PumpDispatcher();
            Assert.Equal(1, service.Verifications);
            Assert.Contains("プッシュしました", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(Control<Button>(window, "PushButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void CancelOrFailedPreflightNeverPushes()
    {
        RunSta(() =>
        {
            var service = new FakePushService();
            var window = CreateWindow(service, (_, _) => false);
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Equal(0, service.Pushes);
            Assert.Contains("中止", Control<TextBlock>(window, "StatusText").Text);
            window.Close();

            service = new FakePushService { PrepareError = new PushException("サーバーの履歴が先行") };
            window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Equal(0, service.Pushes);
            Assert.Contains("先行", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void FailedPushOrVerificationDoesNotClaimSuccess()
    {
        RunSta(() =>
        {
            var pending = new TaskCompletionSource();
            var service = new FakePushService { Pending = pending.Task };
            var window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            Click(window, "PushButton");
            pending.SetException(new IOException("network failure"));
            PumpDispatcher();
            Assert.Equal(0, service.Verifications);
            Assert.Contains("一部が送信", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("network failure", Control<TextBlock>(window, "StatusText").Text);
            window.Close();

            service = new FakePushService { Verified = false };
            window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Contains("確認できません", Control<TextBlock>(window, "StatusText").Text);
            Assert.DoesNotContain("プッシュしました", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void MismatchedRemoteRepositoryNeverPushes()
    {
        RunSta(() =>
        {
            var service = new FakePushService();
            var window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            repositoryInfo.ServerRepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            Click(window, "PushButton");
            Assert.Equal(0, service.Pushes);
            Assert.Contains("一致しません", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    private WorkspaceWindow CreateWindow(IPushService service, Func<string, string, bool> confirm)
    {
        var path = Path.Combine(directory, "repo");
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        var project = new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save([project]);
        var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), confirm,
            repositoryInfo: repositoryInfo, fileStatusService: new FakeFileStatusService(),
            fileLockService: new FakeFileLockService(), pushService: service);
        window.Show();
        return window;
    }

    private static void SelectProject(WorkspaceWindow window)
    {
        Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
        Click(window, "SwitchProjectButton");
        PumpDispatcher();
        Assert.True(Control<Button>(window, "PushButton").IsEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakePushService : IPushService
    {
        public Exception? PrepareError { get; init; }
        public Task Pending { get; init; } = Task.CompletedTask;
        public bool Verified { get; init; } = true;
        public int Pushes { get; private set; }
        public int Verifications { get; private set; }
        public Task<LatestStatus> PrepareAsync(string path) => PrepareError is null
            ? Task.FromResult(Status) : Task.FromException<LatestStatus>(PrepareError);
        public Task PushAsync(string path, LatestStatus expected, IProgress<string> progress)
        {
            Assert.Equal(Status, expected);
            Pushes++;
            return Pending;
        }
        public Task<bool> VerifyAsync(string path, LatestStatus expected)
        {
            Verifications++;
            return Task.FromResult(Verified);
        }
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
