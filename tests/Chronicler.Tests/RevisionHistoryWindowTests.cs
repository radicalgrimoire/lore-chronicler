using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class RevisionHistoryWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void RightClickOpensSelectedRevisionAndRetryShowsErrorsAndChanges()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var first = Entry("first");
            var second = new RevisionHistoryEntry(2, new string('B', 64), "second", "user", null);
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([first, second])
            };
            var delayed = new TaskCompletionSource<IReadOnlyList<RevisionChange>>();
            var changes = new FakeRevisionChangesService { Read = (_, _) => delayed.Task };
            var diffs = new FakeFileDiffService();
            var window = CreateWindow(CreateStore(project), history, changes: changes, diffs: diffs);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            var list = Control<ListBox>(window, "RevisionHistoryList");
            Assert.False(Control<Button>(window, "ShowRevisionChangesButton").IsEnabled);
            list.SelectedIndex = 0;
            list.UpdateLayout();
            var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                Source = item
            });
            Assert.Same(second, list.SelectedItem);
            var menu = Assert.IsType<MenuItem>(list.ContextMenu.Items[0]);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpDispatcher();
            var details = Assert.Single(window.OwnedWindows.OfType<RevisionChangesWindow>());
            Assert.Equal([(project.LocalPath, second.Revision)], changes.Requests);
            Assert.Contains("second", Control<TextBlock>(details, "RevisionMessage").Text);
            Assert.Contains(second.Revision, Control<TextBlock>(details, "RevisionId").Text);
            Assert.Contains("Loading", Control<TextBox>(details, "ChangesStatus").Text);
            Assert.False(Control<Button>(details, "RetryButton").IsEnabled);
            delayed.SetException(new IOException("offline"));
            PumpDispatcher();
            Assert.Contains("offline", Control<TextBox>(details, "ChangesStatus").Text);
            Assert.True(Control<Button>(details, "RetryButton").IsEnabled);
            var entry = new RevisionChange("folder/file.txt", ChangeAction.Added, true, null);
            changes.Read = (_, _) => Task.FromResult<IReadOnlyList<RevisionChange>>([entry]);
            Click(details, "RetryButton");
            PumpDispatcher();
            Assert.Equal(entry, Assert.Single(Control<ListBox>(details, "RevisionChangesList").Items.Cast<RevisionChange>()));
            Assert.Contains("1 changes", Control<TextBox>(details, "ChangesStatus").Text);
            Control<ListBox>(details, "RevisionChangesList").SelectedItem = entry;
            Click(details, "ShowFileDiffButton");
            PumpDispatcher();
            var diff = Assert.Single(details.OwnedWindows.OfType<FileDiffWindow>());
            Assert.Equal((project.LocalPath, second.Revision, entry), Assert.Single(diffs.Requests));
            Assert.Contains("-before", Control<TextBox>(diff, "DiffText").Text);
            Assert.Contains("+after", Control<TextBox>(diff, "DiffText").Text);
            diff.Close();
            details.Close();
            window.Close();
        });
    }

    [Fact]
    public void ClosedDetailsIgnoreLateResults()
    {
        RunSta(() =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<RevisionChange>>();
            var details = new RevisionChangesWindow("Project", Entry("message"), () => pending.Task);
            details.Show();
            PumpDispatcher();
            var status = Control<TextBox>(details, "ChangesStatus").Text;
            details.Close();
            pending.SetResult([new RevisionChange("file", ChangeAction.Added, true, null)]);
            PumpDispatcher();
            Assert.Empty(Control<ListBox>(details, "RevisionChangesList").Items);
            Assert.Equal(status, Control<TextBox>(details, "ChangesStatus").Text);
        });
    }

    [Fact]
    public void DetailsVerifyRepositoryBeforeLoadingAndAllowRetry()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry("message")])
            };
            var changes = new FakeRevisionChangesService();
            var window = CreateWindow(CreateStore(project), history, changes: changes);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Control<ListBox>(window, "RevisionHistoryList").SelectedIndex = 0;
            repositoryInfo.RepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            Click(window, "ShowRevisionChangesButton");
            PumpDispatcher();
            var details = Assert.Single(window.OwnedWindows.OfType<RevisionChangesWindow>());
            Assert.Empty(changes.Requests);
            Assert.Contains("repository ID does not match", Control<TextBox>(details, "ChangesStatus").Text);
            repositoryInfo.RepositoryId = project.RepositoryId!;
            Click(details, "RetryButton");
            PumpDispatcher();
            Assert.Single(changes.Requests);
            Assert.Contains("No changes", Control<TextBox>(details, "ChangesStatus").Text);
            details.Close();
            window.Close();
        });
    }

    [Fact]
    public void AuthorResolutionWarningKeepsHistoryVisible()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>(
                    [new RevisionHistoryEntry(1, "rev", "message", "User:abc", null,
                        AuthorResolutionWarning: "directory unavailable")])
            };
            var window = CreateWindow(CreateStore(project), history);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");

            var entry = Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items));
            Assert.Equal("User:abc", entry.DisplayAuthor);
            Assert.Contains("directory unavailable", Control<TextBox>(window, "HistoryStatus").Text);
            Assert.Contains("Unresolved author IDs are shown", Control<TextBox>(window, "HistoryStatus").Text);
            window.Close();
        });
    }

    [Fact]
    public void TabLoadsOnDemandAndReportsEmptyErrorAndRefresh()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var store = CreateStore(project);
            var service = new FakeHistoryService();
            var results = new Queue<Task<IReadOnlyList<RevisionHistoryEntry>>>();
            results.Enqueue(Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([]));
            results.Enqueue(Task.FromException<IReadOnlyList<RevisionHistoryEntry>>(new IOException("history error")));
            results.Enqueue(Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry("loaded")]));
            service.Read = _ => results.Dequeue();
            var window = CreateWindow(store, service);
            window.Show();
            SelectProject(window, 0);
            Assert.Empty(service.Paths);
            SelectTab(window, "UpdateLogTab");
            Assert.Equal([project.LocalPath], service.Paths);
            Assert.Contains("No local revision history", Control<TextBox>(window, "HistoryStatus").Text);
            Click(window, "RefreshHistoryButton");
            PumpDispatcher();
            Assert.Contains("history error", Control<TextBox>(window, "HistoryStatus").Text);
            Assert.Empty(Control<ListBox>(window, "RevisionHistoryList").Items);
            Assert.True(Control<Button>(window, "RefreshHistoryButton").IsEnabled);
            Click(window, "RefreshHistoryButton");
            PumpDispatcher();
            Assert.Equal("loaded", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Contains("100", Control<TextBox>(window, "HistoryStatus").Text);
            window.Close();
        });
    }

    [Fact]
    public void HistoryLoreErrorsShowTheOperationAndSdkDetails()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var error = new LoreVcs.LoreError(79);
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromException<IReadOnlyList<RevisionHistoryEntry>>(error)
            };
            var window = CreateWindow(CreateStore(project), history);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            var status = Control<TextBox>(window, "HistoryStatus").Text;
            Assert.Contains("Revision history retrieval failed", status);
            Assert.Contains("79", status);
            Assert.Contains(error.Message, status);
            Assert.Empty(Control<ListBox>(window, "RevisionHistoryList").Items);
            Assert.True(Control<Button>(window, "RefreshHistoryButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void RepositoryLoreErrorsIdentifyTheVerificationStep()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var error = new LoreVcs.LoreError(79);
            var history = new FakeHistoryService();
            var window = CreateWindow(CreateStore(project), history);
            window.Show();
            SelectProject(window, 0);
            repositoryInfo.LocalError = error;
            SelectTab(window, "UpdateLogTab");
            var status = Control<TextBox>(window, "HistoryStatus").Text;
            Assert.Contains("Local repository verification failed", status);
            Assert.Contains("79", status);
            Assert.Contains(error.Message, status);
            Assert.Empty(history.Paths);
            window.Close();
        });
    }

    [Fact]
    public void SwitchingProjectsDiscardsDelayedHistory()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            var store = CreateStore(first, second);
            var delayed = new TaskCompletionSource<IReadOnlyList<RevisionHistoryEntry>>();
            var service = new FakeHistoryService
            {
                Read = path => path == first.LocalPath
                    ? delayed.Task
                    : Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry("second")])
            };
            var window = CreateWindow(store, service);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Assert.False(Control<Button>(window, "RefreshHistoryButton").IsEnabled);
            SelectTab(window, "ProjectsTab");
            SelectProject(window, 1);
            SelectTab(window, "UpdateLogTab");
            Assert.Equal("second", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            delayed.SetResult([Entry("first")]);
            PumpDispatcher();
            Assert.Equal("second", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Equal([first.LocalPath, second.LocalPath], service.Paths);
            window.Close();
        });
    }

    [Fact]
    public void BranchSwitchReloadsVisibleHistory()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var store = CreateStore(project);
            var branches = new FakeBranchService();
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry(branches.Current)])
            };
            var window = CreateWindow(store, history, branches);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Assert.Equal("main", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Click(window, "RefreshBranchesButton");
            PumpDispatcher();
            Control<ComboBox>(window, "BranchList").SelectedIndex = 1;
            PumpDispatcher();
            Assert.Equal("feature", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Equal(2, history.Paths.Count);
            window.Close();
        });
    }

    [Fact]
    public void ServerUpdateReloadsVisibleHistory()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var store = CreateStore(project);
            var latest = new FakeLatestService();
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry(latest.Completed ? "after" : "before")])
            };
            var window = CreateWindow(store, history, latest: latest);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Click(window, "GetLatestButton");
            Assert.Empty(Control<ListBox>(window, "RevisionHistoryList").Items);
            Assert.False(Control<Button>(window, "RefreshHistoryButton").IsEnabled);
            latest.Completed = true;
            latest.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            Assert.Equal("after", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Equal(2, history.Paths.Count);
            window.Close();
        });
    }

    [Fact]
    public void PendingCloneDisplaysHistoryAfterInitialFetch()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var project = new ProjectProfile(Guid.NewGuid(), "pending", server.Id, server.RemoteUrl, "repo",
                Path.Combine(directory, "clone"), CloneState: ProjectCloneState.Pending);
            var store = CreateStore(project);
            var history = new FakeHistoryService
            {
                Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([Entry("cloned")])
            };
            var window = CreateWindow(store, history, repositories: new FakeCloneService());
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Assert.Contains("History is available after the initial download", Control<TextBox>(window, "HistoryStatus").Text);
            Assert.Empty(history.Paths);
            Click(window, "GetLatestButton");
            PumpDispatcher();
            Assert.Equal("cloned", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Equal([project.LocalPath], history.Paths);
            window.Close();
        });
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private ProjectSettingsStore CreateStore(params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return store;
    }

    private WorkspaceWindow CreateWindow(ProjectSettingsStore store, FakeHistoryService history, IBranchService? branches = null,
        ILatestService? latest = null, IRepositoryService? repositories = null, IRevisionChangesService? changes = null,
        IFileDiffService? diffs = null) =>
        new(server, store, repositories ?? new UnusedRepositoryService(), (_, _) => true, latestService: latest,
            repositoryInfo: repositoryInfo, branchService: branches, historyService: history,
            fileStatusService: new FakeFileStatusService(), fileLockService: new FakeFileLockService(),
            revisionChangesService: changes, fileDiffService: diffs);

    private static void SelectProject(WorkspaceWindow window, int index)
    {
        Control<ListBox>(window, "ProjectList").SelectedIndex = index;
        Click(window, "SwitchProjectButton");
        PumpDispatcher();
    }

    private static void SelectTab(WorkspaceWindow window, string name)
    {
        Control<TabControl>(window, "WorkspaceTabs").SelectedItem = Control<TabItem>(window, name);
        PumpDispatcher();
    }

    private static RevisionHistoryEntry Entry(string message) => new(1, new string('A', 64), message, null, null);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeHistoryService : IRevisionHistoryService
    {
        public List<string> Paths { get; } = [];
        public Func<string, Task<IReadOnlyList<RevisionHistoryEntry>>> Read { get; set; } =
            _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>([]);

        public Task<IReadOnlyList<RevisionHistoryEntry>> ReadAsync(string path)
        {
            Paths.Add(path);
            return Read(path);
        }

    }

    private sealed class FakeRevisionChangesService : IRevisionChangesService
    {
        public List<(string, string)> Requests { get; } = [];
        public Func<string, string, Task<IReadOnlyList<RevisionChange>>> Read { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<RevisionChange>>([]);

        public Task<IReadOnlyList<RevisionChange>> ReadAsync(string path, string revision)
        {
            Requests.Add((path, revision));
            return Read(path, revision);
        }

    }

    private sealed class FakeFileDiffService : IFileDiffService
    {
        public List<(string Path, string Revision, RevisionChange Change)> Requests { get; } = [];

        public Task<FileDiff> ReadAsync(string path, string revision, RevisionChange change)
        {
            Requests.Add((path, revision, change));
            return Task.FromResult(new FileDiff("before\n", "after\n", "before.txt", "after.txt", true, null));
        }
    }

    private sealed class FakeBranchService : IBranchService
    {
        public string Current { get; private set; } = "main";

        public Task<BranchListResult> ListAsync(string path) => Task.FromResult(new BranchListResult([
            new BranchEntry("1", "main", true, false, Current == "main"),
            new BranchEntry("2", "feature", true, false, Current == "feature")
        ], false));

        public Task SwitchAsync(string path, BranchEntry branch)
        {
            Current = branch.Name;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLatestService : ILatestService
    {
        public bool Completed { get; set; }
        public TaskCompletionSource<LatestStatus> Completion { get; } = new();
        public Task EnsureRemoteAuthorizedAsync(string path) => Task.CompletedTask;
        public Task<LatestStatus> GetLatestAsync(string path, IProgress<string> progress) => Completion.Task;
    }

    private sealed class FakeCloneService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
        {
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
