using System.IO;
using System.Windows.Controls;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");

    [Fact]
    public void LocalTreeShowsFilesAndDirectoriesButNotLoreMetadata()
    {
        var root = Path.Combine(directory, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".lore"));
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        File.WriteAllText(Path.Combine(root, "z.txt"), "z");
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        var entries = LocalFileTree.ReadChildren(root, root, CancellationToken.None);
        Assert.Equal(["folder", "a.txt", "z.txt"], entries.Select(entry => entry.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.False(entries[1].IsDirectory);
        Assert.Throws<OperationCanceledException>(() => LocalFileTree.ReadChildren(root, root, new CancellationToken(true)));
        Assert.Throws<DirectoryNotFoundException>(() => LocalFileTree.ReadChildren(root, Path.Combine(root, "missing"), CancellationToken.None));
    }

    [Fact]
    public void SwitchingProjectsUpdatesTreeAndRemovalKeepsFiles()
    {
        RunSta(() =>
        {
            var firstPath = CreateRepository("first", "first.txt");
            var secondPath = CreateRepository("second", "second.txt");
            Directory.CreateDirectory(Path.Combine(secondPath, "Assets"));
            File.WriteAllText(Path.Combine(secondPath, "Assets", "asset.txt"), "asset");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var first = new ProjectProfile(Guid.NewGuid(), "First", server.Id, server.RemoteUrl, "repo1", firstPath);
            var second = new ProjectProfile(Guid.NewGuid(), "Second", server.Id, server.RemoteUrl, "repo2", secondPath);
            var other = new ProjectProfile(Guid.NewGuid(), "Other", Guid.NewGuid(), server.RemoteUrl, "repo3", Path.Combine(directory, "other"));
            store.Save([first, second, other]);
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
            var list = Control<ListBox>(window, "ProjectList");
            var tree = Control<TreeView>(window, "FileTree");
            var refresh = Control<Button>(window, "RefreshTreeButton");
            var getLatest = Control<Button>(window, "GetLatestButton");
            var submit = Control<Button>(window, "SubmitButton");
            Assert.Equal(3, list.Items.Count);
            Assert.Contains("選択されていません", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Empty(tree.Items);
            Assert.False(refresh.IsEnabled);
            Assert.False(getLatest.IsEnabled);
            Assert.False(submit.IsEnabled);

            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal("First", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("First", Assert.IsType<TreeViewItem>(tree.Items[0]).Header);
            Assert.True(refresh.IsEnabled);
            Assert.True(getLatest.IsEnabled);
            Assert.False(submit.IsEnabled);

            list.SelectedIndex = 1;
            Click(window, "SwitchProjectButton");
            Assert.Equal("Second", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("Second", Assert.IsType<TreeViewItem>(tree.Items[0]).Header);
            WaitForChildren(tree, "second.txt");
            var root = Assert.IsType<TreeViewItem>(tree.Items[0]);
            Assert.DoesNotContain(root.Items.Cast<TreeViewItem>(),
                item => Equals(item.Header, ".lore"));
            var assets = Assert.Single(root.Items.OfType<TreeViewItem>(), item => Equals(item.Header, "Assets"));
            assets.IsExpanded = true;
            WaitForChild(assets, "asset.txt");
            File.WriteAllText(Path.Combine(secondPath, "new.txt"), "new");
            Click(window, "RefreshTreeButton");
            WaitForChildren(tree, "new.txt");

            Click(window, "RemoveProjectButton");
            Assert.Contains("選択されていません", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Empty(tree.Items);
            Assert.False(refresh.IsEnabled);
            Assert.True(File.Exists(Path.Combine(secondPath, "second.txt")));
            Assert.Equal(2, new ProjectSettingsStore(Path.Combine(directory, "projects.json")).Load().Count);
            window.Close();
        });
    }

    [Fact]
    public void LatestIsConfirmedLocksControlsAndRefreshesLocalTree()
    {
        RunSta(() =>
        {
            var path = CreateRepository("latest", "existing.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Latest", server.Id, server.RemoteUrl, "repo", path)]);
            var service = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, service, new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            Assert.Equal(path, service.Path);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.False(Control<Button>(window, "RefreshTreeButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<ListBox>(window, "ProjectList").IsEnabled);
            window.Close();
            Assert.Contains("処理中", Control<TextBlock>(window, "StatusText").Text);
            File.WriteAllText(Path.Combine(path, "downloaded.txt"), "new");
            service.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            Assert.True(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.Contains("更新しました", Control<TextBlock>(window, "StatusText").Text);
            WaitForChildren(Control<TreeView>(window, "FileTree"), "downloaded.txt");
            window.Close();
        });
    }

    [Fact]
    public void LatestReportsFailureAndRefreshesPartialChanges()
    {
        RunSta(() =>
        {
            var path = CreateRepository("failed-latest", "existing.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Latest", server.Id, server.RemoteUrl, "repo", path)]);
            var service = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, service, new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            File.WriteAllText(Path.Combine(path, "partial.txt"), "partial");
            service.Completion.SetException(new LatestSyncException("履歴が分岐しています。"));
            PumpDispatcher();
            Assert.Contains("分岐", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(Control<Button>(window, "GetLatestButton").IsEnabled);
            WaitForChildren(Control<TreeView>(window, "FileTree"), "partial.txt");
            window.Close();
        });
    }

    [Fact]
    public void DecliningLatestDoesNotCallService()
    {
        RunSta(() =>
        {
            var path = CreateRepository("declined", "existing.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Latest", server.Id, server.RemoteUrl, "repo", path)]);
            var service = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => false, service, new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            Assert.Null(service.Path);
            window.Close();
        });
    }

    [Fact]
    public void MissingProjectDoesNotReplaceCurrentProject()
    {
        RunSta(() =>
        {
            var firstPath = CreateRepository("first", "first.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var first = new ProjectProfile(Guid.NewGuid(), "First", server.Id, server.RemoteUrl, "repo1", firstPath);
            var missing = new ProjectProfile(Guid.NewGuid(), "Missing", server.Id, server.RemoteUrl, "repo2", Path.Combine(directory, "missing"));
            store.Save([first, missing]);
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: new FakeProjectRepositoryInfo());
            var list = Control<ListBox>(window, "ProjectList");
            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            list.SelectedIndex = 1;
            Click(window, "SwitchProjectButton");
            Assert.Equal("First", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Contains("確認できません", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void ExistingCloneCanBeRenamedWithoutModifyingFiles()
    {
        RunSta(() =>
        {
            var path = CreateRepository("imported", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            var info = new FakeProjectRepositoryInfo { RemoteUrl = "lore://other.example:41337" };
            store.Load();
            var project = new ProjectProfile(Guid.NewGuid(), "repo", server.Id, info.RemoteUrl, "repo", path, info.RepositoryId);
            store.Save([project]);
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: info, selectProjectName: _ => " New name ");
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal(0, info.RemoteCalls);
            Click(window, "RenameProjectButton");
            var renamed = Assert.Single(new ProjectSettingsStore(Path.Combine(directory, "projects.json")).Load());
            Assert.Equal("New name", renamed.Name);
            Assert.Equal(project.RepositoryId, renamed.RepositoryId);
            Assert.Equal("test", File.ReadAllText(Path.Combine(path, "keep.txt")));
            window.Close();
        });
    }

    [Fact]
    public void ProjectsFromDifferentServersAreListedAndUseTheirOwnServer()
    {
        RunSta(() =>
        {
            var path = CreateRepository("other-server", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var otherUrl = "lore://other.example:41337";
            var info = new FakeProjectRepositoryInfo { RemoteUrl = otherUrl };
            store.Save([new ProjectProfile(Guid.NewGuid(), "Other", Guid.NewGuid(), otherUrl, "repo", path, info.RepositoryId)]);
            string? authUrl = null;
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: info,
                authenticate: url => { authUrl = url; return true; });
            var list = Control<ListBox>(window, "ProjectList");
            Assert.Single(list.Items);
            list.SelectedIndex = 0;
            Click(window, "LoginButton");
            Assert.Equal(otherUrl, authUrl);
            Click(window, "SwitchProjectButton");
            Assert.Equal("Other", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Contains(otherUrl, Control<TextBlock>(window, "ServerText").Text);
            Assert.Equal(0, info.RemoteCalls);
            window.Close();
        });
    }

    [Fact]
    public void PendingProjectClonesOnlyWhenRequestedAndThenUpdates()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "clone");
            var settingsPath = Path.Combine(directory, "projects.json");
            var store = new ProjectSettingsStore(settingsPath);
            store.Load();
            var project = new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path,
                CloneState: ProjectCloneState.Pending);
            store.Save([project]);
            var clone = new FakeCloneService();
            var latest = new FakeLatestService();
            var info = new FakeProjectRepositoryInfo();
            var window = new WorkspaceWindow(server, store, clone, (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.False(Directory.Exists(path));
            Assert.Null(clone.Path);
            Click(window, "GetLatestButton");
            Assert.Equal(path, clone.Path);
            Assert.Equal(ProjectCloneState.Started, Assert.Single(new ProjectSettingsStore(settingsPath).Load()).CloneState);
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            clone.Completion.SetResult();
            PumpDispatcher();
            var saved = Assert.Single(new ProjectSettingsStore(settingsPath).Load());
            Assert.Equal(ProjectCloneState.Ready, saved.CloneState);
            Assert.Equal(info.RepositoryId, saved.RepositoryId);
            Click(window, "GetLatestButton");
            Assert.Equal(path, latest.Path);
            latest.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            window.Close();
        });
    }

    [Fact]
    public void FailedCloneRemainsStartedAndCannotBeRetriedAutomatically()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "failed");
            var settingsPath = Path.Combine(directory, "projects.json");
            var store = new ProjectSettingsStore(settingsPath);
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path,
                CloneState: ProjectCloneState.Pending)]);
            var clone = new FakeCloneService();
            var window = new WorkspaceWindow(server, store, clone, (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            File.WriteAllText(Path.Combine(path, "partial"), "keep");
            clone.Completion.SetException(new LoreVcs.LoreError(42));
            PumpDispatcher();
            Assert.Equal(ProjectCloneState.Started, Assert.Single(new ProjectSettingsStore(settingsPath).Load()).CloneState);
            Click(window, "GetLatestButton");
            Assert.Equal(1, clone.Calls);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "partial")));
            window.Close();
        });
    }

    [Fact]
    public void PendingFolderThatBecomesNonEmptyDoesNotStartClone()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "occupied");
            Directory.CreateDirectory(path);
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path,
                CloneState: ProjectCloneState.Pending)]);
            var clone = new FakeCloneService();
            var window = new WorkspaceWindow(server, store, clone, (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            File.WriteAllText(Path.Combine(path, "keep"), "keep");
            Click(window, "GetLatestButton");
            Assert.Null(clone.Path);
            Assert.Equal(ProjectCloneState.Pending, Assert.Single(new ProjectSettingsStore(Path.Combine(directory, "projects.json")).Load()).CloneState);
            Assert.Contains("空ではありません", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void MissingLoreOnPreviouslyClonedProjectNeverStartsCloneOrSync()
    {
        RunSta(() =>
        {
            var path = CreateRepository("removed-lore", "keep.txt");
            var lorePath = Path.Combine(path, ".lore");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var clone = new FakeCloneService();
            var latest = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, clone, (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Directory.Delete(lorePath);
            Click(window, "GetLatestButton");
            Assert.Null(clone.Path);
            Assert.Null(latest.Path);
            Assert.Contains("cloneしてください", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("test", File.ReadAllText(Path.Combine(path, "keep.txt")));
            window.Close();
        });
    }

    [Fact]
    public void ChangedLocalRepositoryBlocksLatestWithoutServerAccess()
    {
        RunSta(() =>
        {
            var path = CreateRepository("changed", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var latest = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            info.RepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            Click(window, "GetLatestButton");
            Assert.Null(latest.Path);
            Assert.Equal(0, info.RemoteCalls);
            Assert.Contains("一致しません", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void UnavailableServerBlocksLatestWithoutStartingSync()
    {
        RunSta(() =>
        {
            var path = CreateRepository("unavailable", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var latest = new FakeLatestService();
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            info.RemoteError = new InvalidOperationException("サーバーに接続できません。");
            Click(window, "GetLatestButton");
            Assert.Null(latest.Path);
            Assert.Contains("サーバーに接続できません", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("test", File.ReadAllText(Path.Combine(path, "keep.txt")));
            window.Close();
        });
    }

    [Fact]
    public void LegacyProjectMigrationRequiresMatchingServerRepository()
    {
        RunSta(() =>
        {
            var path = CreateRepository("legacy", "keep.txt");
            var settingsPath = Path.Combine(directory, "projects.json");
            var store = new ProjectSettingsStore(settingsPath);
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Legacy", server.Id, server.RemoteUrl, "repo", path)]);
            var info = new FakeProjectRepositoryInfo { ServerRepositoryId = "ABCDEF1234567890ABCDEF1234567890" };
            var window = new WorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Null(Assert.Single(new ProjectSettingsStore(settingsPath).Load()).RepositoryId);
            Assert.Contains("一致しません", Control<TextBlock>(window, "StatusText").Text);
            info.ServerRepositoryId = null;
            Click(window, "SwitchProjectButton");
            Assert.Equal(info.RepositoryId, Assert.Single(new ProjectSettingsStore(settingsPath).Load()).RepositoryId);
            Assert.Equal("Legacy", Control<TextBlock>(window, "ActiveProjectText").Text);
            window.Close();
        });
    }

    private static void WaitForChildren(TreeView tree, string expected)
    {
        WaitForChild(Assert.IsType<TreeViewItem>(tree.Items[0]), expected);
    }

    private static void WaitForChild(TreeViewItem root, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            PumpDispatcher();
            if (root.Items.OfType<TreeViewItem>().Any(item => Equals(item.Header, expected)))
            {
                return;
            }
            Thread.Sleep(10);
        }
        Assert.Fail("Tree did not load expected file");
    }

    private string CreateRepository(string name, string file)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        File.WriteAllText(Path.Combine(path, file), "test");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
            => throw new InvalidOperationException("Unexpected clone");
    }

    private sealed class FakeCloneService : IRepositoryService
    {
        public string? Path { get; private set; }
        public int Calls { get; private set; }
        public TaskCompletionSource Completion { get; } = new();
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException();
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
        {
            Path = path;
            Calls++;
            return Completion.Task;
        }
    }

    private sealed class FakeLatestService : ILatestService
    {
        public string? Path { get; private set; }
        public TaskCompletionSource<LatestStatus> Completion { get; } = new();

        public Task<LatestStatus> GetLatestAsync(string path, IProgress<string> progress)
        {
            Path = path;
            return Completion.Task;
        }
    }
}
