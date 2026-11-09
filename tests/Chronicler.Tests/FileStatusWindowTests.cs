using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Chronicler.Projects;
using Chronicler.Servers;
using LoreVcs;
using LoreVcs.Types.Args;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class FileStatusWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void ShowsFileAndFolderStatusesAndRefreshesThem()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            Directory.CreateDirectory(Path.Combine(project.LocalPath, "folder"));
            File.WriteAllText(Path.Combine(project.LocalPath, "tracked.txt"), "a");
            File.WriteAllText(Path.Combine(project.LocalPath, "folder", "new.txt"), "b");
            File.WriteAllText(Path.Combine(project.LocalPath, "clean.txt"), "c");
            var changed = true;
            var service = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path, changed
                    ? new Dictionary<string, FileChangeKind>
                    {
                        ["tracked.txt"] = FileChangeKind.Modified,
                        ["folder\\new.txt"] = FileChangeKind.Untracked
                    }
                    : new Dictionary<string, FileChangeKind>()))
            };
            var window = CreateWindow(service, project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var tracked = WaitForChild(root, "tracked.txt");
            var folder = WaitForChild(root, "folder");
            var clean = WaitForChild(root, "clean.txt");
            AssertHighlight(root, false);
            AssertHighlight(tracked, false);
            AssertHighlight(folder, false);
            AssertHighlight(clean, false);
            folder.IsExpanded = true;
            var added = WaitForChild(folder, "new.txt");
            AssertHighlight(added, false);
            tracked.IsSelected = true;
            PumpDispatcher();
            AssertHighlight(tracked, false);
            var changes = Control<ListBox>(window, "ChangeList");
            Assert.Contains(changes.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "tracked.txt" && entry.Action == ChangeAction.Modified);
            Assert.Contains(changes.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\new.txt" && entry.Action == ChangeAction.Added);
            Assert.Contains("2 unstaged", Control<TextBox>(window, "StageStatusText").Text);

            changed = false;
            Click(window, "RefreshTreeButton");
            root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            tracked = WaitForChild(root, "tracked.txt");
            folder = WaitForChild(root, "folder");
            AssertHighlight(root, false);
            AssertHighlight(tracked, false);
            AssertHighlight(folder, false);
            Assert.Empty(changes.Items);
            Assert.Equal(2, service.Paths.Count);
            window.Close();
        });
    }

    [Fact]
    public void SwitchingProjectsDiscardsOldScanAndRunsTheNewOne()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            File.WriteAllText(Path.Combine(second.LocalPath, "file.txt"), "x");
            var delayed = new TaskCompletionSource<FileStatusSnapshot>();
            var service = new FakeFileStatusService
            {
                Scan = path => path == first.LocalPath
                    ? delayed.Task
                    : Task.FromResult(new FileStatusSnapshot(path,
                        new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Untracked }))
            };
            var window = CreateWindow(service, first, second);
            window.Show();
            SelectProject(window, 0);
            Assert.Contains("Checking staged changes", Control<TextBox>(window, "StageStatusText").Text);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.True(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            SelectProject(window, 1);
            Assert.Equal([first.LocalPath], service.Paths);
            delayed.SetResult(new FileStatusSnapshot(first.LocalPath,
                new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Modified }));
            PumpDispatcher();
            Assert.Equal([first.LocalPath, second.LocalPath], service.Paths);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var item = WaitForChild(root, "file.txt");
            AssertHighlight(item, false);
            Assert.Equal("file.txt", Assert.Single(Control<ListBox>(window, "ChangeList").Items.OfType<StagedEntry>()).RelativePath);
            Assert.True(Control<Button>(window, "GetLatestButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void ScanFailureIsVisibleAndRefreshCanRetry()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            File.WriteAllText(Path.Combine(project.LocalPath, "file.txt"), "x");
            var service = new FakeFileStatusService
            {
                Scan = _ => Task.FromException<FileStatusSnapshot>(new IOException("scan failed"))
            };
            var window = CreateWindow(service, project);
            window.Show();
            SelectProject(window, 0);
            Assert.Contains("scan failed", Control<TextBox>(window, "StageStatusText").Text);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            WaitForChild(root, "file.txt");
            service.Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Modified }));
            Click(window, "RefreshTreeButton");
            root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var item = WaitForChild(root, "file.txt");
            AssertHighlight(item, false);
            Assert.Equal("file.txt", Assert.Single(Control<ListBox>(window, "ChangeList").Items.OfType<StagedEntry>()).RelativePath);
            Assert.Contains("1 unstaged", Control<TextBox>(window, "StageStatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public async Task RealSdkScanUpdatesTheVisibleTree()
    {
        var path = Path.Combine(directory, "real");
        Directory.CreateDirectory(path);
        using (var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true })
        using (var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"lore://localhost:41337/status-{Guid.NewGuid():N}" })
        {
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            var tracked = Path.Combine(path, "tracked.txt");
            await File.WriteAllTextAsync(tracked, "original");
            using (var stage = new LoreFileStageArgs { Paths = [tracked] })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            await File.WriteAllTextAsync(tracked, "modified content");
            await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new");
        }

        Task<string> repositoryUrl;
        using (await LoreRepositoryOperationGate.AcquireAsync(path))
        {
            repositoryUrl = new ProjectRepositoryInfo().ReadServerUrlAsync(path);
            Assert.False(repositoryUrl.IsCompleted);
        }
        Assert.Equal("lore://localhost:41337", await repositoryUrl.WaitAsync(TimeSpan.FromSeconds(10)));

        RunSta(() =>
        {
            var project = new ProjectProfile(Guid.NewGuid(), "Real", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
            var window = CreateWindow(new LoreFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var tracked = WaitForChild(root, "tracked.txt");
            var added = WaitForChild(root, "new.txt");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Control<ListBox>(window, "ChangeList").Items.Count < 2 && DateTime.UtcNow < deadline)
            {
                PumpDispatcher();
                Thread.Sleep(10);
            }
            AssertHighlight(tracked, false);
            AssertHighlight(added, false);
            var changes = Control<ListBox>(window, "ChangeList").Items.OfType<StagedEntry>().ToArray();
            Assert.Contains(changes, entry => entry.RelativePath == "tracked.txt" && entry.Action == ChangeAction.Modified);
            Assert.Contains(changes, entry => entry.RelativePath == "new.txt" && entry.Action == ChangeAction.Added);
            Assert.Contains("2 unstaged", Control<TextBox>(window, "StageStatusText").Text);
            window.Close();
        });
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private WorkspaceWindow CreateWindow(IFileStatusService status, params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
            repositoryInfo: repositoryInfo, fileStatusService: status, fileLockService: new FakeFileLockService(),
            pushService: new FakePushStatusService(), branchService: new FakeBranchListService());
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
            var child = parent.Items.OfType<TreeViewItem>().FirstOrDefault(item => Equals(item.Header, header));
            if (child is not null) return child;
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Tree item not found: {header}");
    }

    private static void AssertHighlight(TreeViewItem item, bool expected)
    {
        item.UpdateLayout();
        var text = FindHeader(item, (string)item.Header);
        Assert.NotNull(text);
        var background = text.Background as SolidColorBrush;
        if (expected)
            Assert.Equal(Color.FromRgb(255, 242, 179), background?.Color);
        else
            Assert.True(background is null || background.Color == Colors.Transparent);
        Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(item));
    }

    private static TextBlock? FindHeader(DependencyObject parent, string header)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem) continue;
            if (child is TextBlock text && text.Text == header) return text;
            var found = FindHeader(child, header);
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
