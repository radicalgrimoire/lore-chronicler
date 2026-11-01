using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chronicler.Projects;
using Chronicler.Servers;
using LoreVcs;
using LoreVcs.Types.Args;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class CommitWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void CommitUsesAllStagedEntriesRefreshesStatusAndInvalidatesHistory()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var committed = false;
            var commits = new FakeCommitService { Commit = (_, _) => { committed = true; return Task.CompletedTask; } };
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(Snapshot(path, committed ? [] : ["a.txt", "b.txt"]))
            };
            var history = new FakeHistoryService { Read = _ => Task.FromResult<IReadOnlyList<RevisionHistoryEntry>>(
                [new(1, "revision", committed ? "new" : "old", null, null)]) };
            var window = CreateWindow(commits, status, history, project);
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "UpdateLogTab");
            Assert.Equal("old", Assert.IsType<RevisionHistoryEntry>(Control<ListBox>(window, "RevisionHistoryList").Items[0]).Message);
            SelectTab(window, "StageTab");
            var list = Control<ListBox>(window, "StageList");
            list.SelectedIndex = 0;
            var button = Control<Button>(window, "CommitButton");
            Assert.False(button.IsEnabled);
            Control<TextBox>(window, "CommitMessageText").Text = "  \n";
            Assert.False(button.IsEnabled);
            Control<TextBox>(window, "CommitMessageText").Text = "title\n\nbody";
            Assert.True(button.IsEnabled);
            Click(window, "CommitButton");
            Assert.Equal((project.LocalPath, "title\n\nbody"), Assert.Single(commits.Calls));
            Assert.Empty(list.Items);
            Assert.Empty(Control<TextBox>(window, "CommitMessageText").Text);
            Assert.Contains("サーバーへの送信は行っていません", Control<TextBlock>(window, "StatusText").Text);
            Assert.False(button.IsEnabled);
            SelectTab(window, "UpdateLogTab");
            Assert.Equal("new", Assert.IsType<RevisionHistoryEntry>(Control<ListBox>(window, "RevisionHistoryList").Items[0]).Message);
            Assert.Equal(2, history.Paths.Count);
            window.Close();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrConflictedStageCancelsCommitAndKeepsDraft(bool conflict)
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var calls = 0;
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(++calls == 1
                    ? Snapshot(path, ["a.txt"])
                    : Snapshot(path, ["b.txt"], conflict))
            };
            var commits = new FakeCommitService();
            var window = CreateWindow(commits, status, new FakeHistoryService(), project);
            window.Show();
            SelectProject(window, 0);
            var message = Control<TextBox>(window, "CommitMessageText");
            message.Text = "draft";
            Click(window, "CommitButton");
            Assert.Empty(commits.Calls);
            Assert.Equal("draft", message.Text);
            Assert.Equal("b.txt", Assert.IsType<StagedEntry>(Control<ListBox>(window, "StageList").Items[0]).RelativePath);
            Assert.Equal(conflict, Control<TextBox>(window, "StageStatusText").Text.Contains("競合"));
            Assert.Contains("中止", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal(!conflict, Control<Button>(window, "CommitButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void FailedCommitPreservesDraftAndRescansBeforeRetry()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var pending = new TaskCompletionSource();
            var commits = new FakeCommitService { Commit = (_, _) => pending.Task };
            var status = new FakeFileStatusService { Scan = path => Task.FromResult(Snapshot(path, ["file.txt"])) };
            var window = CreateWindow(commits, status, new FakeHistoryService(), project);
            window.Show();
            SelectProject(window, 0);
            var message = Control<TextBox>(window, "CommitMessageText");
            message.Text = "draft";
            Click(window, "CommitButton");
            Assert.False(message.IsEnabled);
            Assert.False(Control<Button>(window, "CommitButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            pending.SetException(new IOException("commit failure"));
            PumpDispatcher();
            Assert.True(message.IsEnabled);
            Assert.Equal("draft", message.Text);
            Assert.Contains("commit failure", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("再試行前", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal(3, status.Paths.Count);
            Assert.Single(Control<ListBox>(window, "StageList").Items);
            window.Close();
        });
    }

    [Fact]
    public void FailedPrecommitScanDoesNotCallCommitOrShowStaleStage()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var status = new FakeFileStatusService();
            status.Scan = path => status.Paths.Count == 1
                ? Task.FromResult(Snapshot(path, ["file.txt"]))
                : Task.FromException<FileStatusSnapshot>(new IOException("scan failed"));
            var commits = new FakeCommitService();
            var window = CreateWindow(commits, status, new FakeHistoryService(), project);
            window.Show();
            SelectProject(window, 0);
            Control<TextBox>(window, "CommitMessageText").Text = "draft";
            Click(window, "CommitButton");
            Assert.Empty(commits.Calls);
            Assert.Equal("draft", Control<TextBox>(window, "CommitMessageText").Text);
            Assert.Empty(Control<ListBox>(window, "StageList").Items);
            Assert.Contains("scan failed", Control<TextBox>(window, "StageStatusText").Text);
            Assert.Contains("コミット前の確認に失敗", Control<TextBlock>(window, "StatusText").Text);
            Assert.False(Control<Button>(window, "CommitButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void DraftsStayWithTheirProject()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            var window = CreateWindow(new FakeCommitService(),
                new FakeFileStatusService { Scan = path => Task.FromResult(Snapshot(path, ["file.txt"])) },
                new FakeHistoryService(), first, second);
            window.Show();
            SelectProject(window, 0);
            var message = Control<TextBox>(window, "CommitMessageText");
            message.Text = "first draft";
            SelectProject(window, 1);
            Assert.Empty(message.Text);
            message.Text = "second draft";
            SelectProject(window, 0);
            Assert.Equal("first draft", message.Text);
            window.Close();
        });
    }

    [Fact]
    public void TwoThousandStagedFilesScrollWithoutMovingCommitControls()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(Snapshot(path, Enumerable.Range(0, 2000).Select(i => $"file{i:D4}.txt").ToArray()))
            };
            var window = CreateWindow(new FakeCommitService(), status, new FakeHistoryService(), project);
            window.Height = 480;
            window.Show();
            SelectProject(window, 0);
            SelectTab(window, "StageTab");
            var list = Control<ListBox>(window, "StageList");
            var message = Control<TextBox>(window, "CommitMessageText");
            var button = Control<Button>(window, "CommitButton");
            window.UpdateLayout();
            Assert.Equal(2000, list.Items.Count);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(1999));
            var scroll = FindScrollViewer(list);
            Assert.NotNull(scroll);
            Assert.True(scroll.ScrollableHeight > 0);
            var before = button.TransformToAncestor(window).Transform(new Point(0, 0));
            list.ScrollIntoView(list.Items[1999]);
            window.UpdateLayout();
            PumpDispatcher();
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(1999));
            Assert.Equal(before, button.TransformToAncestor(window).Transform(new Point(0, 0)));
            Assert.True(message.IsVisible);
            Assert.True(button.IsVisible);
            Assert.True(before.Y + button.ActualHeight < window.ActualHeight);
            window.Close();
        });
    }

    [Fact]
    public async Task RealSdkCommitsFromWindow()
    {
        var path = Path.Combine(directory, "real");
        Directory.CreateDirectory(path);
        using (var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true })
        using (var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"commit-ui-{Guid.NewGuid():N}" })
            await Lore.RepositoryCreate(globals, create).WaitAsync();
        var file = Path.Combine(path, "file.txt");
        await File.WriteAllTextAsync(file, "content");
        await new LoreStageService().StageAsync(path, file);
        await File.WriteAllTextAsync(file, "edited after staging");
        var untracked = Path.Combine(path, "untracked.txt");
        await File.WriteAllTextAsync(untracked, "not staged");
        var project = new ProjectProfile(Guid.NewGuid(), "real", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
        RunSta(() =>
        {
            var window = CreateWindow(new LoreCommitService(), new LoreFileStatusService(), new FakeHistoryService(), project);
            window.Show();
            SelectProject(window, 0);
            Control<TextBox>(window, "CommitMessageText").Text = "real commit";
            WaitUntil(() => Control<Button>(window, "CommitButton").IsEnabled);
            Click(window, "CommitButton");
            WaitUntil(() => Control<TextBlock>(window, "StatusText").Text.Contains("コミットしました")
                && Control<TextBox>(window, "StageStatusText").Text.Contains("ありません"));
            Assert.Empty(Control<TextBox>(window, "CommitMessageText").Text);
            window.Close();
        });
        var history = await new LoreRevisionHistoryService().ReadAsync(path);
        Assert.Equal("real commit", history[0].Message);
        var result = await new LoreFileStatusService().ScanAsync(path);
        Assert.Equal(FileChangeKind.None, result.GetFileStatus(file));
        Assert.Equal(FileChangeKind.Untracked, result.GetFileStatus(untracked));
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private WorkspaceWindow CreateWindow(ICommitService commit, IFileStatusService status, IRevisionHistoryService history,
        params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
            repositoryInfo: repositoryInfo, fileStatusService: status, fileLockService: new FakeFileLockService(),
            historyService: history, commitService: commit);
    }

    private static FileStatusSnapshot Snapshot(string root, string[] paths, bool conflict = false)
    {
        var files = paths.ToDictionary(path => path, _ => FileChangeKind.Staged);
        if (conflict) files.Add("conflict.txt", FileChangeKind.Conflict);
        return new FileStatusSnapshot(root, files);
    }

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

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Workspace state did not update.");
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeCommitService : ICommitService
    {
        public List<(string Path, string Message)> Calls { get; } = [];
        public Func<string, string, Task> Commit { get; set; } = (_, _) => Task.CompletedTask;

        public Task CommitAsync(string path, string message)
        {
            Calls.Add((path, message));
            return Commit(path, message);
        }
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

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
