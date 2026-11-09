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
            Assert.Contains("Local revision", confirmation);
            Assert.Contains("Committed changes", confirmation);
            Assert.Contains("Uncommitted changes", confirmation);
            Assert.Contains("missing server branch may be created or restored", confirmation);
            Assert.Contains("link targets and layer repositories", confirmation);
            Assert.Equal(1, service.Pushes);
            Assert.False(Control<Button>(window, "PushButton").IsEnabled);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            pending.SetResult();
            PumpDispatcher();
            Assert.Equal(1, service.Verifications);
            Assert.Contains("Changes were pushed", Control<TextBlock>(window, "StatusText").Text);
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
            Assert.Contains("Push cancelled.", Control<TextBlock>(window, "StatusText").Text);
            window.Close();

            service = new FakePushService { PrepareError = new PushException("Remote history diverged") };
            window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Equal(0, service.Pushes);
            Assert.Contains("Remote history diverged", Control<TextBlock>(window, "StatusText").Text);
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
            Assert.Contains("The push failed.", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("network failure", Control<TextBlock>(window, "StatusText").Text);
            window.Close();

            service = new FakePushService { Verified = false };
            window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Contains("could not be verified", Control<TextBlock>(window, "StatusText").Text);
            Assert.DoesNotContain("Changes were pushed", Control<TextBlock>(window, "StatusText").Text);
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
            Assert.Contains("Unable to prepare the push", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void PushAutomaticallyAuthenticatesBeforeConfirmationAndRetriesPreflight()
    {
        RunSta(() =>
        {
            var prepareCalls = 0;
            var authenticationCalls = 0;
            var service = new FakePushService
            {
                Prepare = () => ++prepareCalls <= 2
                    ? Task.FromException<LatestStatus>(new RemoteAuthenticationRequiredException("Sign-in to the server is required."))
                    : Task.FromResult(Status)
            };
            var window = CreateWindow(service, (_, _) => true, _ =>
            {
                authenticationCalls++;
                return true;
            });
            SelectProject(window);
            Click(window, "PushButton");
            Assert.Equal(1, authenticationCalls);
            Assert.True(prepareCalls >= 3);
            Assert.Equal(1, service.Pushes);
            window.Close();
        });
    }

    [Fact]
    public void ShowsUnpushedCommitCountAndConfirmsBeforeClosing()
    {
        RunSta(() =>
        {
            var confirmations = new List<string>();
            var window = CreateWindow(new FakePushService(), (message, _) =>
            {
                confirmations.Add(message);
                return true;
            });
            SelectProject(window);
            PumpDispatcher();

            Assert.Equal("Push (3)", Control<Button>(window, "PushButton").Content);
            window.Close();
            PumpDispatcher();

            Assert.Contains(confirmations, message => message.Contains("have not been pushed"));
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public void ChecksPushStatusAfterTheInitialFileScan()
    {
        RunSta(() =>
        {
            var scan = new TaskCompletionSource<FileStatusSnapshot>();
            var status = new FakeFileStatusService { Scan = _ => scan.Task };
            var calls = 0;
            var service = new FakePushService
            {
                Prepare = () =>
                {
                    calls++;
                    return Task.FromResult(Status);
                }
            };
            var window = CreateWindow(service, (_, _) => true, fileStatusService: status);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            PumpDispatcher();
            Assert.Single(status.Paths);
            Assert.Equal(0, calls);
            scan.SetResult(new FileStatusSnapshot(status.Paths[0], new Dictionary<string, FileChangeKind>()));
            PumpDispatcher();
            Assert.Equal(1, calls);
            Assert.Equal("Push (3)", Control<Button>(window, "PushButton").Content);
            window.Close();
            PumpDispatcher();
        });
    }

    [Fact]
    public void DecliningCloseConfirmationKeepsWindowOpen()
    {
        RunSta(() =>
        {
            var window = CreateWindow(new FakePushService(), (_, _) => false);
            SelectProject(window);
            window.Close();
            PumpDispatcher();
            Assert.True(window.IsVisible);
        });
    }

    [Fact]
    public void ClosingWaitsForStatusAndBlocksConflictingOperations()
    {
        RunSta(() =>
        {
            var pending = new TaskCompletionSource<LatestStatus>();
            var calls = 0;
            var service = new FakePushService
            {
                Prepare = () => ++calls == 1 ? Task.FromResult(Status) : pending.Task
            };
            var window = CreateWindow(service, (_, _) => true);
            SelectProject(window);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            Assert.False(closed);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            pending.SetResult(Status);
            PumpDispatcher();
            Assert.True(closed);
        });
    }

    [Fact]
    public void UnavailableStatusCanBeReportedWhenClosing()
    {
        RunSta(() =>
        {
            var messages = new List<string>();
            var window = CreateWindow(new FakePushService
            {
                PrepareError = new LatestSyncException("Status unavailable")
            }, (message, _) =>
            {
                messages.Add(message);
                return true;
            });
            SelectProject(window);
            window.Close();
            PumpDispatcher();
            Assert.Contains(messages, message => message.Contains("Status unavailable"));
            Assert.False(window.IsVisible);
        });
    }

    private WorkspaceWindow CreateWindow(IPushService service, Func<string, string, bool> confirm,
        Func<string, bool>? authenticate = null, IFileStatusService? fileStatusService = null)
    {
        var path = Path.Combine(directory, "repo");
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        var project = new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save([project]);
        var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), confirm,
            repositoryInfo: repositoryInfo, fileStatusService: fileStatusService ?? new FakeFileStatusService(),
            authenticate: authenticate, fileLockService: new FakeFileLockService(), pushService: service,
            unpushedCommitService: new FakeUnpushedCommitService());
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
        public Func<Task<LatestStatus>>? Prepare { get; init; }
        public Task Pending { get; init; } = Task.CompletedTask;
        public bool Verified { get; init; } = true;
        public int Pushes { get; private set; }
        public int Verifications { get; private set; }
        public Task<LatestStatus> PrepareAsync(string path) => Prepare is not null
            ? Prepare()
            : PrepareError is null ? Task.FromResult(Status) : Task.FromException<LatestStatus>(PrepareError);
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

    private sealed class FakeUnpushedCommitService : IUnpushedCommitService
    {
        public Task<UnpushedCommitCount> ReadAsync(string path, LatestStatus status) =>
            Task.FromResult(new UnpushedCommitCount(status.LocalAhead ? 3 : 0, true));
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
