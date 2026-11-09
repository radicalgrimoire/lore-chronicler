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

public sealed class StageWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private readonly ServerProfile server = new(Guid.NewGuid(), "Server", "lore://localhost:41337");
    private readonly FakeProjectRepositoryInfo repositoryInfo = new();

    [Fact]
    public void FilterAndFolderTogglingUseOneTreeAndClearSelection()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                    new Dictionary<string, FileChangeKind>
                    {
                        ["folder\\nested\\new.txt"] = FileChangeKind.Untracked,
                        ["folder\\staged.txt"] = FileChangeKind.Staged | FileChangeKind.Modified,
                        ["folder\\nested\\deleted.txt"] = FileChangeKind.Changed,
                        ["root.txt"] = FileChangeKind.Modified
                    }, actions: new Dictionary<string, ChangeAction>
                    {
                        ["folder\\nested\\deleted.txt"] = ChangeAction.Deleted
                    }))
            };
            var window = CreateWindow(new FakeStageService(), status, project);
            window.Show();
            SelectProject(window, 0);
            Control<TabControl>(window, "WorkspaceTabs").SelectedItem = Control<TabItem>(window, "StageTab");
            var list = Control<ListBox>(window, "ChangeList");
            var filter = Control<ComboBox>(window, "ChangeFilter");
            var folder = Assert.Single(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder");
            Assert.True(folder.IsStaged && folder.IsUnstaged);
            Assert.Equal("✓・", folder.StateMarker);
            Assert.Equal(
                ["folder", "folder\\nested", "folder\\nested\\deleted.txt", "folder\\nested\\new.txt", "folder\\staged.txt", "root.txt"],
                list.Items.OfType<StagedEntry>().Select(entry => entry.RelativePath));
            Assert.Equal([0, 1, 2, 2, 1, 0], list.Items.OfType<StagedEntry>().Select(entry => entry.Depth));
            Assert.Equal("deleted.txt", list.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Deleted).TreeName);
            var staged = Assert.Single(list.Items.OfType<StagedEntry>(), entry => entry.IsStaged && !entry.IsDirectory);
            Assert.Equal("✓", staged.StateMarker);
            Assert.Equal("#FFE4F5E8", staged.Background);
            var added = Assert.Single(list.Items.OfType<StagedEntry>(), entry => entry.Action == ChangeAction.Added);
            Assert.Equal("・", added.StateMarker);
            Assert.Contains("unstaged", added.AccessibleName);
            list.SelectedItem = added;
            filter.SelectedIndex = 2;
            Assert.Empty(list.SelectedItems);
            Assert.All(list.Items.OfType<StagedEntry>(), entry => Assert.True(entry.IsStaged));
            Assert.DoesNotContain(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\nested\\new.txt");
            Assert.DoesNotContain(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\nested");
            filter.SelectedIndex = 1;
            Assert.All(list.Items.OfType<StagedEntry>(), entry => Assert.True(entry.IsUnstaged));
            Assert.DoesNotContain(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\staged.txt");
            filter.SelectedIndex = 0;
            list.SelectedItem = list.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Added);
            folder = list.Items.OfType<StagedEntry>().Single(entry => entry.RelativePath == "folder");
            list.ScrollIntoView(folder);
            window.UpdateLayout();
            PumpDispatcher();
            var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(folder));
            var button = Assert.IsType<Button>(FindButton(container));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(list.SelectedItems);
            Assert.DoesNotContain(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath.StartsWith("folder\\"));
            Assert.Contains(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "root.txt");
            folder = list.Items.OfType<StagedEntry>().Single(entry => entry.RelativePath == "folder");
            Assert.False(folder.IsExpanded);
            list.ScrollIntoView(folder);
            window.UpdateLayout();
            container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(folder));
            Assert.IsType<Button>(FindButton(container)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(list.Items.OfType<StagedEntry>(), entry => entry.Action == ChangeAction.Added);
            var nested = list.Items.OfType<StagedEntry>().Single(entry => entry.RelativePath == "folder\\nested");
            list.ScrollIntoView(nested);
            window.UpdateLayout();
            container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(nested));
            Assert.IsType<Button>(FindButton(container)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\nested\\deleted.txt");
            Assert.Contains(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\staged.txt");
            filter.SelectedIndex = 2;
            Assert.Contains(list.Items.OfType<StagedEntry>(), entry => entry.RelativePath == "folder\\staged.txt");
            filter.SelectedIndex = 0;
            Assert.False(list.Items.OfType<StagedEntry>().Single(entry => entry.RelativePath == "folder\\nested").IsExpanded);
            Assert.Contains("1 staged change", Control<TextBlock>(window, "CommitTargetText").Text);
            window.Close();
        });
    }

    [Fact]
    public void FolderStageWarnsAboutHiddenStagedChangesAndCanBeCancelled()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var service = new FakeStageService();
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                    new Dictionary<string, FileChangeKind>
                    {
                        ["folder\\new.txt"] = FileChangeKind.Untracked,
                        ["folder\\staged.txt"] = FileChangeKind.Staged
                    }))
            };
            string? warning = null;
            var window = CreateWindowWithConfirmation(service, status,
                (message, _) => { warning = message; return false; }, project);
            window.Show();
            SelectProject(window, 0);
            Control<ComboBox>(window, "ChangeFilter").SelectedIndex = 1;
            var list = Control<ListBox>(window, "ChangeList");
            list.SelectedItem = list.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Folder);
            Click(window, "StageSelectionButton");
            Assert.Contains("Staged items hidden by the filter may also be affected", warning);
            Assert.Contains("folder", warning);
            Assert.Empty(service.Staged);
            Assert.Contains("cancelled", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void StagesSelectedFolderAndIndependentFileFromChangeList()
    {
        RunSta(() =>
        {
            var project = CreateProject("first");
            var service = new FakeStageService();
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                    new Dictionary<string, FileChangeKind>
                    {
                        ["folder\\deleted.txt"] = FileChangeKind.Changed,
                        ["other.txt"] = FileChangeKind.Modified
                    }, actions: new Dictionary<string, ChangeAction>
                    {
                        ["folder\\deleted.txt"] = ChangeAction.Deleted,
                        ["other.txt"] = ChangeAction.Modified
                    }))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            var list = Control<ListBox>(window, "ChangeList");
            var entries = list.Items.OfType<StagedEntry>().ToArray();
            var folder = Assert.Single(entries, entry => entry.Action == ChangeAction.Folder);
            var deleted = Assert.Single(entries, entry => entry.Action == ChangeAction.Deleted);
            var other = Assert.Single(entries, entry => entry.Action == ChangeAction.Modified);
            Assert.Equal("−", deleted.Icon);
            Assert.Equal("Delete", deleted.ActionText);
            Assert.Equal("#FFFFF2C6", deleted.Background);
            list.SelectedItems.Add(folder);
            list.SelectedItems.Add(deleted);
            list.SelectedItems.Add(other);
            Click(window, "StageSelectionButton");
            Assert.Equal(2, service.Staged.Count);
            Assert.Contains((project.LocalPath, Path.Combine(project.LocalPath, "folder")), service.Staged);
            Assert.Contains((project.LocalPath, Path.Combine(project.LocalPath, "other.txt")), service.Staged);
            window.Close();
        });
    }

    [Fact]
    public async Task RealSdkStagesDeletedFileFromChangeList()
    {
        var path = Path.Combine(directory, "deleted");
        Directory.CreateDirectory(path);
        using (var globals = new LoreGlobalArgs { RepositoryPath = path, Offline = true })
        using (var create = new LoreRepositoryCreateArgs { RepositoryUrl = $"stage-delete-{Guid.NewGuid():N}" })
        {
            await Lore.RepositoryCreate(globals, create).WaitAsync();
            Directory.CreateDirectory(Path.Combine(path, "folder"));
            var file = Path.Combine(path, "folder", "deleted.txt");
            await File.WriteAllTextAsync(file, "content");
            using (var stage = new LoreFileStageArgs { Paths = [file], Scan = true })
                await Lore.FileStage(globals, stage).WaitAsync();
            using (var commit = new LoreRevisionCommitArgs { Message = "initial" })
                await Lore.RevisionCommit(globals, commit).WaitAsync();
            File.Delete(file);
        }
        var project = new ProjectProfile(Guid.NewGuid(), "real", server.Id, server.RemoteUrl, "repo", path, repositoryInfo.RepositoryId);
        RunSta(() =>
        {
            var window = CreateWindow(new LoreStageService(), new LoreFileStatusService(), project);
            window.Show();
            SelectProject(window, 0);
            var unstaged = Control<ListBox>(window, "ChangeList");
            WaitUntil(() => unstaged.Items.OfType<StagedEntry>().Any(entry => entry.Action == ChangeAction.Deleted));
            unstaged.SelectedItem = unstaged.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Deleted);
            Assert.True(Control<Button>(window, "StageSelectionButton").IsEnabled);
            Click(window, "StageSelectionButton");
            var staged = Control<ListBox>(window, "ChangeList");
            WaitUntil(() => staged.Items.OfType<StagedEntry>().Any(entry => entry.Action == ChangeAction.Deleted && entry.IsStaged));
            Assert.DoesNotContain(unstaged.Items.OfType<StagedEntry>(), entry => entry.Action == ChangeAction.Deleted && entry.IsUnstaged);
            staged.SelectedItem = staged.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Folder);
            Click(window, "UnstageButton");
            WaitUntil(() => unstaged.Items.OfType<StagedEntry>().Any(entry => entry.Action == ChangeAction.Deleted && entry.IsUnstaged));
            Assert.False(File.Exists(Path.Combine(path, "folder", "deleted.txt")));
            window.Close();
        });
    }

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
                    : new Dictionary<string, FileChangeKind> { ["folder\\file.txt"] = FileChangeKind.Untracked }))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            Assert.Null(window.FindName("StageButton"));
            var unstaged = Control<ListBox>(window, "ChangeList");
            unstaged.SelectedItem = unstaged.Items.OfType<StagedEntry>().Single(entry => entry.Action == ChangeAction.Folder);
            Assert.True(Control<Button>(window, "StageSelectionButton").IsEnabled);
            Click(window, "StageSelectionButton");
            Assert.Equal((project.LocalPath, folder), Assert.Single(service.Staged));
            var list = Control<ListBox>(window, "ChangeList");
            Assert.Equal(["folder", "folder\\file.txt", "other.txt"], list.Items.OfType<StagedEntry>().Select(item => item.RelativePath));
            Control<TabControl>(window, "WorkspaceTabs").SelectedItem = Control<TabItem>(window, "StageTab");
            list.SelectAll();
            Assert.True(Control<Button>(window, "UnstageButton").IsEnabled);
            Click(window, "UnstageButton");
            var unstage = Assert.Single(service.Unstaged);
            Assert.Equal(project.LocalPath, unstage.Root);
            Assert.Equal([folder, Path.Combine(project.LocalPath, "other.txt")], unstage.Paths);
            Control<ComboBox>(window, "ChangeFilter").SelectedIndex = 2;
            Assert.Empty(list.Items);
            Assert.Contains("no staged changes", Control<TextBox>(window, "StageStatusText").Text);
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
                ? Task.FromResult(new FileStatusSnapshot(path, new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Untracked }))
                : rescan.Task;
            var service = new FakeStageService
            {
                Stage = (_, _) => Task.FromException(new IOException("partial failure"))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            var list = Control<ListBox>(window, "ChangeList");
            list.SelectedItem = list.Items.OfType<StagedEntry>().Single();
            Click(window, "StageSelectionButton");
            Assert.Equal((project.LocalPath, filePath), Assert.Single(service.Staged));
            Assert.Contains("partial failure", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("Stage failed", Control<TextBlock>(window, "StatusText").Text);
            Assert.Contains("Checking staged changes", Control<TextBox>(window, "StageStatusText").Text);
            Assert.False(Control<Button>(window, "StageSelectionButton").IsEnabled);
            Assert.False(Control<Button>(window, "UnstageButton").IsEnabled);
            rescan.SetResult(new FileStatusSnapshot(project.LocalPath,
                new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Staged }));
            PumpDispatcher();
            Assert.Single(Control<ListBox>(window, "ChangeList").Items.OfType<StagedEntry>());
            Assert.False(Control<Button>(window, "StageSelectionButton").IsEnabled);
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
            Assert.Single(Control<ListBox>(window, "ChangeList").Items.OfType<StagedEntry>());
            SelectProject(window, 1);
            Assert.Empty(Control<ListBox>(window, "ChangeList").Items);
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
            var status = new FakeFileStatusService
            {
                Scan = path => Task.FromResult(new FileStatusSnapshot(path,
                    new Dictionary<string, FileChangeKind> { ["file.txt"] = FileChangeKind.Untracked }))
            };
            var window = CreateWindow(service, status, project);
            window.Show();
            SelectProject(window, 0);
            var unstaged = Control<ListBox>(window, "ChangeList");
            unstaged.SelectedItem = unstaged.Items.OfType<StagedEntry>().Single();
            Click(window, "StageSelectionButton");
            Assert.False(Control<Button>(window, "StageSelectionButton").IsEnabled);
            Assert.False(Control<Button>(window, "SwitchProjectButton").IsEnabled);
            Assert.False(Control<Button>(window, "GetLatestButton").IsEnabled);
            Assert.Contains("Stage in progress", Control<TextBox>(window, "StageStatusText").Text);
            pending.SetResult();
            PumpDispatcher();
            unstaged.SelectedItem = unstaged.Items.OfType<StagedEntry>().Single();
            Assert.True(Control<Button>(window, "StageSelectionButton").IsEnabled);
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
            var unstaged = Control<ListBox>(window, "ChangeList");
            WaitUntil(() => unstaged.Items.OfType<StagedEntry>().Count() == 1);
            unstaged.SelectedItem = unstaged.Items.OfType<StagedEntry>().Single();
            WaitUntil(() => Control<Button>(window, "StageSelectionButton").IsEnabled);
            Click(window, "StageSelectionButton");
            var list = Control<ListBox>(window, "ChangeList");
            WaitUntil(() => list.Items.OfType<StagedEntry>().Any(entry => entry.IsStaged) && Control<TextBox>(window, "StageStatusText").Text.Contains("staged"));
            Assert.Equal("file.txt", Assert.Single(list.Items.OfType<StagedEntry>()).RelativePath);
            list.SelectedItem = list.Items.OfType<StagedEntry>().Single();
            Click(window, "UnstageButton");
            Control<ComboBox>(window, "ChangeFilter").SelectedIndex = 2;
            WaitUntil(() => list.Items.Count == 0 && Control<TextBox>(window, "StageStatusText").Text.Contains("no staged changes"));
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
        => CreateWindowWithConfirmation(stage, status, (_, _) => true, projects);

    private WorkspaceWindow CreateWindowWithConfirmation(IStageService stage, IFileStatusService status,
        Func<string, string, bool> confirm, params ProjectProfile[] projects)
    {
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save(projects);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), confirm,
            repositoryInfo: repositoryInfo, fileStatusService: status, fileLockService: new FakeFileLockService(),
            stageService: stage, pushService: new FakePushStatusService(), branchService: new FakeBranchListService());
    }

    private static Button? FindButton(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) return button;
            var found = FindButton(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static void SelectProject(WorkspaceWindow window, int index)
    {
        Control<ListBox>(window, "ProjectList").SelectedIndex = index;
        Click(window, "SwitchProjectButton");
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
