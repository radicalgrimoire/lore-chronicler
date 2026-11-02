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
        ServerText.Text = "サーバー: 未選択";
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
            StatusText.Text = "右側からプロジェクトを選択するか、新規作成してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            available = false;
            StatusText.Text = $"プロジェクト設定を読み込めません。ファイルは変更されていません。\n{ex.Message}";
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
        activeProject = project;
        if (changed)
        {
            collapsedChangeFolders.Clear();
            CommitMessageText.Text = project is null ? string.Empty : commitDrafts.GetValueOrDefault(project.Id, string.Empty);
        }
    }

    private void CommitMessageText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (activeProject is not null) commitDrafts[activeProject.Id] = CommitMessageText.Text;
        UpdateControls();
    }

    private async void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProjectDetails.Text = ProjectList.SelectedItem is ProjectProfile project
            ? $"サーバー: {project.ServerUrl}\nリポジトリ: {project.RepositoryName}\nフォルダー: {project.LocalPath}"
            : string.Empty;
        UpdateControls();
        await RefreshAuthenticationStatusAsync();
    }

    private async Task RefreshAuthenticationStatusAsync()
    {
        var version = ++authenticationStatusVersion;
        if (closed) return;
        AuthenticationStatusText.Foreground = Brushes.DimGray;
        AuthenticationStatusText.ToolTip = "保存済み認証情報の期限（日本時間）です。サーバーへの接続確認は行いません。";
        if (ProjectList.SelectedItem is not ProjectProfile project)
        {
            AuthenticationStatusText.Text = "認証状態: プロジェクトを選択してください。";
            return;
        }
        AuthenticationStatusText.ToolTip = $"プロジェクト: {project.Name}\n{AuthenticationStatusText.ToolTip}";
        if (project.CloneState != ProjectCloneState.Ready)
        {
            AuthenticationStatusText.Text = "認証状態: 初回取得後に確認できます。";
            return;
        }
        AuthenticationStatusText.Text = "認証状態を確認しています…";
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
            AuthenticationStatusText.Text = $"認証状態を確認できません（Loreエラー {ex.ReturnCode}）。";
            AuthenticationStatusText.Foreground = Brushes.Firebrick;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or OverflowException)
        {
            if (closed || version != authenticationStatusVersion) return;
            AuthenticationStatusText.Text = $"認証状態を確認できません: {ex.Message}";
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
                ActiveProjectText.Text = project.Name;
                ServerText.Text = $"サーバー: {project.ServerUrl}";
                Title = $"Chronicler - {project.Name}";
                ShowProjectTree();
                StatusText.Text = project.CloneState == ProjectCloneState.Pending
                    ? "初回取得待ちです。「サーバーから取得」でcloneします。"
                    : "前回のcloneが完了していません。フォルダーを確認し、登録を削除してから作り直してください。";
                return;
            }
            var rootAttributes = File.GetAttributes(project.LocalPath);
            var loreAttributes = ReadLoreAttributes(project.LocalPath);
            if ((rootAttributes & FileAttributes.Directory) == 0 || (loreAttributes & FileAttributes.Directory) == 0)
            {
                StatusText.Text = "ローカルのLore作業フォルダーが見つかりません。";
                return;
            }
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            if (project.RepositoryId is null)
            {
                StatusText.Text = "既存プロジェクトのリポジトリ情報をサーバーで確認しています…";
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("サーバーのリポジトリ名が登録情報と一致しません。再登録してください。");
                }
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                var current = store.Load().ToList();
                var index = current.FindIndex(item => item == project);
                if (index < 0)
                {
                    throw new InvalidOperationException("プロジェクトが別のプロセスで変更されました。");
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
            ServerText.Text = $"サーバー: {project.ServerUrl}";
            ActiveProjectText.Text = project.Name;
            Title = $"Chronicler - {project.Name}";
            ShowProjectTree();
            var branchStatus = await RefreshBranchesAsync(project);
            StatusText.Text = $"プロジェクト「{project.Name}」に切り替えました。表示はローカルファイルです。"
                + (branchStatus is null or "ブランチ一覧を取得しました。" ? string.Empty : $"\n{branchStatus}");
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"リポジトリを確認できません（Loreエラー {ex.ReturnCode}）。ログイン状態、接続とアクセス権を確認してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            StatusText.Text = $"ローカルのLore作業フォルダーを確認できません: {ex.Message}";
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
        CommitTargetText.Text = "コミット対象：未確認";
        StageStatusText.Text = fileStatusNeedsRefresh ? "ステージ状態の確認を待っています…"
            : activeProject is null ? "プロジェクトを選択してください。" : "初回取得後に表示できます。";
        LockStatusText.Text = lockNeedsRefresh ? "ロック状態の確認を待っています…" : "ロック状態は未取得です。";
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
        var root = CreateDirectoryItem($"{localFolderName} (ローカル)", activeProject.LocalPath);
        FileTree.Items.Add(root);
        root.IsExpanded = true;
        LoadPendingFileStatus();
    }

    private TreeViewItem CreateDirectoryItem(string name, string path)
    {
        var item = new TreeViewItem { Tag = new TreeItemInfo(name, path, true) };
        UpdateTreeItem(item);
        item.Items.Add("読み込み中…");
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
                    var suffix = entry.IsReparsePoint ? " (リンク)" : string.Empty;
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
                item.Items.Add("読み込めません。更新してください。");
                StatusText.Text = $"フォルダーを読み込めません: {ex.Message}";
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
            ? $"\nほか {expected.Count - 20}件（「ステージ済み一覧」で確認できます）" : string.Empty;
        if (!confirm($"ステージ済みの変更 {expected.Count}件をローカルにコミットします。表示フィルターや選択は対象に影響しません。\n\n{preview}{remainder}\n\nステージ後に編集したファイルはコミット時の内容が使われます。",
                "コミットの確認"))
        {
            StatusText.Text = "コミットをキャンセルしました。";
            return;
        }
        var scanCompleted = false;
        var attempted = false;
        busy = true;
        UpdateControls();
        StatusText.Text = "コミット前にステージ状態を確認しています…";
        StageStatusText.Text = "コミット前にステージ状態を確認しています…";
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
                StatusText.Text = "ステージ状態が変わったためコミットを中止しました。一覧と競合を確認してください。";
                return;
            }

            InvalidateHistory();
            attempted = true;
            StatusText.Text = "ローカルにコミットしています…";
            StageStatusText.Text = "コミットしています…";
            await commitService.CommitAsync(project.LocalPath, message);
            CommitMessageText.Clear();
            StatusText.Text = "ローカルにコミットしました。サーバーへの送信は行っていません。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = attempted
                ? $"コミットを確認できません（Loreエラー {ex.ReturnCode}）。再試行前に更新ログとステージ状態を確認してください。\n{ex.Message}"
                : $"コミット前の確認に失敗しました（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = attempted
                ? $"コミットを確認できません。再試行前に更新ログとステージ状態を確認してください。\n{ex.Message}"
                : $"コミット前の確認に失敗しました: {ex.Message}";
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
        var remainder = folders.Length > 20 ? $"\nほか {folders.Length - 20}フォルダー" : string.Empty;
        var operation = stage ? "ステージ" : "アンステージ";
        var warning = stage
            ? "フォルダー配下を再帰的に走査します。表示フィルターで見えないステージ済みの項目にも影響する可能性があります。"
            : "表示フィルターや種類に関係なく、フォルダー配下のステージ済みの変更すべてが対象です。";
        if (confirm($"{warning}\n\n{paths}{remainder}\n\n{operation}しますか？", $"{operation}の確認")) return true;
        StatusText.Text = $"{operation}をキャンセルしました。";
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
        var operation = stage ? "ステージ" : "アンステージ";
        busy = true;
        UpdateControls();
        StatusText.Text = $"{operation}しています…";
        StageStatusText.Text = $"{operation}しています…";
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
            StatusText.Text = $"{operation}しました。状態を再確認します。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"{operation}に失敗しました（Loreエラー {ex.ReturnCode}）。一部が変更された可能性があります。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = $"{operation}を完了できません。{(attempted ? "一部が変更された可能性があります。 " : string.Empty)}{ex.Message}";
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
        CommitTargetText.Text = "コミット対象：未確認";
        StageStatusText.Text = "ステージ状態を再確認しています…";
    }

    private void ApplyFileStatus(FileStatusSnapshot result)
    {
        fileStatus = result;
        var staged = result.StagedEntries;
        var unstaged = result.UnstagedEntries;
        SetChangeList(result);
        CommitTargetText.Text = $"コミット対象：ステージ済み {staged.Count}件（前回の確認時点）";
        StageStatusText.Text = staged.Count == 0 && unstaged.Count == 0
            ? "変更はありません（前回の確認時点）。"
            : $"未ステージ {unstaged.Count(entry => entry.Action != ChangeAction.Folder)}件、"
                + (staged.Count == 0 ? "ステージ済みの変更はありません" : $"ステージ済み {staged.Count}件")
                + "（前回の確認時点）。フォルダーの操作は配下も対象です。";
        if (result.UnresolvedConflictCount > 0)
            StageStatusText.Text += $" 未解決の競合が{result.UnresolvedConflictCount}件あり、一覧には含まれていません。";
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
            tooltip = "Loreのロック取得者:\n" + string.Join("\n", owners.Select(owner =>
                $"{(string.IsNullOrWhiteSpace(owner.Name) ? (string.IsNullOrWhiteSpace(owner.Id) ? "不明" : owner.Id) : owner.Name)}（ブランチID: {owner.Branch}）"));
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
        LockStatusText.Text = "サーバーのロック状態を確認しています…";
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
            LockStatusText.Text = result.Warning ?? string.Empty;
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) LockStatusText.Text = $"ロック状態を確認できません（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) LockStatusText.Text = $"ロック状態を確認できません: {ex.Message}";
        }
    }

    private async Task LoadFileStatusAsync(ProjectProfile project, int version)
    {
        fileStatusNeedsRefresh = false;
        fileStatusRunning = true;
        StageStatusText.Text = "ステージ状態を確認しています…";
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
            if (IsCurrent()) StageStatusText.Text = $"変更状態を確認できません（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) StageStatusText.Text = $"変更状態を確認できません: {ex.Message}";
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
            StatusText.Text = "プッシュ前にサーバーとローカルの履歴を確認しています…";
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var status = await RetryAfterAuthenticationAsync(project, async () =>
            {
                var status = await pushService.PrepareAsync(project.LocalPath);
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                    throw new InvalidOperationException("サーバーのリポジトリ名が登録情報と一致しません。再登録してください。");
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                return status;
            });
            if (!confirm($"プロジェクト: {project.Name}\nサーバー: {project.ServerUrl}\nリポジトリ: {project.RepositoryName}\nブランチ: {status.Branch}\nローカルのリビジョン: {status.LocalRevision}\n\nコミット済みの履歴を送信します。未コミットの変更は送信しません。リンク先やレイヤーのリポジトリも送信される場合があります。\nサーバーにブランチがない場合や削除済みの場合は、作成または復元されることがあります。\n\nプッシュを開始しますか？", "プッシュの確認"))
            {
                StatusText.Text = "プッシュを中止しました。";
                return;
            }

            ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
            var progress = new Progress<string>(message => { if (progressActive && !closed) StatusText.Text = message; });
            StatusText.Text = "プッシュ中です…";
            attempted = true;
            await pushService.PushAsync(project.LocalPath, status, progress);
            progressActive = false;
            StatusText.Text = "プッシュ後のサーバー状態を確認しています…";
            if (await pushService.VerifyAsync(project.LocalPath, status))
                StatusText.Text = $"ブランチ「{status.Branch}」をプッシュしました。";
            else
                StatusText.Text = "プッシュ処理は完了しましたが、サーバーのリビジョンを確認できません。再試行前にサーバーの状態を確認してください。";
        }
        catch (PushException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (RemoteAuthenticationRequiredException)
        {
            StatusText.Text = "サーバーの認証を確認できません。ログイン状態とアクセス権を確認してください。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = attempted
                ? $"プッシュを完了できませんでした（Loreエラー {ex.ReturnCode}）。一部が送信された可能性があります。再試行前にサーバーの状態を確認してください。\n{ex.Message}"
                : $"プッシュ前の確認に失敗しました（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = attempted
                ? $"プッシュまたは送信後の確認に失敗しました。一部が送信された可能性があります。再試行前にサーバーの状態を確認してください。\n{ex.Message}"
                : $"プッシュ前の確認に失敗しました: {ex.Message}";
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
            StatusText.Text = "前回のcloneが完了していません。フォルダーを確認し、登録を削除してから作り直してください。";
            return;
        }
        if (project.CloneState == ProjectCloneState.Pending)
        {
            await ClonePendingProjectAsync(project);
            return;
        }
        if (!confirm($"プロジェクト: {project.Name}\nフォルダー: {project.LocalPath}\n\n現在のブランチをサーバーで確認した最新リビジョンに更新します。ファイルの追加・変更・削除が発生する場合があります。\n\n取得を開始しますか？", "最新取得の確認"))
        {
            return;
        }

        async Task ClonePendingProjectAsync(ProjectProfile project)
        {
            if (!confirm($"サーバー: {project.ServerUrl}\nリポジトリ: {project.RepositoryName}\nフォルダー: {project.LocalPath}\n\n初回cloneを開始しますか？", "初回取得"))
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
                    throw new InvalidOperationException("プロジェクトが別のプロセスで変更されました。");
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
                StatusText.Text = "clone中です。完了するまでお待ちください。";
                var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
                await repositoryService.CloneAsync(project.ServerUrl, project.RepositoryName, project.LocalPath, progress);
                var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
                ProjectRepositoryInfo.Validate(local, project.ServerUrl);
                var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                    throw new InvalidOperationException("cloneしたリポジトリ名が登録情報と一致しません。");
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
                current = store.Load().ToList();
                index = current.FindIndex(item => item == inProgress);
                if (index < 0)
                    throw new InvalidOperationException("プロジェクトが別のプロセスで変更されました。");
                var completed = inProgress with { CloneState = ProjectCloneState.Ready, RepositoryId = local.RepositoryId };
                current[index] = completed;
                store.Save(current);
                SetActiveProject(completed);
                InvalidateHistory();
                ReloadProjects(completed.Id);
                ShowProjectTree();
                var branchStatus = await RefreshBranchesAsync(completed);
                StatusText.Text = "初回取得が完了しました。"
                    + (branchStatus is null or "ブランチ一覧を取得しました。" ? string.Empty : $"\n{branchStatus}");
            }
            catch (LoreError ex)
            {
                StatusText.Text = $"初回取得に失敗しました（Loreエラー {ex.ReturnCode}）。フォルダーを確認し、登録を削除して作り直してください。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                or JsonException or Win32Exception)
            {
                StatusText.Text = started
                    ? $"初回取得を完了できません。フォルダーを確認し、登録を削除して作り直してください: {ex.Message}"
                    : $"初回取得を開始できません: {ex.Message}";
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
        StatusText.Text = "サーバーとローカルの履歴を確認しています…";
        var syncStarted = false;
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
            var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
            syncStarted = true;
            var status = await RetryAfterAuthenticationAsync(project,
                () => latestService.GetLatestAsync(project.LocalPath, progress));
            ClearBranches();
            StatusText.Text = $"ブランチ「{status.Branch}」を、サーバーで確認したリビジョンに更新しました。";
        }
        catch (RemoteAuthenticationRequiredException)
        {
            StatusText.Text = "サーバーの認証を確認できません。ログイン状態とアクセス権を確認してください。";
        }
        catch (LatestSyncException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (LoreError ex)
        {
            StatusText.Text = syncStarted
                ? $"最新取得に失敗しました（Loreエラー {ex.ReturnCode}）。ファイルが一部変更された可能性があります。ローカルの状態、接続、ログインとアクセス権を確認してください。"
                : $"リポジトリを確認できません（Loreエラー {ex.ReturnCode}）。接続、ログインとアクセス権を確認してください。";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = syncStarted
                ? $"最新取得を完了できませんでした。ファイルが一部変更された可能性があります: {ex.Message}"
                : $"リポジトリを確認できません: {ex.Message}";
        }
        finally
        {
            ShowProjectTree();
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
                StatusText.Text = "プロジェクト名を入力してください。";
                return;
            }
            try
            {
                var current = store.Load().ToList();
                var index = current.FindIndex(item => item == project);
                if (index < 0)
                {
                    throw new InvalidOperationException("プロジェクトが別のプロセスで変更されました。");
                }
                var updated = project with { Name = name };
                current[index] = updated;
                store.Save(current);
                if (activeProject?.Id == project.Id)
                {
                    SetActiveProject(updated);
                    ActiveProjectText.Text = updated.Name;
                    Title = $"Chronicler - {updated.Name}";
                    ShowProjectTree();
                }
                ReloadProjects(updated.Id);
                StatusText.Text = "プロジェクト名を変更しました。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                StatusText.Text = $"プロジェクト名を変更できません: {ex.Message}";
            }
        }
    private void RemoveProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectProfile project
            || !confirm("このプロジェクトの登録を削除しますか？\nローカルフォルダーやサーバー上のデータは削除されません。", "プロジェクト登録の削除"))
        {
            return;
        }
        try
        {
            var current = store.Load().ToList();
            if (current.RemoveAll(item => item == project) != 1)
            {
                throw new InvalidOperationException("プロジェクトが別のプロセスで変更または削除されました。");
            }
            store.Save(current);
            if (activeProject?.Id == project.Id)
            {
                SetActiveProject(null);
                commitDrafts.Remove(project.Id);
                InvalidateHistory();
                ClearBranches();
                ServerText.Text = "サーバー: 未選択";
                ActiveProjectText.Text = "プロジェクトが選択されていません";
                Title = "Chronicler";
                ShowProjectTree();
            }

            ReloadProjects();
            if (available)
            {
                StatusText.Text = "プロジェクトの登録を削除しました。ローカルフォルダーは残っています。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"プロジェクトの登録を削除できませんでした。\n{ex.Message}";
        }
        UpdateControls();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectProfile project)
        {
            StatusText.Text = authenticate(project.ServerUrl)
                ? "認証操作が終了しました。必要に応じて操作をやり直してください。"
                : "ログインを中止しました。";
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
            throw new InvalidOperationException("Loreの管理フォルダーが見つかりません。フォルダーを確認し、必要なら新たにcloneしてください。", ex);
        }
    }

    private void ClearBranches()
    {
        BranchList.ItemsSource = null;
        CurrentBranchText.Text = "ブランチ: 未取得";
    }

    private void InvalidateHistory()
    {
        historyVersion++;
        historyLoading = false;
        historyNeedsRefresh = true;
        RevisionHistoryList.ItemsSource = null;
        HistoryStatus.Text = activeProject switch
        {
            null => "プロジェクトを選択してください。",
            { CloneState: ProjectCloneState.Pending } => "初回取得後に履歴を表示できます。",
            { CloneState: ProjectCloneState.Started } => "cloneが完了していないため、履歴を表示できません。",
            _ => "履歴を取得していません。"
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
        HistoryStatus.Text = "リビジョン履歴を取得しています…（必要に応じてサーバーに接続します）";
        UpdateControls();
        bool IsCurrent() => !closed && version == historyVersion && activeProject?.Id == project.Id;
        var operation = "ローカルリポジトリの確認";
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            if (!IsCurrent()) return;
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            operation = "リビジョン履歴の取得";
            var entries = await historyService.ReadAsync(project.LocalPath);
            if (!IsCurrent()) return;
            RevisionHistoryList.ItemsSource = entries;
            HistoryStatus.Text = entries.Count == 0
                ? "ローカルのリビジョン履歴はありません。"
                : $"{entries.Count}件を表示しています（最大100件）。";
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) HistoryStatus.Text = $"{operation}に失敗しました（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) HistoryStatus.Text = $"履歴を取得できません: {ex.Message}";
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
            ? $"ブランチ: {current[0].Name}"
            : "ブランチ: 確認できません";
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
                ? "ブランチ一覧を取得しました。"
                : "ローカルのブランチを表示しています。サーバーの一覧は取得できませんでした。接続、ログイン状態、アクセス権を確認してください。";
        }
        catch (LoreError ex)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "ブランチ: 確認できません";
            return $"ブランチ一覧を取得できません（Loreエラー {ex.ReturnCode}）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "ブランチ: 確認できません";
            return $"ブランチ一覧を取得できません: {ex.Message}";
        }
    }

    private async void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        busy = true;
        UpdateControls();
        StatusText.Text = "ブランチ一覧を取得しています…";
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
        var source = branch.IsLocal ? "ローカルのリビジョン" : "サーバーのリビジョン";
        if (!confirm($"プロジェクト: {project.Name}\n切り替え先: {branch.Name}（{source}）\n\n作業ファイルが追加・変更・削除される場合があります。未コミットの変更はLoreが保持できない場合、切り替えに失敗します。\n\n切り替えますか？", "ブランチ切り替え"))
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
            StatusText.Text = "ブランチを切り替えています…";
            attempted = true;
            await branchService.SwitchAsync(project.LocalPath, branch);
            message = $"ブランチ「{branch.Name}」に切り替えました。";
        }
        catch (LoreError ex)
        {
            message = $"ブランチ切り替えに失敗しました（Loreエラー {ex.ReturnCode}）。ファイルが一部変更された可能性があります。ローカルの変更や接続を確認してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            message = attempted
                ? $"ブランチを切り替えられません。ファイルが一部変更された可能性があります: {ex.Message}"
                : $"ブランチを切り替えられません: {ex.Message}";
        }
        if (attempted)
        {
            ShowProjectTree();
            ClearBranches();
            try
            {
                var result = await ReadBranchesAsync(project);
                DisplayBranches(result);
                if (!result.RemoteAvailable) message += " サーバーのブランチ一覧は取得できませんでした。";
            }
            catch (LoreError ex)
            {
                message += $" 切り替え後のブランチ状態を確認できません（Loreエラー {ex.ReturnCode}）。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                message += $" 切り替え後のブランチ状態を確認できません: {ex.Message}";
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
                StatusText.Text = "プロジェクトのフォルダーが見つかりません。";
                return;
            }
            Process.Start(new ProcessStartInfo(activeProject.LocalPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"フォルダーを開けませんでした: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy || fileStatusRunning)
        {
            e.Cancel = true;
            StatusText.Text = "処理中です。完了するまでお待ちください。";
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
