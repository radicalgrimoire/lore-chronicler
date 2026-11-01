using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly IStageService stageService;
    private readonly ICommitService commitService;
    private readonly IPushService pushService;
    private readonly Func<string, string, bool> confirm;
    private readonly Func<string, string?> selectProjectName;
    private readonly Func<string, bool> authenticate;
    private readonly ObservableCollection<ProjectProfile> projects = [];
    private readonly Dictionary<Guid, string> commitDrafts = [];
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
        IStageService? stageService = null, ICommitService? commitService = null, IPushService? pushService = null)
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
        this.stageService = stageService ?? new LoreStageService();
        this.commitService = commitService ?? new LoreCommitService();
        this.pushService = pushService ?? new LorePushService();
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
        ServerText.Text = "サーバー: 未選択";
        ProjectList.ItemsSource = projects;
        ReloadProjects();
        UpdateControls();
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
        StageButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is not null
            && FileTree.SelectedItem is TreeViewItem { Tag: TreeItemInfo { IsReparsePoint: false } };
        UnstageButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is not null && StageList.SelectedItems.Count > 0;
        CommitMessageText.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        CommitButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !fileStatusRunning && fileStatus is { UnresolvedConflictCount: 0 }
            && StageList.Items.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessageText.Text);
    }

    private void SetActiveProject(ProjectProfile? project)
    {
        var changed = activeProject?.Id != project?.Id;
        activeProject = project;
        if (changed)
            CommitMessageText.Text = project is null ? string.Empty : commitDrafts.GetValueOrDefault(project.Id, string.Empty);
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
            ? $"サーバー: {project.ServerUrl}\nリポジトリ: {project.RepositoryName}\nフォルダー: {project.LocalPath}"
            : string.Empty;
        UpdateControls();
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
            StatusText.Text = $"プロジェクト「{project.Name}」に切り替えました。表示はローカルファイルです。";
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
        fileStatusVersion++;
        lockVersion++;
        fileStatus = null;
        fileLocks = null;
        fileStatusNeedsRefresh = activeProject is { CloneState: ProjectCloneState.Ready };
        lockNeedsRefresh = fileStatusNeedsRefresh;
        FileStatusText.Text = fileStatusNeedsRefresh ? "変更状態の確認を待っています…" : "変更状態は未取得です。";
        StageList.ItemsSource = null;
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
        var root = CreateDirectoryItem(activeProject.Name, activeProject.LocalPath);
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
                    var child = new TreeViewItem { Tag = new TreeItemInfo(entry.Name + suffix, entry.Path, entry.IsDirectory, entry.IsReparsePoint) };
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

    private sealed record TreeItemInfo(string Name, string Path, bool IsDirectory, bool IsReparsePoint = false);

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => UpdateControls();

    private void StageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private async void CommitButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is not { UnresolvedConflictCount: 0 } previous
            || StageList.Items.Count == 0 || string.IsNullOrWhiteSpace(CommitMessageText.Text)
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;

        var message = CommitMessageText.Text;
        var expected = previous.StagedEntries;
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
            ApplyFileStatus(project, latest);
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
            && string.Equals(pair.First.FullPath, pair.Second.FullPath, StringComparison.OrdinalIgnoreCase));

    private async void StageButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is null
            || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || FileTree.SelectedItem is not TreeViewItem { Tag: TreeItemInfo { IsReparsePoint: false } info }) return;
        WorkspaceTabs.SelectedItem = StageTab;
        await ChangeStageAsync(project, [info.Path], stage: true);
    }

    private async void UnstageButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || fileStatus is null
            || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        var paths = StageList.SelectedItems.OfType<StagedEntry>().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        await ChangeStageAsync(project, paths, stage: false);
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
            if (stage) await stageService.StageAsync(project.LocalPath, paths[0]);
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
        StageList.ItemsSource = null;
        StageStatusText.Text = "ステージ状態を再確認しています…";
        FileStatusText.Text = "変更状態を再確認しています…";
        foreach (var root in FileTree.Items.OfType<TreeViewItem>()) UpdateTreeItem(root);
    }

    private void ApplyFileStatus(ProjectProfile project, FileStatusSnapshot result)
    {
        fileStatus = result;
        foreach (var root in FileTree.Items.OfType<TreeViewItem>()) UpdateTreeItem(root);
        FileStatusText.Text = "変更状態を表示しています（前回の確認時点）。";
        var staged = result.StagedEntries;
        StageList.ItemsSource = staged;
        StageStatusText.Text = staged.Count == 0 ? "ステージ済みの変更はありません。" : $"{staged.Count}件（前回の確認時点）。フォルダーのアンステージは配下も対象です。";
        if (result.UnresolvedConflictCount > 0)
            StageStatusText.Text += $" 未解決の競合が{result.UnresolvedConflictCount}件あり、一覧には含まれていません。";
    }

    private void UpdateTreeItem(TreeViewItem item)
    {
        if (item.Tag is not TreeItemInfo info) return;
        var changed = fileStatus is not null && (info.IsDirectory
            ? fileStatus.HasDirectoryChanges(info.Path)
            : fileStatus.GetFileStatus(info.Path) != FileChangeKind.None);
        item.Header = info.Name;
        AutomationProperties.SetItemStatus(item, changed ? "変更あり" : string.Empty);
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
            LockStatusText.Text = result.Warning is null
                ? "ロック状態を表示しています（前回の確認時点）。"
                : $"ロック状態を表示しています（前回の確認時点）。\n{result.Warning}";
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
        FileStatusText.Text = "変更状態を確認しています…";
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
            ApplyFileStatus(project, result);
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) StageStatusText.Text = FileStatusText.Text = $"変更状態を確認できません（Loreエラー {ex.ReturnCode}）。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent()) StageStatusText.Text = FileStatusText.Text = $"変更状態を確認できません: {ex.Message}";
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
            var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
            ProjectRepositoryInfo.ValidateRemote(local, remote);
            if (!string.Equals(remote.Name, project.RepositoryName, StringComparison.Ordinal))
                throw new InvalidOperationException("サーバーのリポジトリ名が登録情報と一致しません。再登録してください。");
            ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(project.LocalPath), project.ServerUrl, local.RepositoryId);
            var status = await pushService.PrepareAsync(project.LocalPath);
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
                StatusText.Text = "初回取得が完了しました。";
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
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            var remote = await repositoryInfo.ReadRemoteAsync(project.LocalPath);
            ProjectRepositoryInfo.ValidateRemote(local, remote);
            var checkedLocal = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(checkedLocal, project.ServerUrl, project.RepositoryId);
            var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
            syncStarted = true;
            var status = await latestService.GetLatestAsync(project.LocalPath, progress);
            ClearBranches();
            StatusText.Text = $"ブランチ「{status.Branch}」を、サーバーで確認したリビジョンに更新しました。";
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

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is ProjectProfile project)
        {
            StatusText.Text = authenticate(project.ServerUrl)
                ? "認証操作が終了しました。必要に応じて操作をやり直してください。"
                : "ログインを中止しました。";
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
        if (e.Source == WorkspaceTabs) LoadHistoryIfVisible();
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

    private async void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        busy = true;
        ClearBranches();
        UpdateControls();
        StatusText.Text = "ブランチ一覧を取得しています…";
        try
        {
            var result = await ReadBranchesAsync(project);
            DisplayBranches(result);
            StatusText.Text = result.RemoteAvailable
                ? "ブランチ一覧を取得しました。"
                : "ローカルのブランチを表示しています。サーバーの一覧は取得できませんでした。接続、ログイン状態、アクセス権を確認してください。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"ブランチ一覧を取得できません（Loreエラー {ex.ReturnCode}）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            StatusText.Text = $"ブランチ一覧を取得できません: {ex.Message}";
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
        historyVersion++;
        lockVersion++;
        treeCancellation.Cancel();
        treeCancellation.Dispose();
    }
}
