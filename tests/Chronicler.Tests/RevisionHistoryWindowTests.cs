using System.IO;
using System.Windows.Controls;
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
            Assert.Contains("ありません", Control<TextBox>(window, "HistoryStatus").Text);
            Click(window, "RefreshHistoryButton");
            PumpDispatcher();
            Assert.Contains("history error", Control<TextBox>(window, "HistoryStatus").Text);
            Assert.Empty(Control<ListBox>(window, "RevisionHistoryList").Items);
            Assert.True(Control<Button>(window, "RefreshHistoryButton").IsEnabled);
            Click(window, "RefreshHistoryButton");
            PumpDispatcher();
            Assert.Equal("loaded", Assert.IsType<RevisionHistoryEntry>(
                Assert.Single(Control<ListBox>(window, "RevisionHistoryList").Items)).Message);
            Assert.Contains("最大100件", Control<TextBox>(window, "HistoryStatus").Text);
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
            Assert.Contains("リビジョン履歴の取得", status);
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
            Assert.Contains("ローカルリポジトリの確認", status);
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
            Click(window, "SwitchBranchButton");
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
            Assert.Contains("初回取得後", Control<TextBox>(window, "HistoryStatus").Text);
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
        ILatestService? latest = null, IRepositoryService? repositories = null) =>
        new(server, store, repositories ?? new UnusedRepositoryService(), (_, _) => true, latestService: latest,
            repositoryInfo: repositoryInfo, branchService: branches, historyService: history);

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
