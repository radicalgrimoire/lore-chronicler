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

    private static WorkspaceWindow CreateWorkspaceWindow(ServerProfile server, ProjectSettingsStore store,
        IRepositoryService repositoryService, Func<string, string, bool>? confirm = null,
        ILatestService? latestService = null, IProjectRepositoryInfo? repositoryInfo = null,
        Func<string, string?>? selectProjectName = null, Func<string, bool>? authenticate = null,
        IBranchService? branchService = null, IRevisionHistoryService? historyService = null,
        IFileStatusService? fileStatusService = null, IFileLockService? fileLockService = null) =>
        new(server, store, repositoryService, confirm, latestService, repositoryInfo, selectProjectName,
            authenticate, branchService ?? new FakeBranchService(), historyService, fileStatusService ?? new FakeFileStatusService(),
            fileLockService ?? new FakeFileLockService());

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
    public void ServerAddressIsBlankWhenProjectSettingsCannotBeRead()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "projects.json");
            File.WriteAllText(path, "{invalid json");
            var window = CreateWorkspaceWindow(server, new ProjectSettingsStore(path), new UnusedRepositoryService());
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.NotEmpty(Control<TextBlock>(window, "StatusText").Text);
            Assert.False(Control<Button>(window, "CreateProjectButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void WorkspaceTabsPreserveProjectSelectionAndOperations()
    {
        RunSta(() =>
        {
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var project = new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo",
                Path.Combine(directory, "clone"), CloneState: ProjectCloneState.Pending);
            store.Save([project]);
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: new FakeProjectRepositoryInfo(), selectProjectName: _ => "Renamed");
            window.Show();
            var tabs = Control<TabControl>(window, "WorkspaceTabs");
            var projects = Control<TabItem>(window, "ProjectsTab");
            var stage = Control<TabItem>(window, "StageTab");
            var log = Control<TabItem>(window, "UpdateLogTab");
            var branchesTab = Control<TabItem>(window, "BranchesTab");
            var list = Control<ListBox>(window, "ProjectList");
            var login = Control<Button>(window, "LoginButton");
            var manageServers = Control<Button>(window, "ManageServersButton");
            var toolbar = Assert.IsType<WrapPanel>(login.Parent);
            Assert.Same(toolbar, manageServers.Parent);
            Assert.Equal(toolbar.Children.IndexOf(login) + 1, toolbar.Children.IndexOf(manageServers));
            Assert.True(manageServers.IsVisible);
            Assert.Equal(4, tabs.Items.Count);
            Assert.Same(projects, tabs.SelectedItem);
            Assert.Equal("Projects", projects.Header);
            Assert.Equal("Changes", stage.Header);
            Assert.Equal("Update log", log.Header);
            Assert.Equal("Branches", branchesTab.Header);
            Assert.Equal("Get latest", Control<Button>(window, "GetLatestButton").Content);
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("Branch:", Control<TextBlock>(window, "CurrentBranchText").Text);
            list.SelectedIndex = 0;
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            Click(window, "SwitchProjectButton");
            Assert.Equal($"Project ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("Branch:", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Contains("Get latest", Control<TextBlock>(window, "StatusText").Text);
            var status = Control<TextBlock>(window, "StatusText").Text;

            tabs.SelectedItem = stage;
            PumpDispatcher();
            Assert.True(Assert.IsType<Grid>(stage.Content).IsVisible);
            Assert.True(manageServers.IsVisible);
            Assert.Contains("Available after initial download", Control<TextBox>(window, "StageStatusText").Text);
            tabs.SelectedItem = log;
            PumpDispatcher();
            Assert.True(Assert.IsType<Grid>(log.Content).IsVisible);
            Assert.Contains("initial download", Control<TextBox>(window, "HistoryStatus").Text);
            Assert.Equal($"Project ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal(status, Control<TextBlock>(window, "StatusText").Text);

            tabs.SelectedItem = projects;
            PumpDispatcher();
            Assert.True(list.IsVisible);
            Assert.Equal(project, list.SelectedItem);
            Assert.True(Control<Button>(window, "CreateProjectButton").IsEnabled);
            Assert.True(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Click(window, "RenameProjectButton");
            Assert.Equal("Renamed", Assert.IsType<ProjectProfile>(list.SelectedItem).Name);
            Assert.Equal($"Renamed ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("Renamed", Assert.Single(new ProjectSettingsStore(Path.Combine(directory, "projects.json")).Load()).Name);
            window.Close();
        });
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
            var list = Control<ListBox>(window, "ProjectList");
            var tree = Control<TreeView>(window, "FileTree");
            var refresh = Control<Button>(window, "RefreshTreeButton");
            var getLatest = Control<Button>(window, "GetLatestButton");
            var push = Control<Button>(window, "PushButton");
            Assert.Equal(3, list.Items.Count);
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Empty(tree.Items);
            Assert.False(refresh.IsEnabled);
            Assert.False(getLatest.IsEnabled);
            Assert.False(push.IsEnabled);

            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal($"First ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("first (local)", Assert.IsType<TreeViewItem>(tree.Items[0]).Header);
            Assert.True(refresh.IsEnabled);
            Assert.True(getLatest.IsEnabled);
            Assert.True(push.IsEnabled);

            list.SelectedIndex = 1;
            Click(window, "SwitchProjectButton");
            Assert.Equal($"Second ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("second (local)", Assert.IsType<TreeViewItem>(tree.Items[0]).Header);
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
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal("Branch:", Control<TextBlock>(window, "CurrentBranchText").Text);
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
            var fileStatus = new FakeFileStatusService();
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, service,
                new FakeProjectRepositoryInfo(), branchService: branches, fileStatusService: fileStatus);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal("Branch: main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal("main", Assert.IsType<BranchEntry>(Control<ComboBox>(window, "BranchList").SelectedItem).Name);
            Click(window, "GetLatestButton");
            Assert.Equal(path, service.Path);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.False(Control<Button>(window, "RefreshTreeButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<ListBox>(window, "ProjectList").IsEnabled);
            window.Close();
            Assert.Contains("operation is in progress", Control<TextBlock>(window, "StatusText").Text);
            File.WriteAllText(Path.Combine(path, "downloaded.txt"), "new");
            service.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            Assert.True(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.Contains("Branch \"main\" was updated", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("Branch: main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal("main", Assert.IsType<BranchEntry>(Control<ComboBox>(window, "BranchList").SelectedItem).Name);
            Assert.Equal(2, branches.ListCalls);
            WaitForChildren(Control<TreeView>(window, "FileTree"), "downloaded.txt");
            Assert.Equal([path, path], fileStatus.Paths);
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
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, service,
                new FakeProjectRepositoryInfo(), branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            branches.Completion.SetResult();
            File.WriteAllText(Path.Combine(path, "partial.txt"), "partial");
            service.Completion.SetException(new LatestSyncException("Update failed."));
            PumpDispatcher();
            Assert.Contains("Update failed", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.Equal("Branch: feature", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal("feature", Assert.IsType<BranchEntry>(Control<ComboBox>(window, "BranchList").SelectedItem).Name);
            Assert.Equal(2, branches.ListCalls);
            WaitForChildren(Control<TreeView>(window, "FileTree"), "partial.txt");
            window.Close();
        });
    }

    [Fact]
    public void LatestReportsBranchRefreshFailureWithoutHidingUpdateResult()
    {
        RunSta(() =>
        {
            var path = CreateRepository("latest-branch-error", "existing.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Latest", server.Id, server.RemoteUrl, "repo", path)]);
            var latest = new FakeLatestService();
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                latest, new FakeProjectRepositoryInfo(), branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            branches.ListFailure = new IOException("branch refresh failed");
            latest.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            Assert.Equal("Branch:", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Null(Control<ComboBox>(window, "BranchList").SelectedItem);
            Assert.Contains("Branch \"main\" was updated", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("branch refresh failed", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => false, service, new FakeProjectRepositoryInfo());
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: new FakeProjectRepositoryInfo());
            var list = Control<ListBox>(window, "ProjectList");
            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            list.SelectedIndex = 1;
            Click(window, "SwitchProjectButton");
            Assert.Equal($"First ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Contains("Could not verify the local Lore workspace", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: info,
                authenticate: url => { authUrl = url; return true; });
            var list = Control<ListBox>(window, "ProjectList");
            Assert.Single(list.Items);
            list.SelectedIndex = 0;
            Click(window, "LoginButton");
            Assert.Equal(otherUrl, authUrl);
            Click(window, "SwitchProjectButton");
            Assert.Equal($"Other ({otherUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
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
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, clone, (_, _) => true, latest, info,
                branchService: branches);
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
            Assert.Equal("Project", saved.Name);
            Assert.Equal($"Project ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            Assert.Equal(info.RepositoryId, saved.RepositoryId);
            Assert.Equal("Branch: main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal(1, branches.ListCalls);
            Click(window, "GetLatestButton");
            Assert.Equal(path, latest.Path);
            latest.Completion.SetResult(new LatestStatus("main", "abc", "abc", true, true, true, false, false));
            PumpDispatcher();
            window.Close();
        });
    }

    [Fact]
    public void LatestAutomaticallyAuthenticatesAndRetriesOnce()
    {
        RunSta(() =>
        {
            var path = CreateRepository("latest-auth", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var authorizationChecks = 0;
            var latest = new FakeLatestService
            {
                EnsureRemoteAuthorized = _ => ++authorizationChecks == 1
                    ? Task.FromException(new RemoteAuthenticationRequiredException("Authentication required."))
                    : Task.CompletedTask,
                GetLatest = (_, _) => Task.FromResult(new LatestStatus("main", "local", "remote", true, true, true, false, true))
            };
            var authenticationCalls = 0;
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                latestService: latest, repositoryInfo: info, authenticate: _ =>
                {
                    authenticationCalls++;
                    return true;
                });
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "GetLatestButton");
            Assert.Equal(1, authenticationCalls);
            Assert.Equal(2, authorizationChecks);
            Assert.Equal(1, latest.GetLatestCalls);
            Assert.Contains("Branch \"main\" was updated", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, clone, (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
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
            var window = CreateWorkspaceWindow(server, store, clone, (_, _) => true, repositoryInfo: new FakeProjectRepositoryInfo());
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            File.WriteAllText(Path.Combine(path, "keep"), "keep");
            Click(window, "GetLatestButton");
            Assert.Null(clone.Path);
            Assert.Equal(ProjectCloneState.Pending, Assert.Single(new ProjectSettingsStore(Path.Combine(directory, "projects.json")).Load()).CloneState);
            Assert.Contains("Could not start initial download", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, clone, (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Directory.Delete(lorePath);
            Click(window, "GetLatestButton");
            Assert.Null(clone.Path);
            Assert.Null(latest.Path);
            Assert.Contains("Could not verify the repository", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            info.RepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            Click(window, "GetLatestButton");
            Assert.Null(latest.Path);
            Assert.Equal(0, info.RemoteCalls);
            Assert.Contains("Could not verify the repository", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true, latest, info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            info.RemoteError = new InvalidOperationException("Remote verification failed.");
            Click(window, "GetLatestButton");
            Assert.Null(latest.Path);
            Assert.Contains("Remote verification failed", Control<TextBlock>(window, "StatusText").Text);
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
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: info);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Null(Assert.Single(new ProjectSettingsStore(settingsPath).Load()).RepositoryId);
            Assert.Contains("Could not verify the local Lore workspace", Control<TextBlock>(window, "StatusText").Text);
            info.ServerRepositoryId = null;
            Click(window, "SwitchProjectButton");
            Assert.Equal(info.RepositoryId, Assert.Single(new ProjectSettingsStore(settingsPath).Load()).RepositoryId);
            Assert.Equal($"Legacy ({server.RemoteUrl})", Control<TextBlock>(window, "ActiveProjectText").Text);
            window.Close();
        });
    }

    [Fact]
    public void BranchSelectionLoadsSwitchesAndRefreshesCurrentBranch()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService();
            var fileStatus = new FakeFileStatusService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: info, branchService: branches, fileStatusService: fileStatus);
            Assert.False(Control<Button>(window, "RefreshBranchesButton").IsEnabled);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Contains("main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal(1, branches.ListCalls);
            Click(window, "RefreshBranchesButton");
            Assert.Equal(2, branches.ListCalls);
            Assert.Contains("main", Control<TextBlock>(window, "CurrentBranchText").Text);
            var list = Control<ComboBox>(window, "BranchList");
            Assert.Equal(nameof(BranchEntry.Name), list.DisplayMemberPath);
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("main", Assert.IsType<BranchEntry>(list.SelectedItem).Name);
            list.SelectedIndex = 1;
            Assert.Equal("feature", branches.Switched?.Name);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            branches.Completion.SetResult();
            PumpDispatcher();
            Assert.Contains("feature", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Contains("Switched to branch \"feature\".", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.Equal([path, path], fileStatus.Paths);
            window.Close();
        });
    }

    [Fact]
    public void BranchesTabCreatesSwitchesAndDeletesBranches()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch-tab", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: info, branchService: branches);
            window.Show();
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            var tabs = Control<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Control<TabItem>(window, "BranchesTab");
            PumpDispatcher();
            var list = Control<ListBox>(window, "BranchesList");
            Assert.Equal(2, list.Items.Count);
            Assert.Equal("main", Assert.IsType<BranchEntry>(list.SelectedItem).Name);
            var newName = Control<TextBox>(window, "NewBranchNameText");
            newName.Text = "release";
            Click(window, "CreateBranchButton");
            Assert.Equal("release", Assert.Single(branches.Created));
            Assert.Equal(3, list.Items.Count);
            var release = Assert.Single(list.Items.Cast<BranchEntry>(), entry => entry.Name == "release");
            list.SelectedItem = release;
            Assert.True(Control<Button>(window, "SwitchSelectedBranchButton").IsEnabled);
            Click(window, "SwitchSelectedBranchButton");
            branches.Completion.SetResult();
            PumpDispatcher();
            Assert.Equal("release", branches.Switched?.Name);
            Assert.Equal("release", Assert.IsType<BranchEntry>(list.SelectedItem).Name);
            Assert.False(Control<Button>(window, "DeleteBranchButton").IsEnabled);
            var feature = Assert.Single(list.Items.Cast<BranchEntry>(), entry => entry.Name == "feature");
            list.SelectedItem = feature;
            Click(window, "DeleteBranchButton");
            Assert.Equal("feature", Assert.Single(branches.Deleted).Name);
            Assert.DoesNotContain(list.Items.Cast<BranchEntry>(), entry => entry.Name == "feature");
            window.Close();
        });
    }

    [Fact]
    public void ProjectSwitchReportsWhenCurrentBranchCannotBeRead()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch-unavailable", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService { ListFailure = new IOException("branch unavailable") };
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), repositoryInfo: info,
                branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal("Branch:", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Contains("branch unavailable", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void FailedBranchSwitchRefreshesStateAndReportsPartialChanges()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch-error", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService { RemoteAvailable = false };
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: info, branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "RefreshBranchesButton");
            Assert.Contains("Could not retrieve server branches", Control<TextBlock>(window, "StatusText").Text);
            Control<ComboBox>(window, "BranchList").SelectedIndex = 1;
            branches.Completion.SetException(new LoreVcs.LoreError(42));
            PumpDispatcher();
            Assert.Contains("Files may have changed", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal(3, branches.ListCalls);
            window.Close();
        });
    }

    [Fact]
    public void DecliningBranchSwitchDoesNotCallSdk()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch-declined", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => false,
                repositoryInfo: info, branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "RefreshBranchesButton");
            Control<ComboBox>(window, "BranchList").SelectedIndex = 1;
            Assert.Null(branches.Switched);
            Assert.Contains("main", Control<TextBlock>(window, "CurrentBranchText").Text);
            Assert.Equal("main", Assert.IsType<BranchEntry>(Control<ComboBox>(window, "BranchList").SelectedItem).Name);
            window.Close();
        });
    }

    [Fact]
    public void ChangedRepositoryBlocksBranchSwitch()
    {
        RunSta(() =>
        {
            var path = CreateRepository("branch-changed", "keep.txt");
            var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
            store.Load();
            var info = new FakeProjectRepositoryInfo();
            store.Save([new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "repo", path, info.RepositoryId)]);
            var branches = new FakeBranchService();
            var window = CreateWorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
                repositoryInfo: info, branchService: branches);
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Click(window, "RefreshBranchesButton");
            info.RepositoryId = "ABCDEF1234567890ABCDEF1234567890";
            Control<ComboBox>(window, "BranchList").SelectedIndex = 1;
            Assert.Null(branches.Switched);
            Assert.Contains("Could not switch branches", Control<TextBlock>(window, "StatusText").Text);
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

    private sealed class FakeBranchService : IBranchService
    {
        private readonly BranchEntry main = new("11111111111111111111111111111111", "main", true, true, true);
        private readonly BranchEntry feature = new("22222222222222222222222222222222", "feature", true, true, false);
        public BranchEntry? Switched { get; private set; }
        public bool RemoteAvailable { get; init; } = true;
        public Exception? ListFailure { get; set; }
        public int ListCalls { get; private set; }
        public List<string> Created { get; } = [];
        public List<BranchEntry> Deleted { get; } = [];
        public TaskCompletionSource Completion { get; } = new();

        public Task<BranchListResult> ListAsync(string path)
        {
            ListCalls++;
            if (ListFailure is { } failure) return Task.FromException<BranchListResult>(failure);
            var switched = Completion.Task.IsCompletedSuccessfully;
            var branches = new List<BranchEntry>
            {
                main with { IsCurrent = !switched },
                feature with { IsCurrent = switched && (Switched is null || Switched.Name == feature.Name) }
            };
            branches = branches.Where(branch => !Deleted.Any(deleted => deleted.Name == branch.Name)).ToList();
            branches.AddRange(Created.Where(name => !Deleted.Any(branch => branch.Name == name))
                .Select((name, index) => new BranchEntry($"{index + 3:D32}", name, true, true, switched && Switched?.Name == name)));
            return Task.FromResult(new BranchListResult(branches, RemoteAvailable));
        }

        public Task SwitchAsync(string path, BranchEntry branch)
        {
            Switched = branch;
            return Completion.Task;
        }

        public Task CreateAsync(string path, string name)
        {
            Created.Add(name);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, BranchEntry branch)
        {
            Deleted.Add(branch);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLatestService : ILatestService
    {
        public string? Path { get; private set; }
        public Func<string, Task>? EnsureRemoteAuthorized { get; init; }
        public Func<string, IProgress<string>, Task<LatestStatus>>? GetLatest { get; init; }
        public int GetLatestCalls { get; private set; }
        public TaskCompletionSource<LatestStatus> Completion { get; } = new();

        public Task EnsureRemoteAuthorizedAsync(string path) =>
            EnsureRemoteAuthorized?.Invoke(path) ?? Task.CompletedTask;

        public Task<LatestStatus> GetLatestAsync(string path, IProgress<string> progress)
        {
            Path = path;
            GetLatestCalls++;
            return GetLatest?.Invoke(path, progress) ?? Completion.Task;
        }
    }
}
