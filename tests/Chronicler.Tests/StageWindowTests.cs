using System.IO;
using System.Windows.Controls;
using Chronicler.Projects;
using Chronicler.Servers;
using LoreVcs;
using LoreVcs.Types.Args;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class StageWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void StagesSelectedFolderAndUnstagesSelectedItems()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var folder = Path.Combine(project.LocalPath, "folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "file.txt"), "file");
            var changed = false;
            var service = new FakeStageService
            {
                Stage = (_, _) => { changed = true; return Task.CompletedTask; },
                Unstage = (_, _) => { changed = false; return Task.CompletedTask; }
            };
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path, changed
                    ? new Dictionary<string, FileChangeKind> { ["folder\\file.txt"] = FileChangeKind.Staged, ["other.txt"] = FileChangeKind.Staged }
                    : new Dictionary<string, FileChangeKind>()))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var folderItem = WaitForChild(root, "folder");
            folderItem.IsSelected = true;
            Assert.True(Control<Button>(window, "StageButton").IsEnabled);
            Click(window, "StageButton");
            Assert.Equal((project.LocalPath, folder), Assert.Single(service.Staged));
            Assert.Same(Control<TabItem>(window, "StageTab"), Control<TabControl>(window, "WorkspaceTabs").SelectedItem);
            var list = Control<ListBox>(window, "StageList");
            Assert.Equal(["folder\\file.txt", "other.txt"], list.Items.OfType<StagedEntry>().Select(item => item.RelativePath));
            Control<TabControl>(window, "WorkspaceTabs").SelectedItem = Control<TabItem>(window, "StageTab");
            list.SelectAll();
            Assert.True(Control<Button>(window, "UnstageButton").IsEnabled);
            Click(window, "UnstageButton");
            var unstage = Assert.Single(service.Unstaged);
            Assert.Equal(project.LocalPath, unstage.Root);
            Assert.Equal([Path.Combine(folder, "file.txt"), Path.Combine(project.LocalPath, "other.txt")], unstage.Paths);
            Assert.Empty(list.Items);
            Assert.Contains("ありません", Control<TextBox>(window, "StageStatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void FileStageFailureRefreshesAndDisablesOperationsDuringScan()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var filePath = Path.Combine(project.LocalPath, "file.txt");
            File.WriteAllText(filePath, "file");
            var rescan = new TaskCompletionSource<FileStatusSnapshot>();
            var status = new FakeFileStatusService();
            status.Scan = path => status.Paths.Count == 1
                ? Task.FromResult(new FileStatusSnapshot(path, new Dictionary<string, FileChangeKind>()))
                : rescan.Task;
            var service = new FakeStageService
            {
                Stage = (_, _) => Task.FromException(new IOException("partial failure"))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var file = WaitForChild(root, "file.txt");
            file.IsSelected = true;
            Click(window, "StageButton");
            Assert.Equal((project.LocalPath, filePath), Assert.Single(service.Staged));
            Assert.Contains("partial failure", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("一部", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("確認しています", Control<TextBox>(window, "StageStatusText").Text);
            Assert.False(Control<Button>(window, "StageButton").IsEnabled);
            Assert.False(Control<Button>(window, "UnstageButton").IsEnabled);
            rescan.SetResult(new FileStatusSnapshot(project.LocalPath,
                new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Staged }));
            PumpDispatcher();
            Assert.Single(Control<ListBox>(window, "StageList").Items);
            Assert.True(Control<Button>(window, "StageButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void ProjectSwitchAndScanFailureNeverShowOldStagedEntries()
    {
        RunSta(() =>
        {
            var first = CreateProject("first");
            var second = CreateProject("second");
            var status = new FakeFileStatusService
            {
                Scan = path => path == first.LocalPath
                    ? Task.FromResult(new FileStatusSnapshot(path, new Dictionary<string, FileChangeKind> { ["first.txt"] = FileChangeKind.Staged }))
                    : Task.FromException<FileStatusSnapshot>(new IOException("scan failed"))
            };
            var window = CreateWindow(new FakeStageService(), status, first, second);
            window.Show();
            SelectProject(window, 0);
            Assert.Single(Control<ListBox>(window, "StageList").Items);
            SelectProject(window, 1);
            Assert.Empty(Control<ListBox>(window, "StageList").Items);
            Assert.Contains("scan failed", Control<TextBox>(window, "StageStatusText").Text);
            Assert.False(Control<Button>(window, "UnstageButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void StageInProgressPreventsConflictingOperations()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var pending = new TaskCompletionSource();
            var service = new FakeStageService { Stage = (_, _) => pending.Task };
            var window = CreateWindow(service, new FakeFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            root.IsSelected = true;
            Click(window, "StageButton");
            Assert.False(Control<Button>(window, "StageButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.Contains("しています", Control<TextBox>(window, "StageStatusText").Text);
            pending.SetResult();
            PumpDispatcher();
            Assert.True(Control<Button>(window, "StageButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public async Task RealSdkStagesAndUnstagesSelectedFileThroughWindow()
    {
        var path = Path.Combine(directory, "real");
        Directory.CreateDirectory(path);
        using (var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true })
        using (var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"stage-ui-{Guid.NewGuid():N}" })
            await Lore.RepositoryCreate(globals, create).WaitAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "working content");
        var project = new ProjectProfile(Guid.NewGuid(), "real", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
        RunSta(() =>
        {
            var window = CreateWindow(new LoreStageService(), new LoreFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FileTree").Items[0]);
            var file = WaitForChild(root, "file.txt");
            file.IsSelected = true;
            WaitUntil(() => Control<Button>(window, "StageButton").IsEnabled);
            Click(window, "StageButton");
            var list = Control<ListBox>(window, "StageList");
            WaitUntil(() => list.Items.Count == 1 && Control<TextBox>(window, "StageStatusText").Text.Contains("前回"));
            Assert.Equal("file.txt", Assert.IsType<StagedEntry>(list.Items[0]).RelativePath);
            list.SelectedIndex = 0;
            Click(window, "UnstageButton");
            WaitUntil(() => list.Items.Count == 0 && Control<TextBox>(window, "StageStatusText").Text.Contains("ありません"));
            Assert.Equal("working content", File.ReadAllText(Path.Combine(path, "file.txt")));
            window.Close();
        });
    }

    private ProjectProfile CreateProject(string name)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.Combine(path, ".lore"));
        return new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
    }

    private WorkspaceWindow CreateWindow(IStageService stage, IFileStatusService status, params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), (_, _) => true,
            repositoryInfo: repositoryInfo, fileStatusService: status, fileLockService: new FakeFileLockService(), stageService: stage);
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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeStageService : IStageService
    {
        public List<(string Root, string Path)> Staged { get; } = [];
        public List<(string Root, string[] Paths)> Unstaged { get; } = [];
        public Func<string, string, Task> Stage { get; set; } = (_, _) => Task.CompletedTask;
        public Func<string, IReadOnlyList<string>, Task> Unstage { get; set; } = (_, _) => Task.CompletedTask;

        public Task StageAsync(string root, string path)
        {
            Staged.Add((root, path));
            return Stage(root, path);
        }

        public Task UnstageAsync(string root, IReadOnlyList<string> paths)
        {
            Unstaged.Add((root, paths.ToArray()));
            return Unstage(root, paths);
        }
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress) =>
            throw new InvalidOperationException("Unexpected network access");
    }
}
