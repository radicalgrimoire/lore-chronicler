using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Chronicler.Servers;
using Chronicler.Authentication;
using LoreVcs;
using Microsoft.Win32;

namespace Chronicler.Projects;

public partial class WorkspaceWindow : Window
{
    private const string BranchesLoadedMessage = "Branch list retrieved.";
    private readonly ServerProfile server;
    private readonly ProjectSettingsStore store;
    private readonly IRepositoryService repositoryService;
    private readonly ILatestService latestService;
    private readonly IProjectRepositoryInfo repositoryInfo;
    private readonly IBranchService branchService;
    private readonly IRevisionHistoryService historyService;
    private readonly IFileStatusService fileStatusService;
    private readonly IFileLockService fileLockService;
    private readonly IFileLockActionsService fileLockActionsService;
    private readonly IStageService stageService;
    private readonly ICommitService commitService;
    private readonly IPushService pushService;
    private readonly IAuthenticationStatusService authenticationStatusService;
    private readonly DispatcherTimer authenticationStatusTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Func<string, string, bool> confirm;
    private readonly Func<string, string?> selectProjectName;
    private readonly Func<string, bool> authenticate;
    private readonly Func<string, bool> authenticateAutomatically;
    private readonly ObservableCollection<ProjectProfile> projects = [];
    private readonly Dictionary<Guid, string> commitDrafts = [];
    private readonly HashSet<string> collapsedChangeFolders = new(StringComparer.OrdinalIgnoreCase);
    private ProjectProfile? activeProject;
    private CancellationTokenSource treeCancellation = new();
    private bool available;
    private bool busy;
    private bool closed;
    private bool historyLoading;
    private bool historyNeedsRefresh = true;
    private int historyVersion;
    private int fileStatusVersion;
    private bool fileStatusRunning;
    private bool fileStatusNeedsRefresh;
    private FileStatusSnapshot? fileStatus;
    private int lockVersion;
    private bool lockNeedsRefresh;
    private IReadOnlyDictionary<string, IReadOnlyList<FileLockOwner>>? fileLocks;
    private int authenticationStatusVersion;

    public WorkspaceWindow() : this(new ServerProfile(Guid.NewGuid(), "server", string.Empty),
        new ProjectSettingsStore(ProjectSettingsStore.DefaultPath), new LoreRepositoryService())
    {
    }

    public WorkspaceWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null, ILatestService? latestService = null,
        IProjectRepositoryInfo? repositoryInfo = null,
        Func<string, string?>? selectProjectName = null, Func<string, bool>? authenticate = null,
        IBranchService? branchService = null, IRevisionHistoryService? historyService = null,
        IFileStatusService? fileStatusService = null, IFileLockService? fileLockService = null,
        IStageService? stageService = null, ICommitService? commitService = null, IPushService? pushService = null,
        IAuthenticationStatusService? authenticationStatusService = null,
        IFileLockActionsService? fileLockActionsService = null)
    {
        this.server = server;
        this.store = store;
        this.repositoryService = repositoryService;
        this.latestService = latestService ?? new LoreLatestService();
        this.repositoryInfo = repositoryInfo ?? new ProjectRepositoryInfo();
        this.branchService = branchService ?? new LoreBranchService();
        this.historyService = historyService ?? new LoreRevisionHistoryService();
        this.fileStatusService = fileStatusService ?? new LoreFileStatusService();
        this.fileLockService = fileLockService ?? new LoreFileLockService();
        this.fileLockActionsService = fileLockActionsService ?? new LoreFileLockService();
        this.stageService = stageService ?? new LoreStageService();
        this.commitService = commitService ?? new LoreCommitService();
        this.pushService = pushService ?? new LorePushService();
        this.authenticationStatusService = authenticationStatusService ?? new LoreAuthenticationStatusService();
        this.confirm = confirm ?? ((message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
        InitializeComponent();
        WorkspaceTabs.SelectionChanged += WorkspaceTabs_SelectionChanged;
        this.selectProjectName = selectProjectName ?? (name =>
        {
            var dialog = new RenameProjectWindow(name) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.ProjectName : null;
        });
        this.authenticate = authenticate ?? (url => new LoginWindow(url) { Owner = this }.ShowDialog() == true);
        authenticateAutomatically = authenticate
            ?? (url => new LoginWindow(url, autoStart: true) { Owner = this }.ShowDialog() == true);
        ProjectList.ItemsSource = projects;
        ReloadProjects();
        UpdateControls();
        authenticationStatusTimer.Tick += async (_, _) => await RefreshAuthenticationStatusAsync();
        Loaded += (_, _) => authenticationStatusTimer.Start();
        Activated += async (_, _) => await RefreshAuthenticationStatusAsync();
    }

    private void ReloadProjects(Guid? selectedId = null)
    {
        try
        {
            var loaded = store.Load();
            projects.Clear();
            foreach (var project in loaded)
            {
                projects.Add(project);
            }
            ProjectList.SelectedItem = projects.FirstOrDefault(project => project.Id == selectedId);
            available = true;
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            available = false;
            StatusText.Text = $"Could not load project settings. The file was not changed.\n{ex.Message}";
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        var selected = ProjectList.SelectedItem is ProjectProfile;
        CreateProjectButton.IsEnabled = available && !busy;
        ManageServersButton.IsEnabled = !busy;
        RenameProjectButton.IsEnabled = available && selected && !busy;
        LoginButton.IsEnabled = available && selected && !busy;
        SwitchProjectButton.IsEnabled = available && selected && !busy;
        RemoveProjectButton.IsEnabled = available && selected && !busy;
        ProjectList.IsEnabled = !busy;
        RefreshTreeButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        GetLatestButton.IsEnabled = activeProject is not null && !busy && !fileStatusRunning;
        PushButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy && !fileStatusRunning;
        OpenFolderButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        BranchList.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        RefreshBranchesButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        SwitchBranchButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && BranchList.SelectedItem is BranchEntry { IsCurrent: false } && !busy && !fileStatusRunning;
        RefreshHistoryButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !historyLoading;
        StageSelectionButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is not null
            && ChangeList.SelectedItems.OfType<StagedEntry>().Any(entry => entry.IsUnstaged);
        UnstageButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is not null
            && ChangeList.SelectedItems.OfType<StagedEntry>().Any(entry => entry.IsStaged);
        CommitMessageText.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        CommitButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is { UnresolvedConflictCount: 0 }
            && fileStatus.StagedCount > 0 && !string.IsNullOrWhiteSpace(CommitMessageText.Text);
        UpdateFileLockControls();
    }

    private void SetActiveProject(ProjectProfile? project)
    {
        var changed = activeProject?.Id != project?.Id;
        var authenticationChanged = activeProject?.Id != project?.Id
            || activeProject?.CloneState != project?.CloneState
            || activeProject?.RepositoryId != project?.RepositoryId;
        activeProject = project;
        ActiveProjectText.Text = project is null ? string.Empty : $"{project.Name} ({project.ServerUrl})";
        if (changed)
        {
            collapsedChangeFolders.Clear();
            CommitMessageText.Text = project is null ? string.Empty : commitDrafts.GetValueOrDefault(project.Id, string.Empty);
        }
        if (authenticationChanged) _ = RefreshAuthenticationStatusAsync();
    }

    private void CommitMessageText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (activeProject is not null) commitDrafts[activeProject.Id] = CommitMessageText.Text;
        UpdateControls();
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProjectDetails.Text = ProjectList.SelectedItem is ProjectProfile project
            ? $"Server: {project.ServerUrl}\nRepository: {project.RepositoryName}\nFolder: {project.LocalPath}"
            : string.Empty;
        UpdateControls();
    }

    private async Task RefreshAuthenticationStatusAsync()
    {
        var version = ++authenticationStatusVersion;
        if (closed) return;
        AuthenticationStatusText.Foreground = Brushes.DimGray;
        AuthenticationStatusText.ToolTip = "Based on locally stored credentials; server connectivity is not checked.";
        if (activeProject is not ProjectProfile project)
        {
            AuthenticationStatusText.Text = string.Empty;
            return;
        }
        if (project.CloneState != ProjectCloneState.Ready)
        {
            AuthenticationStatusText.Text = "Available after initial download";
            return;
        }
        AuthenticationStatusText.Text = "Checking authentication...";
        try
        {
            var repositoryId = project.RepositoryId
                ?? (await repositoryInfo.ReadLocalAsync(project.LocalPath)).RepositoryId;
            var status = await authenticationStatusService.ReadAsync(repositoryId);
            if (closed || version != authenticationStatusVersion) return;
            var now = DateTimeOffset.UtcNow;
            AuthenticationStatusText.Text = status.GetText(now);
            AuthenticationStatusText.Foreground = !status.HasIdentity || status.ExpiresAt <= now
                ? Brushes.Firebrick : status.ExpiresAt is null ? Brushes.DimGray : Brushes.DarkGreen;
        }
        catch (LoreError ex)
        {
            if (closed || version != authenticationStatusVersion) return;
            AuthenticationStatusText.Text = $"Could not check authentication (Lore error {ex.ReturnCode}): {ex.Message}";
            AuthenticationStatusText.Foreground = Brushes.Firebrick;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or OverflowException)
        {
            if (closed || version != authenticationStatusVersion) return;
            AuthenticationStatusText.Text = $"Could not check authentication: {ex.Message}";
            AuthenticationStatusText.Foreground = Brushes.Firebrick;
        }
    }

    private void CreateProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProjectWindow(server, store, repositoryService, repositoryInfo: repositoryInfo, authenticate: authenticate) { Owner = this };
        window.ShowDialog();
        ReloadProjects(window.CreatedProject?.Id);
        if (window.CreatedProject is not null && available)
        {
            SwitchProject(window.CreatedProject);
        }
    }

    private void ManageServersButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ServerShortcutsWindow(new ServerSettingsStore(
            Path.Combine(store.DirectoryPath, "servers.json"))) { Owner = this };
        window.ShowDialog();
    }

    private void SwitchProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectProfile project)
        {
            SwitchProject(project);
        }
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectProfile project)
        {
            SwitchProject(project);
        }
    }

    private async void SwitchProject(ProjectProfile project)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        UpdateControls();
        try
        {
            if (project.CloneState != ProjectCloneState.Ready)
            {
                SetActiveProject(project);
                InvalidateHistory();
                ClearBranches();
                Title = $"Chronicler - {project.Name}";
                ShowProjectTree();
                StatusText.Text = project.CloneState == ProjectCloneState.Pending
                    ? "Waiting for the initial download. Select \"Get latest\" to clone the repository."
                    : "The previous clone did not complete. Check the folder, remove the registration, and create it again.";
                return;
            }
            var rootAttributes = File.GetAttributes(project.LocalPath);
            var loreAttributes = ReadLoreAttributes(project.LocalPath);
            if ((rootAttributes & FileAttributes.Directory) == 0 || (loreAttributes & FileAttributes.Directory) == 0)
            {
                StatusText.Text = "The local Lore workspace was not found.";
                return;
            }
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            if (project.RepositoryId is null)
            {
                StatusText.Text = "Verifying the project's repository on the server…";
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The server repository name does not match the registration. Register the project again.");
                }
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                var current = store.Load().ToList();
                var index = current.FindIndex(item => item == project);
                if (index < 0)
                {
                    throw new InvalidOperationException("The project was changed by another process.");
                }
                var updated = project with { RepositoryId = local.RepositoryId };
                current[index] = updated;
                store.Save(current);
                project = updated;
                ReloadProjects(project.Id);
            }
            SetActiveProject(project);
            InvalidateHistory();
            ClearBranches();
            Title = $"Chronicler - {project.Name}";
            ShowProjectTree();
            var branchStatus = await RefreshBranchesAsync(project);
            StatusText.Text = $"Switched to project \"{project.Name}\". Local files are displayed."
                + (branchStatus is null or BranchesLoadedMessage ? string.Empty : $"\n{branchStatus}");
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"Could not verify the repository (Lore error {ex.ReturnCode}). Check sign-in, connection, and permissions.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            StatusText.Text = $"Could not verify the local Lore workspace: {ex.Message}";
        }
        finally
        {
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
            LoadHistoryIfVisible();
        }
    }

    private void ShowProjectTree()
    {
        InvalidateMyLocks();
        fileStatusVersion++;
        lockVersion++;
        fileStatus = null;
        fileLocks = null;
        fileStatusNeedsRefresh = activeProject is { CloneState: ProjectCloneState.Ready };
        lockNeedsRefresh = fileStatusNeedsRefresh;
        ChangeList.ItemsSource = null;
        CommitTargetText.Text = "Commit target: not checked";
        StageStatusText.Text = fileStatusNeedsRefresh ? "Waiting to check staged changes…"
            : activeProject is null ? "Open a project." : "Available after initial download.";
        treeCancellation.Cancel();
        treeCancellation.Dispose();
        treeCancellation = new CancellationTokenSource();
        FileTree.Items.Clear();
        if (activeProject is null)
        {
            return;
        }
        if (activeProject.CloneState != ProjectCloneState.Ready)
        {
            return;
        }
        var localFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(activeProject.LocalPath));
        var root = CreateDirectoryItem($"{localFolderName} (local)", activeProject.LocalPath);
        FileTree.Items.Add(root);
        root.IsExpanded = true;
        LoadPendingFileStatus();
    }

    private TreeViewItem CreateDirectoryItem(string name, string path)
    {
        var item = new TreeViewItem { Tag = new TreeItemInfo(name, path, true) };
        UpdateTreeItem(item);
        item.Items.Add("Loading…");
        item.Expanded += Directory_Expanded;
        return item;
    }

    private async void Directory_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || !ReferenceEquals(e.OriginalSource, item)
            || item.Tag is not TreeItemInfo { Path: var path } || activeProject is null
            || item.Items.Count != 1 || item.Items[0] is not string)
        {
            return;
        }
        var root = activeProject.LocalPath;
        var token = treeCancellation.Token;
        try
        {
            var entries = await Task.Run(() => LocalFileTree.ReadChildren(root, path, token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            item.Items.Clear();
            foreach (var entry in entries)
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    item.Items.Add(CreateDirectoryItem(entry.Name, entry.Path));
                }
                else
                {
                    var suffix = entry.IsReparsePoint ? " (link)" : string.Empty;
                    var child = new TreeViewItem { Tag = new TreeItemInfo(entry.Name + suffix, entry.Path, entry.IsDirectory) };
                    UpdateTreeItem(child);
                    item.Items.Add(child);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested)
            {
                item.Items.Clear();
                item.Items.Add("Could not load. Refresh to retry.");
                StatusText.Text = $"Could not load folder: {ex.Message}";
            }
        }
    }

    private void RefreshTreeButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeProject is not null)
        {
            SwitchProject(activeProject);
        }
    }

    private sealed record TreeItemInfo(string Name, string Path, bool IsDirectory);

    private void StageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private void ChangeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || fileStatus is null) return;
        SetChangeList(fileStatus);
        UpdateControls();
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StagedEntry { HasChildren: true } folder } || fileStatus is null) return;
        if (!collapsedChangeFolders.Add(folder.FullPath)) collapsedChangeFolders.Remove(folder.FullPath);
        SetChangeList(fileStatus);
        UpdateControls();
    }

    private async void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is not { UnresolvedConflictCount: 0 } previous
            || previous.StagedCount == 0 || string.IsNullOrWhiteSpace(CommitMessageText.Text)
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;

        var message = CommitMessageText.Text;
        var expected = previous.StagedEntries;
        var preview = string.Join("\n", expected.Take(20).Select(entry => $"{entry.ActionText}: {entry.DisplayName}"));
        var remainder = expected.Count > 20
            ? $"\n{expected.Count - 20} additional staged changes are not shown." : string.Empty;
        if (!confirm($"{expected.Count} staged changes will be committed. The filter and selection do not affect the commit targets.\n\n{preview}{remainder}\n\nIf a file was edited after staging, its current contents will be committed even if it was not staged again.\n\nCommit these changes?",
                "Commit changes"))
        {
            StatusText.Text = "Commit cancelled.";
            return;
        }
        var scanCompleted = false;
        var attempted = false;
        busy = true;
        UpdateControls();
        StatusText.Text = "Checking staged changes before committing...";
        StageStatusText.Text = "Checking staged changes before committing...";
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var latest = await fileStatusService.ScanAsync(project.LocalPath);
            scanCompleted = true;
            ApplyFileStatus(latest);
            var actual = latest.StagedEntries;
            if (latest.UnresolvedConflictCount > 0 || actual.Count == 0 || !SameStagedEntries(expected, actual))
            {
                StatusText.Text = "Staged changes have changed or contain conflicts. The commit was cancelled.";
                return;
            }

            InvalidateHistory();
            attempted = true;
            StatusText.Text = "Creating commit...";
            StageStatusText.Text = "Creating commit...";
            await commitService.CommitAsync(project.LocalPath, message);
            CommitMessageText.Clear();
            StatusText.Text = "Commit created. Refreshing staged changes...";
        }
        catch (LoreError ex)
        {
            StatusText.Text = attempted
                ? $"Could not verify the commit (Lore error {ex.ReturnCode}). Check history and staged changes before retrying.\n{ex.Message}"
                : $"Pre-commit verification failed (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = attempted
                ? $"Could not verify the commit. Check history and staged changes before retrying.\n{ex.Message}"
                : $"Pre-commit verification failed: {ex.Message}";
        }
        finally
        {
            if (attempted || !scanCompleted)
                InvalidateFileStatus();
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
            LoadHistoryIfVisible();
        }
    }

    private static bool SameStagedEntries(IReadOnlyList<StagedEntry> left, IReadOnlyList<StagedEntry> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.IsDirectory == pair.Second.IsDirectory
            && pair.First.Action == pair.Second.Action
            && string.Equals(pair.First.FullPath, pair.Second.FullPath, StringComparison.OrdinalIgnoreCase));

    private async void UnstageButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is null
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        var selected = ChangeList.SelectedItems.OfType<StagedEntry>().Where(entry => entry.IsStaged).ToArray();
        var paths = SelectedChangePaths(selected);
        if (paths.Length == 0) return;
        if (!ConfirmFolderOperation(selected, stage: false)) return;
        await ChangeStageAsync(project, paths, stage: false);
    }

    private async void StageSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is null
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        var selected = ChangeList.SelectedItems.OfType<StagedEntry>().Where(entry => entry.IsUnstaged).ToArray();
        var paths = SelectedChangePaths(selected);
        if (paths.Length == 0) return;
        if (!ConfirmFolderOperation(selected, stage: true)) return;
        await ChangeStageAsync(project, paths, stage: true);
    }

    private bool ConfirmFolderOperation(IReadOnlyList<StagedEntry> selected, bool stage)
    {
        var folders = selected.Where(entry => entry.IsDirectory).ToArray();
        if (folders.Length == 0) return true;
        var paths = string.Join("\n", folders.Take(20).Select(entry => entry.RelativePath));
        var remainder = folders.Length > 20 ? $"\n{folders.Length - 20} additional folders" : string.Empty;
        var operation = stage ? "Stage" : "Unstage";
        var warning = stage
            ? "Folders are scanned recursively. Staged items hidden by the filter may also be affected."
            : "All staged changes in the selected folders are included, regardless of type or filter.";
        if (confirm($"{warning}\n\n{paths}{remainder}\n\n{operation} these changes?", $"Confirm {operation.ToLowerInvariant()}")) return true;
        StatusText.Text = $"{operation} cancelled.";
        return false;
    }

    private static string[] SelectedChangePaths(IReadOnlyList<StagedEntry> selected)
    {
        var folders = selected.Where(entry => entry.IsDirectory)
            .Select(entry => entry.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected.Where(entry =>
        {
            var parent = Path.GetDirectoryName(entry.FullPath);
            while (parent is not null)
            {
                if (folders.Contains(parent)) return false;
                parent = Path.GetDirectoryName(parent);
            }
            return true;
        }).Select(entry => entry.FullPath).ToArray();
    }

    private async Task ChangeStageAsync(ProjectProfile project, string[] paths, bool stage)
    {
        var operation = stage ? "Stage" : "Unstage";
        busy = true;
        UpdateControls();
        StatusText.Text = $"{operation} in progress…";
        StageStatusText.Text = $"{operation} in progress…";
        var attempted = false;
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            attempted = true;
            if (stage)
                foreach (var path in paths) await stageService.StageAsync(project.LocalPath, path);
            else await stageService.UnstageAsync(project.LocalPath, paths);
            StatusText.Text = $"{operation} completed. Refreshing changes.";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"{operation} failed (Lore error {ex.ReturnCode}). Some changes may have been applied.\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = $"{operation} failed. {(attempted ? "Some changes may have been applied. " : string.Empty)}{ex.Message}";
        }
        finally
        {
            if (attempted)
                InvalidateFileStatus();
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
        }
    }

    private void InvalidateFileStatus()
    {
        fileStatusVersion++;
        fileStatus = null;
        fileStatusNeedsRefresh = true;
        ChangeList.ItemsSource = null;
        CommitTargetText.Text = "Commit target: not checked";
        StageStatusText.Text = "Refreshing staged changes…";
    }

    private void ApplyFileStatus(FileStatusSnapshot result)
    {
        fileStatus = result;
        var staged = result.StagedEntries;
        var unstaged = result.UnstagedEntries;
        SetChangeList(result);
        CommitTargetText.Text = $"Commit target: {staged.Count} staged change(s)";
        StageStatusText.Text = staged.Count == 0 && unstaged.Count == 0
            ? "No changes at the last check."
            : $"{unstaged.Count(entry => entry.Action != ChangeAction.Folder)} unstaged, "
                + (staged.Count == 0 ? "no staged changes" : $"{staged.Count} staged")
                + " at the last check. Folder operations include descendants.";
        if (result.UnresolvedConflictCount > 0)
            StageStatusText.Text += $" There are {result.UnresolvedConflictCount} unresolved conflicts. They are excluded from the list.";
    }

    private void SetChangeList(FileStatusSnapshot result)
    {
        var entries = result.AllDisplayEntries.Where(entry => ChangeFilter.SelectedIndex switch
        {
            1 => entry.IsUnstaged,
            2 => entry.IsStaged,
            _ => true
        }).ToArray();
        var byPath = entries.ToDictionary(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<StagedEntry>>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<StagedEntry>();
        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.FullPath);
            if (parent is null || !byPath.ContainsKey(parent))
                roots.Add(entry);
            else
            {
                if (!children.TryGetValue(parent, out var siblings))
                    children.Add(parent, siblings = []);
                siblings.Add(entry);
            }
        }
        static IEnumerable<StagedEntry> Sorted(IEnumerable<StagedEntry> items) =>
            items.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.TreeName, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(StagedEntry Entry, int Depth)>();
        foreach (var entry in Sorted(roots).Reverse()) pending.Push((entry, 0));
        var rows = new List<StagedEntry>();
        while (pending.TryPop(out var item))
        {
            var hasChildren = children.TryGetValue(item.Entry.FullPath, out var childEntries);
            var expanded = hasChildren && !collapsedChangeFolders.Contains(item.Entry.FullPath);
            rows.Add(item.Entry with { Depth = item.Depth, HasChildren = hasChildren, IsExpanded = expanded });
            if (expanded)
                foreach (var child in Sorted(childEntries!).Reverse()) pending.Push((child, item.Depth + 1));
        }
        ChangeList.ItemsSource = rows;
    }

    private void UpdateTreeItem(TreeViewItem item)
    {
        if (item.Tag is not TreeItemInfo info) return;
        item.Header = info.Name;
        var tooltip = string.Empty;
        if (!info.IsDirectory && fileLocks is not null && fileLocks.TryGetValue(info.Path, out var owners))
        {
            tooltip = "Lore lock owners:\n" + string.Join("\n", owners.Select(owner =>
                $"{(string.IsNullOrWhiteSpace(owner.Name) ? (string.IsNullOrWhiteSpace(owner.Id) ? "Unknown" : owner.Id) : owner.Name)} (Branch ID: {owner.Branch})"));
        }
        AutomationProperties.SetHelpText(item, tooltip);
        foreach (var child in item.Items.OfType<TreeViewItem>()) UpdateTreeItem(child);
    }

    private void LoadPendingFileStatus()
    {
        LoadMyLocksIfVisible();
        LoadPendingLocks();
        if (closed || busy || fileStatusRunning || !fileStatusNeedsRefresh
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        _ = LoadFileStatusAsync(project, fileStatusVersion);
    }

    private void LoadPendingLocks()
    {
        if (closed || busy || !lockNeedsRefresh || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        lockNeedsRefresh = false;
        _ = LoadLocksAsync(project, lockVersion);
    }

    private async Task LoadLocksAsync(ProjectProfile project, int version)
    {
        bool IsCurrent() => !closed && version == lockVersion && activeProject?.Id == project.Id;
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            if (!IsCurrent()) return;
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var result = await fileLockService.QueryAsync(project.LocalPath);
            if (!IsCurrent()) return;
            fileLocks = result.Files;
            foreach (var root in FileTree.Items.OfType<TreeViewItem>()) UpdateTreeItem(root);
            if (result.Warning is not null) ReportLockWarning(result.Warning);
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) ReportLockWarning($"Could not retrieve file locks (Lore error {ex.ReturnCode}).\n{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) ReportLockWarning($"Could not retrieve file locks: {ex.Message}");
        }
    }

    private void ReportLockWarning(string message)
    {
        StatusText.Text = string.IsNullOrEmpty(StatusText.Text) ? message : $"{StatusText.Text}\n{message}";
    }

    private async Task LoadFileStatusAsync(ProjectProfile project, int version)
    {
        fileStatusNeedsRefresh = false;
        fileStatusRunning = true;
        StageStatusText.Text = "Checking staged changes…";
        UpdateControls();
        bool IsCurrent() => !closed && version == fileStatusVersion && activeProject?.Id == project.Id;
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            if (!IsCurrent()) return;
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var result = await fileStatusService.ScanAsync(project.LocalPath);
            if (!IsCurrent()) return;
            ApplyFileStatus(result);
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) StageStatusText.Text = $"Could not check changes (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) StageStatusText.Text = $"Could not check changes: {ex.Message}";
        }
        finally
        {
            fileStatusRunning = false;
            if (!closed)
            {
                UpdateControls();
                LoadPendingFileStatus();
            }
        }
    }

    private async void PushButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        busy = true;
        UpdateControls();
        var attempted = false;
        var progressActive = true;
        try
        {
            StatusText.Text = "Preparing to push...";
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var status = await RetryAfterAuthenticationAsync(project, async () =>
            {
                var status = await pushService.PrepareAsync(project.LocalPath);
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The selected repository does not match the project repository.");
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                return status;
            });
            if (!confirm($"Project: {project.Name}\nServer: {project.ServerUrl}\nRepository: {project.RepositoryName}\nBranch: {status.Branch}\nLocal revision: {status.LocalRevision}\n\nCommitted changes will be pushed to the server. Uncommitted changes will not be sent.\nIf the remote branch has changed or diverged, the push will be cancelled. A missing server branch may be created or restored. Lore link targets and layer repositories may also be sent.\n\nPush these changes?", "Push changes"))
            {
                StatusText.Text = "Push cancelled.";
                return;
            }

            ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
            var progress = new Progress<string>(message => { if (progressActive && !closed) StatusText.Text = message; });
            StatusText.Text = "Pushing changes...";
            attempted = true;
            await pushService.PushAsync(project.LocalPath, status, progress);
            progressActive = false;
            StatusText.Text = "Verifying pushed changes...";
            if (await pushService.VerifyAsync(project.LocalPath, status))
                StatusText.Text = $"Changes were pushed to branch \"{status.Branch}\".";
            else
                StatusText.Text = "The push completed, but the remote revision could not be verified.";
        }
        catch (PushException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (RemoteAuthenticationRequiredException)
        {
            StatusText.Text = "Authentication is required. Sign in and try again.";
        }
        catch (LoreError ex)
        {
            StatusText.Text = attempted
                ? $"The push failed (Lore error {ex.ReturnCode}). Repository data may have changed.\n{ex.Message}"
                : $"Unable to prepare the push (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = attempted
                ? $"The push failed. Repository data may have changed.\n{ex.Message}"
                : $"Unable to prepare the push: {ex.Message}";
        }
        finally
        {
            progressActive = false;
            if (attempted)
            {
                InvalidateHistory();
                ClearBranches();
            }
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
            LoadHistoryIfVisible();
        }
    }

    private async void GetLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || activeProject is not { } project)
        {
            return;
        }
        if (project.CloneState == ProjectCloneState.Started)
        {
            StatusText.Text = "The previous clone did not complete. Check the folder, remove the registration, and create it again.";
            return;
        }
        if (project.CloneState == ProjectCloneState.Pending)
        {
            await ClonePendingProjectAsync(project);
            return;
        }
        if (!confirm($"Project: {project.Name}\nFolder: {project.LocalPath}\n\nUpdate the current branch to the latest revision verified on the server? Files may be added, changed, or deleted.", "Get latest"))
        {
            return;
        }

        async Task ClonePendingProjectAsync(ProjectProfile project)
        {
            if (!confirm($"Server: {project.ServerUrl}\nRepository: {project.RepositoryName}\nFolder: {project.LocalPath}\n\nStart the initial clone?", "Initial download"))
            {
                return;
            }
            busy = true;
            UpdateControls();
            var started = false;
            try
            {
                var exists = CloneDestination.ExistsAndIsEmpty(project.LocalPath);
                var current = store.Load().ToList();
                var index = current.FindIndex(item => item == project);
                if (index < 0)
                {
                    throw new InvalidOperationException("The project was changed by another process.");
                }
                var inProgress = project with { CloneState = ProjectCloneState.Started };
                current[index] = inProgress;
                store.Save(current);
                started = true;
                SetActiveProject(inProgress);
                InvalidateHistory();
                ReloadProjects(inProgress.Id);
                if (!exists) CloneDestination.Reserve(project.LocalPath);
                CloneDestination.EnsureEmpty(project.LocalPath);
                StatusText.Text = "Cloning. Please wait until completion.";
                var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
                await repositoryService.CloneAsync(project.ServerUrl, project.RepositoryName, project.LocalPath, progress);
                var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
                ProjectRepositoryInfo.Validate(local, project.ServerUrl);
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The cloned repository name does not match the registration.");
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                current = store.Load().ToList();
                index = current.FindIndex(item => item == inProgress);
                if (index < 0)
                    throw new InvalidOperationException("The project was changed by another process.");
                var completed = inProgress with { CloneState = ProjectCloneState.Ready, RepositoryId = local.RepositoryId };
                current[index] = completed;
                store.Save(current);
                SetActiveProject(completed);
                InvalidateHistory();
                ReloadProjects(completed.Id);
                ShowProjectTree();
                var branchStatus = await RefreshBranchesAsync(completed);
                StatusText.Text = "Initial download completed."
                    + (branchStatus is null or BranchesLoadedMessage ? string.Empty : $"\n{branchStatus}");
            }
            catch (LoreError ex)
            {
                StatusText.Text = $"Initial download failed (Lore error {ex.ReturnCode}). Check the folder, remove the registration, and create it again.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                or JsonException or Win32Exception)
            {
                StatusText.Text = started
                    ? $"Initial download failed. Check the folder, remove the registration, and create it again: {ex.Message}"
                    : $"Could not start initial download: {ex.Message}";
            }
            finally
            {
                busy = false;
                UpdateControls();
                LoadPendingFileStatus();
                LoadHistoryIfVisible();
            }
        }
        InvalidateHistory();
        busy = true;
        UpdateControls();
        StatusText.Text = "Checking server and local history…";
        var syncStarted = false;
        var progressActive = true;
        try
        {
            await RetryAfterAuthenticationAsync(project, async () =>
            {
                await latestService.EnsureRemoteAuthorizedAsync(project.LocalPath);
                return true;
            });
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
            ProjectRepositoryInfo.ValidateRemote(local, remote);
            var checkedLocal = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(checkedLocal, project.ServerUrl, project.RepositoryId);
            var progress = new Progress<string>(message => { if (progressActive && busy && !closed) StatusText.Text = message; });
            syncStarted = true;
            var status = await RetryAfterAuthenticationAsync(project,
                () => latestService.GetLatestAsync(project.LocalPath, progress));
            progressActive = false;
            StatusText.Text = $"Branch \"{status.Branch}\" was updated to revision {status.LocalRevision}.";
        }
        catch (RemoteAuthenticationRequiredException)
        {
            StatusText.Text = "Could not verify server authentication. Check sign-in and permissions.";
        }
        catch (LatestSyncException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (LoreError ex)
        {
            StatusText.Text = syncStarted
                ? $"Update failed (Lore error {ex.ReturnCode}). Files may have changed. Check local files, connection, sign-in, and permissions."
                : $"Could not verify the repository (Lore error {ex.ReturnCode}). Check connection, sign-in, and permissions.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = syncStarted
                ? $"Update failed. Files may have changed: {ex.Message}"
                : $"Could not verify the repository: {ex.Message}";
        }
        finally
        {
            progressActive = false;
            ShowProjectTree();
            if (syncStarted && !closed)
            {
                var branchStatus = await RefreshBranchesAsync(project);
                if (branchStatus is not null and not BranchesLoadedMessage)
                    StatusText.Text += $"\n{branchStatus}";
            }
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
            LoadHistoryIfVisible();
        }
    }

        private void RenameProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectList.SelectedItem is not ProjectProfile project)
            {
                return;
            }
            var name = selectProjectName(project.Name);
            if (name is null)
            {
                return;
            }
            name = name.Trim();
            if (name.Length == 0)
            {
                StatusText.Text = "Enter a project name.";
                return;
            }
            try
            {
                var current = store.Load().ToList();
                var index = current.FindIndex(item => item == project);
                if (index < 0)
                {
                    throw new InvalidOperationException("The project was changed by another process.");
                }
                var updated = project with { Name = name };
                current[index] = updated;
                store.Save(current);
                if (activeProject?.Id == project.Id)
                {
                    SetActiveProject(updated);
                    Title = $"Chronicler - {updated.Name}";
                    ShowProjectTree();
                }
                ReloadProjects(updated.Id);
                StatusText.Text = "Project renamed.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                StatusText.Text = $"Could not rename project: {ex.Message}";
            }
        }
    private void RemoveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectProfile project
            || !confirm("Remove this project registration?\nThe local folder and server data will not be deleted.", "Remove project"))
        {
            return;
        }
        try
        {
            var current = store.Load().ToList();
            if (current.RemoveAll(item => item == project) != 1)
            {
                throw new InvalidOperationException("The project was changed or removed by another process.");
            }
            store.Save(current);
            if (activeProject?.Id == project.Id)
            {
                SetActiveProject(null);
                commitDrafts.Remove(project.Id);
                InvalidateHistory();
                ClearBranches();
                Title = "Chronicler";
                ShowProjectTree();
            }

            ReloadProjects();
            if (available)
            {
                StatusText.Text = "Project registration removed. The local folder remains.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"Could not remove project registration.\n{ex.Message}";
        }
        UpdateControls();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectProfile project)
        {
            StatusText.Text = authenticate(project.ServerUrl)
                ? "Authentication completed. Retry the operation if necessary."
                : "Sign-in cancelled.";
            await RefreshAuthenticationStatusAsync();
        }
    }

    private async Task<T> RetryAfterAuthenticationAsync<T>(ProjectProfile project, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (RemoteAuthenticationRequiredException)
        {
            if (!authenticateAutomatically(project.ServerUrl))
                throw;
            _ = RefreshAuthenticationStatusAsync();
            return await operation();
        }
    }

    private static FileAttributes ReadLoreAttributes(string path)
    {
        try
        {
            return File.GetAttributes(Path.Combine(path, ".lore"));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("The Lore metadata folder was not found. Check the folder and clone again if necessary.", ex);
        }
    }

    private void ClearBranches()
    {
        BranchList.ItemsSource = null;
        CurrentBranchText.Text = "Branch:";
    }

    private void InvalidateHistory()
    {
        historyVersion++;
        historyLoading = false;
        historyNeedsRefresh = true;
        RevisionHistoryList.ItemsSource = null;
        HistoryStatus.Text = activeProject switch
        {
            null => "Open a project.",
            { CloneState: ProjectCloneState.Pending } => "History is available after the initial download.",
            { CloneState: ProjectCloneState.Started } => "History is unavailable because the clone did not complete.",
            _ => "History has not been loaded."
        };
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == WorkspaceTabs)
        {
            LoadHistoryIfVisible();
            LoadMyLocksIfVisible();
        }
    }

    private void LoadHistoryIfVisible()
    {
        if (!closed && !busy && historyNeedsRefresh && !historyLoading
            && WorkspaceTabs.SelectedItem == UpdateLogTab
            && activeProject is { CloneState: ProjectCloneState.Ready } project)
            _ = LoadHistoryAsync(project);
    }

    private void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || historyLoading || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        _ = LoadHistoryAsync(project);
    }

    private async Task LoadHistoryAsync(ProjectProfile project)
    {
        var version = ++historyVersion;
        historyLoading = true;
        historyNeedsRefresh = false;
        RevisionHistoryList.ItemsSource = null;
        HistoryStatus.Text = "Loading revision history (connecting to the server if needed)…";
        UpdateControls();
        bool IsCurrent() => !closed && version == historyVersion && activeProject?.Id == project.Id;
        var operation = "Local repository verification";
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            if (!IsCurrent()) return;
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            operation = "Revision history retrieval";
            var entries = await historyService.ReadAsync(project.LocalPath);
            if (!IsCurrent()) return;
            RevisionHistoryList.ItemsSource = entries;
            HistoryStatus.Text = entries.Count == 0
                ? "No local revision history."
                : $"Showing {entries.Count} revisions (maximum 100).";
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) HistoryStatus.Text = $"{operation} failed (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) HistoryStatus.Text = $"Could not retrieve history: {ex.Message}";
        }
        finally
        {
            if (IsCurrent())
            {
                historyLoading = false;
                UpdateControls();
            }
        }
    }

    private void BranchList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async Task<BranchListResult> ReadBranchesAsync(ProjectProfile project)
    {
        ReadLoreAttributes(project.LocalPath);
        var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
        ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
        return await branchService.ListAsync(project.LocalPath);
    }

    private void DisplayBranches(BranchListResult result)
    {
        BranchList.ItemsSource = result.Branches;
        var current = result.Branches.Where(branch => branch.IsCurrent).ToArray();
        CurrentBranchText.Text = current.Length == 1
            ? $"Branch: {current[0].Name}"
            : "Branch:";
        BranchList.SelectedItem = current.Length == 1 ? current[0] : null;
    }

    private async Task<string?> RefreshBranchesAsync(ProjectProfile project)
    {
        ClearBranches();
        try
        {
            var result = await ReadBranchesAsync(project);
            if (closed || activeProject?.Id != project.Id) return null;
            DisplayBranches(result);
            return result.RemoteAvailable
                ? BranchesLoadedMessage
                : "Showing local branches. Could not retrieve server branches; check connection, sign-in, and permissions.";
        }
        catch (LoreError ex)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "Branch:";
            return $"Could not retrieve branches (Lore error {ex.ReturnCode}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "Branch:";
            return $"Could not retrieve branches: {ex.Message}";
        }
    }

    private async void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        busy = true;
        UpdateControls();
        StatusText.Text = "Retrieving branches…";
        try
        {
            StatusText.Text = await RefreshBranchesAsync(project) ?? StatusText.Text;
        }
        finally
        {
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
        }
    }

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || BranchList.SelectedItem is not BranchEntry { IsCurrent: false } branch) return;
        var source = branch.IsLocal ? "local revision" : "server revision";
        if (!confirm($"Project: {project.Name}\nSwitch to: {branch.Name} ({source})\n\nFiles may be added, changed, or deleted. Switching fails if Lore cannot preserve uncommitted changes.\n\nSwitch branches?", "Switch branch"))
            return;
        InvalidateHistory();
        busy = true;
        UpdateControls();
        var attempted = false;
        string message;
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            if (!branch.IsLocal)
            {
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, project.RepositoryId);
            }
            StatusText.Text = "Switching branches…";
            attempted = true;
            await branchService.SwitchAsync(project.LocalPath, branch);
            message = $"Switched to branch \"{branch.Name}\".";
        }
        catch (LoreError ex)
        {
            message = $"Branch switch failed (Lore error {ex.ReturnCode}). Files may have changed. Check local changes and the connection.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            message = attempted
                ? $"Could not switch branches. Files may have changed: {ex.Message}"
                : $"Could not switch branches: {ex.Message}";
        }
        if (attempted)
        {
            ShowProjectTree();
            ClearBranches();
            try
            {
                var result = await ReadBranchesAsync(project);
                DisplayBranches(result);
                if (!result.RemoteAvailable) message += " Could not retrieve server branches.";
            }
            catch (LoreError ex)
            {
                message += $" Could not verify branches after switching (Lore error {ex.ReturnCode}).";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                message += $" Could not verify branches after switching: {ex.Message}";
            }
        }
        StatusText.Text = message;
        busy = false;
        UpdateControls();
        LoadPendingFileStatus();
        LoadHistoryIfVisible();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeProject is null)
        {
            return;
        }
        try
        {
            var attributes = File.GetAttributes(activeProject.LocalPath);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                StatusText.Text = "The project folder was not found.";
                return;
            }
            Process.Start(new ProcessStartInfo(activeProject.LocalPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy || fileStatusRunning)
        {
            e.Cancel = true;
            StatusText.Text = "An operation is in progress. Please wait.";
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        authenticationStatusVersion++;
        authenticationStatusTimer.Stop();
        myLocksVersion++;
        historyVersion++;
        lockVersion++;
        treeCancellation.Cancel();
        treeCancellation.Dispose();
    }
}
