using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
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
    private readonly Func<string, string, bool> confirm;
    private readonly Func<string, string?> selectProjectName;
    private readonly Func<string, bool> authenticate;
    private readonly ObservableCollection<ProjectProfile> projects = [];
    private ProjectProfile? activeProject;
    private CancellationTokenSource treeCancellation = new();
    private bool available;
    private bool busy;
    private bool closed;
    private bool historyLoading;
    private bool historyNeedsRefresh = true;
    private int historyVersion;

    public WorkspaceWindow() : this(new ServerProfile(Guid.NewGuid(), "server", string.Empty),
        new ProjectSettingsStore(ProjectSettingsStore.DefaultPath), new LoreRepositoryService())
    {
    }

    public WorkspaceWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null, ILatestService? latestService = null,
        IProjectRepositoryInfo? repositoryInfo = null,
        Func<string, string?>? selectProjectName = null, Func<string, bool>? authenticate = null,
        IBranchService? branchService = null, IRevisionHistoryService? historyService = null)
    {
        this.server = server;
        this.store = store;
        this.repositoryService = repositoryService;
        this.latestService = latestService ?? new LoreLatestService();
        this.repositoryInfo = repositoryInfo ?? new ProjectRepositoryInfo();
        this.branchService = branchService ?? new LoreBranchService();
        this.historyService = historyService ?? new LoreRevisionHistoryService();
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
        GetLatestButton.IsEnabled = activeProject is not null && !busy;
        OpenFolderButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        BranchList.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        RefreshBranchesButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
        SwitchBranchButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && BranchList.SelectedItem is BranchEntry { IsCurrent: false } && !busy;
        RefreshHistoryButton.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready }
            && !busy && !historyLoading;
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
                activeProject = project;
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
            activeProject = project;
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
            LoadHistoryIfVisible();
        }
    }

    private void ShowProjectTree()
    {
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
    }

    private TreeViewItem CreateDirectoryItem(string name, string path)
    {
        var item = new TreeViewItem { Header = name, Tag = path };
        item.Items.Add("読み込み中…");
        item.Expanded += Directory_Expanded;
        return item;
    }

    private async void Directory_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || !ReferenceEquals(e.OriginalSource, item)
            || item.Tag is not string path || activeProject is null
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
                    item.Items.Add(new TreeViewItem { Header = entry.Name + suffix, Tag = entry.Path });
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

    private async void GetLatestButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { } project)
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
                activeProject = inProgress;
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
                activeProject = completed;
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
                    activeProject = updated;
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
                activeProject = null;
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
        }
    }

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project
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
        if (busy)
        {
            e.Cancel = true;
            StatusText.Text = "処理中です。完了するまでお待ちください。";
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        historyVersion++;
        treeCancellation.Cancel();
        treeCancellation.Dispose();
    }
}
