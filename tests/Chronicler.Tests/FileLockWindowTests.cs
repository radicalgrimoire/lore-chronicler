using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class FileLockWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void LockedFilesShowOwnersWithoutChangeHighlightAndRefreshClearsLock()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            Directory.CreateDirectory(Path.Combine(project.LocalPath, "folder"));
            File.WriteAllText(Path.Combine(project.LocalPath, "folder", "asset.txt"), "asset");
            var locked = true;
            var service = new FakeFileLockService
            {
                Query = path => Task.FromResult(locked
                    ? Result(path, "folder\\asset.txt", new("id-1", "Alice", "branch-1"), new("id-2", "Bob", "branch-2"))
                    : Result(path))
            };
            var statuses = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                    new Dictionary<string, FileChangeKind> { ["folder\\asset.txt"] = FileChangeKind.Modified }))
            };
            var window = CreateWindow(service, statuses, project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var folder = WaitForChild(root, "folder");
            folder.IsExpanded = true;
            var file = WaitForChild(folder, "asset.txt");
            Assert.Empty(AutomationProperties.GetHelpText(folder));
            Assert.NotEqual("変更あり", AutomationProperties.GetItemStatus(file));
            var icon = FindText(file, "🔒");
            Assert.NotNull(icon);
            Assert.Equal(Visibility.Visible, icon.Visibility);
            Assert.Contains("Alice", Assert.IsType<string>(icon.ToolTip));
            Assert.Contains("Bob", Assert.IsType<string>(icon.ToolTip));
            Assert.Contains("branch-1", Assert.IsType<string>(icon.ToolTip));
            file.IsSelected = true;
            PumpDispatcher();
            Assert.Equal(Visibility.Visible, icon.Visibility);
            var header = Assert.IsType<TextBlock>(FindText(file, "asset.txt"));
            Assert.True(header.Background is null || header.Background is SolidColorBrush { Color: var color } && color == Colors.Transparent);

            locked = false;
            Click(window, "RefreshTreeButton");
            root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            folder = WaitForChild(root, "folder");
            folder.IsExpanded = true;
            file = WaitForChild(folder, "asset.txt");
            icon = FindText(file, "🔒");
            Assert.NotNull(icon);
            Assert.Equal(Visibility.Collapsed, icon.Visibility);
            Assert.NotEqual("変更あり", AutomationProperties.GetItemStatus(file));
            Assert.Equal([project.LocalPath, project.LocalPath], service.Paths);
            window.Close();
        });
    }

    [Fact]
    public void LockFailureAndUnresolvedNamesAreReportedAndCanRetry()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            File.WriteAllText(Path.Combine(project.LocalPath, "file.txt"), "file");
            var service = new FakeFileLockService
            {
                Query = _ => Task.FromException<FileLockResult>(new IOException("lock query failed"))
            };
            var window = CreateWindow(service, new FakeFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            Assert.Contains("lock query failed", Control<TextBox>(window, "LockStatusText").Text);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var file = WaitForChild(root, "file.txt");
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(FindText(file, "🔒")).Visibility);
            service.Query = path => Task.FromResult(Result(path, "file.txt", new FileLockOwner("user-id", null, "branch")) with
                { Warning = "取得者名を確認できません。" });
            Click(window, "RefreshTreeButton");
            root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            file = WaitForChild(root, "file.txt");
            var icon = Assert.IsType<TextBlock>(FindText(file, "🔒"));
            Assert.Equal(Visibility.Visible, icon.Visibility);
            Assert.Contains("user-id", Assert.IsType<string>(icon.ToolTip));
            Assert.Contains("取得者名を確認できません", Control<TextBox>(window, "LockStatusText").Text);
            service.Query = _ => Task.FromException<FileLockResult>(new IOException("retry failed"));
            Click(window, "RefreshTreeButton");
            root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            file = WaitForChild(root, "file.txt");
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(FindText(file, "🔒")).Visibility);
            Assert.Contains("retry failed", Control<TextBox>(window, "LockStatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void DelayedLockResultsCannotOverwriteAnotherProject()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            File.WriteAllText(Path.Combine(first.LocalPath, "file.txt"), "a");
            File.WriteAllText(Path.Combine(second.LocalPath, "file.txt"), "b");
            var delayed = new TaskCompletionSource<FileLockResult>();
            var service = new FakeFileLockService
            {
                Query = path => path == first.LocalPath ? delayed.Task :
                    Task.FromResult(Result(path, "file.txt", new FileLockOwner("b", "Bob", "branch")))
            };
            var window = CreateWindow(service, new FakeFileStatusService(), first, second);
            window.Show();
            SelectProject(window, 0);
            Assert.Contains("確認しています", Control<TextBox>(window, "LockStatusText").Text);
            SelectProject(window, 1);
            delayed.SetResult(Result(first.LocalPath, "file.txt", new FileLockOwner("a", "Alice", "branch")));
            PumpDispatcher();
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var file = WaitForChild(root, "file.txt");
            var icon = Assert.IsType<TextBlock>(FindText(file, "🔒"));
            Assert.Contains("Bob", Assert.IsType<string>(icon.ToolTip));
            Assert.DoesNotContain("Alice", Assert.IsType<string>(icon.ToolTip));
            window.Close();
        });
    }

    [Fact]
    public void DelayedResultCannotOverwriteNewerRefresh()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            File.WriteAllText(Path.Combine(project.LocalPath, "file.txt"), "file");
            var delayed = new TaskCompletionSource<FileLockResult>();
            var service = new FakeFileLockService();
            service.Query = path => service.Paths.Count == 1 ? delayed.Task :
                Task.FromResult(Result(path, "file.txt", new FileLockOwner("b", "Bob", "branch")));
            var window = CreateWindow(service, new FakeFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            Click(window, "RefreshTreeButton");
            delayed.SetResult(Result(project.LocalPath, "file.txt", new FileLockOwner("a", "Alice", "branch")));
            PumpDispatcher();
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var file = WaitForChild(root, "file.txt");
            var icon = Assert.IsType<TextBlock>(FindText(file, "🔒"));
            Assert.Contains("Bob", Assert.IsType<string>(icon.ToolTip));
            Assert.DoesNotContain("Alice", Assert.IsType<string>(icon.ToolTip));
            window.Close();
        });
    }

    private static FileLockResult Result(string root, string? path = null, params FileLockOwner[] owners)
    {
        var files = new Dictionary<string, IReadOnlyList<FileLockOwner>>(StringComparer.OrdinalIgnoreCase);
        if (path is not null) files.Add(Path.Combine(root, path), owners);
        return new FileLockResult(files, null);
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private WorkspaceWindow CreateWindow(IFileLockService locks, IFileStatusService statuses, params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
            repositoryInfo: repositoryInfo, fileStatusService: statuses, fileLockService: locks);
    }

    private static void SelectProject(WorkspaceWindow window, int index)
    {
        Control<ListBox>(window, "ProjectList").SelectedIndex = index;
        Click(window, "SwitchProjectButton");
        PumpDispatcher();
    }

    private static TreeViewItem WaitForChild(TreeViewItem parent, string header)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
            var item = parent.Items.OfType<TreeViewItem>().FirstOrDefault(child => Equals(child.Header, header));
            if (item is not null)
            {
                item.UpdateLayout();
                return item;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Tree item not found: {header}");
    }

    private static TextBlock? FindText(DependencyObject parent, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem) continue;
            if (child is TextBlock block && block.Text == text) return block;
            var found = FindText(child, text);
            if (found is not null) return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
