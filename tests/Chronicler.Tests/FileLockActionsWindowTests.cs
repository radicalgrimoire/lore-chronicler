using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class FileLockActionsWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();
    private readonly FakeActions actions = new();
    private readonly FakeFileLockService icons = new();
    private bool confirm = true;

    [Fact]
    public void ChangesAndMyLocksSwitchWithoutLosingStateOrReloading()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var entry = new MyFileLock(Path.Combine(project.LocalPath, "asset.txt"), "asset.txt", "branch", "me");
            actions.Entries = [entry];
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var tabs = Control<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Control<TabItem>(window, "StageTab");
            PumpDispatcher();
            var changes = Control<Grid>(window, "ChangesView");
            var locks = Control<Grid>(window, "MyLocksView");
            var button = Control<Button>(window, "SwitchChangesViewButton");
            var message = Control<TextBox>(window, "CommitMessageText");
            var filter = Control<ComboBox>(window, "ChangeFilter");
            var list = Control<ListBox>(window, "MyLocksList");
            message.Text = "draft";
            filter.SelectedIndex = 1;
            Assert.True(changes.IsVisible);
            Assert.False(locks.IsVisible);
            Assert.Empty(actions.Queried);

            Click(window, "SwitchChangesViewButton");
            Assert.False(changes.IsVisible);
            Assert.True(locks.IsVisible);
            Assert.Equal("Back to Changes", button.Content);
            Assert.Equal([project.LocalPath], actions.Queried);
            list.SelectedIndex = 0;

            Click(window, "SwitchChangesViewButton");
            Assert.True(changes.IsVisible);
            Assert.False(locks.IsVisible);
            Assert.Equal("Show My Locks", button.Content);
            Assert.Equal("draft", message.Text);
            Assert.Equal(1, filter.SelectedIndex);
            Click(window, "SwitchChangesViewButton");
            Assert.Equal(entry, list.SelectedItem);
            tabs.SelectedItem = Control<TabItem>(window, "UpdateLogTab");
            tabs.SelectedItem = Control<TabItem>(window, "StageTab");
            PumpDispatcher();
            Assert.True(locks.IsVisible);
            Assert.Equal([project.LocalPath], actions.Queried);
            window.Close();
        });
    }

    [Fact]
    public void AcquiresSelectedFileAndReleasesExactLockAfterConfirmation()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var entry = new MyFileLock(Path.Combine(project.LocalPath, "asset.txt"), "asset.txt", "branch-one", "me");
            actions.Acquire = (_, _) =>
            {
                actions.Entries = [entry];
                return Task.CompletedTask;
            };
            actions.Release = (_, fileLock) =>
            {
                Assert.Equal(entry, fileLock);
                actions.Entries = [];
                return Task.CompletedTask;
            };
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var tree = Control<TreeView>(window, "FileTree");
            var root = Assert.IsType<TreeViewItem>(tree.Items[0]);
            var file = WaitForChild(root, "asset.txt");
            file.IsSelected = true;
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal([(project.LocalPath, entry.FullPath)], actions.Acquired);
            Assert.Equal(2, icons.Paths.Count);
            Assert.Single(Control<ListBox>(window, "MyLocksList").Items);
            Assert.Contains("Lock acquisition completed", Control<TextBlock>(window, "StatusText").Text);
            ShowMyLocks(window);
            var list = Control<ListBox>(window, "MyLocksList");
            list.SelectedIndex = 0;
            Assert.True(Control<Button>(window, "ReleaseMyLockButton").IsEnabled);
            confirm = false;
            Click(window, "ReleaseMyLockButton");
            Assert.Empty(actions.Released);
            confirm = true;
            Click(window, "ReleaseMyLockButton");
            Assert.Equal([(project.LocalPath, entry)], actions.Released);
            Assert.Empty(list.Items);
            Assert.False(Control<Button>(window, "ReleaseMyLockButton").IsEnabled);
            Assert.Contains("Lock release completed", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal(3, icons.Paths.Count);
            window.Close();
        });
    }

    [Fact]
    public void ViewQueriesOnlyActiveProjectAndIgnoresStaleResults()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            var delayed = new TaskCompletionSource<IReadOnlyList<MyFileLock>>();
            var secondEntry = new MyFileLock(Path.Combine(second.LocalPath, "asset.txt"), "asset.txt", "branch-two", "me");
            actions.Query = path => path == first.LocalPath ? delayed.Task
                : Task.FromResult<IReadOnlyList<MyFileLock>>([secondEntry]);
            var window = CreateWindow(first, second);
            window.Show();
            SelectProject(window, 0);
            Assert.Empty(actions.Queried);
            ShowMyLocks(window);
            Assert.Equal([first.LocalPath], actions.Queried);
            Assert.False(Control<Button>(window, "RefreshMyLocksButton").IsEnabled);
            SelectProject(window, 1);
            delayed.SetResult([new MyFileLock(Path.Combine(first.LocalPath, "asset.txt"), "asset.txt", "branch-one", "me")]);
            PumpDispatcher();
            Assert.Equal(secondEntry, Assert.Single(Control<ListBox>(window, "MyLocksList").Items.Cast<MyFileLock>()));
            Assert.Contains("second", Control<TextBox>(window, "MyLocksStatusText").Text);
            Click(window, "RefreshMyLocksButton");
            Assert.Equal([first.LocalPath, second.LocalPath, second.LocalPath], actions.Queried);
            window.Close();
        });
    }

    [Fact]
    public void FailedMutationIsNotRetriedAndRefreshFailureIsNotShownAsEmptySuccess()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            actions.Acquire = (_, _) => Task.FromException(new IOException("lock conflict"));
            actions.Query = _ => Task.FromException<IReadOnlyList<MyFileLock>>(new IOException("query unavailable"));
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            WaitForChild(root, "asset.txt").IsSelected = true;
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Single(actions.Acquired);
            Assert.Contains("lock conflict", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("query unavailable", Control<TextBox>(window, "MyLocksStatusText").Text);
            Assert.Empty(Control<ListBox>(window, "MyLocksList").Items);
            Assert.True(Control<Button>(window, "RefreshMyLocksButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void InvalidRepositoryAndDirectoriesCannotBeLocked()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            root.IsSelected = true;
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(actions.Acquired);
            WaitForChild(root, "asset.txt").IsSelected = true;
            repositoryInfo.RepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Empty(actions.Acquired);
            Assert.Contains("Lock acquisition failed", Control<TextBlock>(window, "StatusText").Text);
            Assert.False(Control<Button>(window, "ReleaseMyLockButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void BusyMutationDisablesReleaseAndProjectSwitching()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var completion = new TaskCompletionSource();
            actions.Acquire = (_, _) => completion.Task;
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            WaitForChild(root, "asset.txt").IsSelected = true;
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<Button>(window, "RefreshMyLocksButton").IsEnabled);
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Single(actions.Acquired);
            completion.SetResult();
            PumpDispatcher();
            Assert.True(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void FailedReleaseKeepsLockAndReportsErrorWithoutRetrying()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var entry = new MyFileLock(Path.Combine(project.LocalPath, "asset.txt"), "asset.txt", "other-branch", "me");
            actions.Entries = [entry];
            actions.Release = (_, _) => Task.FromException(new IOException("release denied"));
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            ShowMyLocks(window);
            var list = Control<ListBox>(window, "MyLocksList");
            list.SelectedIndex = 0;
            Click(window, "ReleaseMyLockButton");
            Assert.Equal((project.LocalPath, entry), Assert.Single(actions.Released));
            Assert.Equal(entry, Assert.Single(list.Items.Cast<MyFileLock>()));
            Assert.Contains("release denied", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void PendingProjectClearsPreviousListAndDoesNotQueryServer()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var pending = CreateProject("pending") with { RepositoryId = null, CloneState = ProjectCloneState.Pending };
            actions.Entries = [new MyFileLock(Path.Combine(first.LocalPath, "asset.txt"), "asset.txt", "branch", "me")];
            var window = CreateWindow(first, pending);
            window.Show();
            SelectProject(window, 0);
            ShowMyLocks(window);
            Assert.Single(Control<ListBox>(window, "MyLocksList").Items);
            SelectProject(window, 1);
            Assert.Empty(Control<ListBox>(window, "MyLocksList").Items);
            Assert.Equal([first.LocalPath], actions.Queried);
            Assert.Contains("Available after initial download", Control<TextBox>(window, "MyLocksStatusText").Text);
            Assert.False(Control<Button>(window, "RefreshMyLocksButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void LateQueryAfterClosingCannotUpdateList()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var delayed = new TaskCompletionSource<IReadOnlyList<MyFileLock>>();
            actions.Query = _ => delayed.Task;
            var window = CreateWindow(project);
            SelectProject(window, 0);
            ShowMyLocks(window);
            var before = Control<TextBox>(window, "MyLocksStatusText").Text;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            PumpDispatcher();
            Assert.True(closed);
            delayed.SetResult([new MyFileLock(Path.Combine(project.LocalPath, "asset.txt"), "asset.txt", "branch", "me")]);
            PumpDispatcher();
            Assert.Empty(Control<ListBox>(window, "MyLocksList").Items);
            Assert.Equal(before, Control<TextBox>(window, "MyLocksStatusText").Text);
        });
    }

    [Fact]
    public void RightClickSelectsTargetInsteadOfPreviouslySelectedFile()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            File.WriteAllText(Path.Combine(project.LocalPath, "second.txt"), "second");
            var window = CreateWindow(project);
            window.Show();
            SelectProject(window, 0);
            var tree = Control<TreeView>(window, "FileTree");
            var root = Assert.IsType<TreeViewItem>(tree.Items[0]);
            WaitForChild(root, "asset.txt").IsSelected = true;
            var second = WaitForChild(root, "second.txt");
            second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseDownEvent,
                Source = second
            });
            Assert.Same(second, tree.SelectedItem);
            AcquireMenu(window).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal((project.LocalPath, Path.Combine(project.LocalPath, "second.txt")), Assert.Single(actions.Acquired));
            window.Close();
        });
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        File.WriteAllText(Path.Combine(path, "asset.txt"), "asset");
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private WorkspaceWindow CreateWindow(params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => confirm,
            repositoryInfo: repositoryInfo, branchService: new FakeBranchService(),
            fileStatusService: new FakeFileStatusService(), fileLockService: icons, fileLockActionsService: actions,
            pushService: new FakePushStatusService());
    }

    private static MenuItem AcquireMenu(WorkspaceWindow window) =>
        Assert.IsType<MenuItem>(Control<TreeView>(window, "FileTree").ContextMenu.Items[0]);

    private static void SelectProject(WorkspaceWindow window, int index)
    {
        Control<ListBox>(window, "ProjectList").SelectedIndex = index;
        Click(window, "SwitchProjectButton");
        PumpDispatcher();
    }

    private static void ShowMyLocks(WorkspaceWindow window)
    {
        Control<TabControl>(window, "WorkspaceTabs").SelectedItem = Control<TabItem>(window, "StageTab");
        Click(window, "SwitchChangesViewButton");
    }

    private static TreeViewItem WaitForChild(TreeViewItem parent, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
            if (parent.Items.OfType<TreeViewItem>().FirstOrDefault(item => Equals(item.Header, name)) is { } child) return child;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Tree item not found: {name}");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FakeActions : IFileLockActionsService
    {
        public IReadOnlyList<MyFileLock> Entries { get; set; } = [];
        public List<string> Queried { get; } = [];
        public List<(string, string)> Acquired { get; } = [];
        public List<(string, MyFileLock)> Released { get; } = [];
        public Func<string, Task<IReadOnlyList<MyFileLock>>>? Query { get; set; }
        public Func<string, string, Task> Acquire { get; set; } = (_, _) => Task.CompletedTask;
        public Func<string, MyFileLock, Task> Release { get; set; } = (_, _) => Task.CompletedTask;

        public Task<IReadOnlyList<MyFileLock>> QueryMineAsync(string path)
        {
            Queried.Add(path);
            return Query?.Invoke(path) ?? Task.FromResult(Entries);
        }
        public Task AcquireAsync(string path, string file)
        {
            Acquired.Add((path, file));
            return Acquire(path, file);
        }
        public Task ReleaseAsync(string path, MyFileLock fileLock)
        {
            Released.Add((path, fileLock));
            return Release(path, fileLock);
        }
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl)
            => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
            => throw new InvalidOperationException("Unexpected clone");
    }

    private sealed class FakeBranchService : IBranchService
    {
        public Task<BranchListResult> ListAsync(string path) => Task.FromResult(
            new BranchListResult([new BranchEntry("11111111111111111111111111111111", "main", true, true, true)], true));
        public Task SwitchAsync(string path, BranchEntry branch)
            => throw new InvalidOperationException("Unexpected branch switch");
    }
}
