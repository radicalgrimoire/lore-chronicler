using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Chronicler.Servers;
using LoreVcs;

namespace Chronicler.Projects;

public partial class WorkspaceWindow : Window
{
    private readonly ServerProfile server;
    private readonly ProjectSettingsStore store;
    private readonly IRepositoryService repositoryService;
    private readonly ILatestService latestService;
    private readonly Func<string, string, bool> confirm;
    private readonly ObservableCollection<ProjectProfile> projects = [];
    private ProjectProfile? activeProject;
    private CancellationTokenSource treeCancellation = new();
    private bool available;
    private bool busy;

    public WorkspaceWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null, ILatestService? latestService = null)
    {
        this.server = server;
        this.store = store;
        this.repositoryService = repositoryService;
        this.latestService = latestService ?? new LoreLatestService();
        this.confirm = confirm ?? ((message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
        InitializeComponent();
        ServerText.Text = $"サーバー: {server.Name}";
        ProjectList.ItemsSource = projects;
        ReloadProjects();
        UpdateControls();
    }

    private void ReloadProjects(Guid? selectedId = null)
    {
        try
        {
            var loaded = store.Load().Where(project => project.ServerId == server.Id).ToArray();
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
        SwitchProjectButton.IsEnabled = available && selected && !busy;
        RemoveProjectButton.IsEnabled = available && selected && !busy;
        ProjectList.IsEnabled = !busy;
        RefreshTreeButton.IsEnabled = activeProject is not null && !busy;
        GetLatestButton.IsEnabled = activeProject is not null && !busy;
        OpenFolderButton.IsEnabled = activeProject is not null && !busy;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ProjectDetails.Text = ProjectList.SelectedItem is ProjectProfile project
            ? $"リポジトリ: {project.RepositoryName}\nフォルダー: {project.LocalPath}"
            : string.Empty;
        UpdateControls();
    }

    private void CreateProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProjectWindow(server, store, repositoryService) { Owner = this };
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

    private void SwitchProject(ProjectProfile project)
    {
        if (busy)
        {
            return;
        }
        if (!string.Equals(project.ServerUrl, server.RemoteUrl, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "登録時と現在のサーバーURLが異なります。サーバー設定を確認してください。";
            return;
        }
        try
        {
            var rootAttributes = File.GetAttributes(project.LocalPath);
            var loreAttributes = File.GetAttributes(Path.Combine(project.LocalPath, ".lore"));
            if ((rootAttributes & FileAttributes.Directory) == 0 || (loreAttributes & FileAttributes.Directory) == 0)
            {
                StatusText.Text = "ローカルのLore作業フォルダーが見つかりません。";
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"ローカルのLore作業フォルダーを確認できません: {ex.Message}";
            return;
        }
        activeProject = project;
        ActiveProjectText.Text = project.Name;
        Title = $"Chronicler - {project.Name}";
        ShowProjectTree();
        StatusText.Text = $"プロジェクト「{project.Name}」に切り替えました。表示はローカルファイルです。";
        UpdateControls();
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
        if (!confirm($"プロジェクト: {project.Name}\nフォルダー: {project.LocalPath}\n\n現在のブランチをサーバーで確認した最新リビジョンに更新します。ファイルの追加・変更・削除が発生する場合があります。\n\n取得を開始しますか？", "最新取得の確認"))
        {
            return;
        }
        busy = true;
        UpdateControls();
        StatusText.Text = "サーバーとローカルの履歴を確認しています…";
        try
        {
            var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
            var status = await latestService.GetLatestAsync(project.LocalPath, progress);
            StatusText.Text = $"ブランチ「{status.Branch}」を、サーバーで確認したリビジョンに更新しました。";
        }
        catch (LatestSyncException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"最新取得に失敗しました（Loreエラー {ex.ReturnCode}）。ファイルが一部変更された可能性があります。ローカルの状態、接続、ログインとアクセス権を確認してください。";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"最新取得を完了できませんでした（{ex.GetType().Name}）。ファイルが一部変更された可能性があります。ローカルの状態と実行環境を確認してください。";
        }
        finally
        {
            ShowProjectTree();
            busy = false;
            UpdateControls();
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
            StatusText.Text = "最新取得中です。完了するまでお待ちください。";
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        treeCancellation.Cancel();
        treeCancellation.Dispose();
    }
}
