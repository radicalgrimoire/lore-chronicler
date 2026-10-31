using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Chronicler.Servers;
using LoreVcs;
using Microsoft.Win32;

namespace Chronicler.Projects;

public partial class ProjectWindow : Window
{
    private readonly ServerProfile server;
    private readonly ProjectSettingsStore store;
    private readonly IRepositoryService repositoryService;
    private readonly Func<string, string, bool> confirm;
    private readonly List<ProjectProfile> projects = [];
    private bool available;
    private bool busy;
    private ProjectProfile? pending;
    public ProjectProfile? CreatedProject { get; private set; }

    public ProjectWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null)
    {
        this.server = server;
        this.store = store;
        this.repositoryService = repositoryService;
        this.confirm = confirm ?? ((message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        InitializeComponent();
        ServerTitle.Text = $"サーバー: {server.Name} ({server.RemoteUrl})";
        try
        {
            projects.AddRange(store.Load());
            available = true;
            StatusText.Text = "リポジトリ一覧を取得して、clone先を指定してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"プロジェクト設定を読み込めません。ファイルは変更されていません。\n{ex.Message}";
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        var editable = available && !busy && pending is null;
        FetchButton.IsEnabled = editable;
        RepositoryList.IsEnabled = editable;
        ProjectName.IsEnabled = editable;
        LocalPath.IsEnabled = editable;
        BrowseButton.IsEnabled = editable;
        BrowseParentButton.IsEnabled = editable;
        CloneButton.IsEnabled = editable && RepositoryList.SelectedItem is string;
        RetrySaveButton.Visibility = pending is null ? Visibility.Collapsed : Visibility.Visible;
        RetrySaveButton.IsEnabled = available && !busy;
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        busy = true;
        RepositoryList.ItemsSource = null;
        UpdateControls();
        StatusText.Text = "リポジトリ一覧を取得しています…";
        try
        {
            var names = await repositoryService.ListAsync(server.RemoteUrl);
            RepositoryList.ItemsSource = names;
            StatusText.Text = names.Count == 0 ? "表示できるリポジトリはありません。" : $"{names.Count}件のリポジトリを取得しました。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"一覧を取得できませんでした（Loreエラー {ex.ReturnCode}）。ログイン状態、アクセス権、接続と証明書を確認してください。";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            StatusText.Text = $"一覧を取得できませんでした（{ex.GetType().Name}）。SDKと実行環境を確認してください。";
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private void RepositoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var oldName = e.RemovedItems.OfType<string>().FirstOrDefault();
        if (RepositoryList.SelectedItem is string name
            && (string.IsNullOrWhiteSpace(ProjectName.Text) || ProjectName.Text == oldName))
        {
            ProjectName.Text = name;
        }
        UpdateControls();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "clone先のフォルダーを選択" };
        if (dialog.ShowDialog(this) == true)
        {
            LocalPath.Text = dialog.FolderName;
        }
    }

    private void BrowseParentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "clone先の親フォルダーを選択" };
        if (dialog.ShowDialog(this) == true)
        {
            var repositoryName = RepositoryList.SelectedItem as string;
            var folderName = repositoryName?.Split('/').LastOrDefault();
            LocalPath.Text = string.IsNullOrEmpty(folderName) ? dialog.FolderName : Path.Combine(dialog.FolderName, folderName);
        }
    }

    private async void CloneButton_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoryList.SelectedItem is not string repositoryName)
        {
            return;
        }
        var name = ProjectName.Text.Trim();
        if (name.Length == 0 || !ProjectValidator.IsValidRepositoryName(repositoryName))
        {
            StatusText.Text = "プロジェクト名または選択されたリポジトリ名が不正です。";
            return;
        }
        if (!ProjectValidator.TryNormalizePath(LocalPath.Text, out var path, out var error))
        {
            StatusText.Text = error;
            return;
        }
        if (projects.Any(project => string.Equals(project.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "このclone先は既にプロジェクトとして登録されています。";
            return;
        }
        bool destinationExists;
        try
        {
            destinationExists = CloneDestination.ExistsAndIsEmpty(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"clone先を確認できません: {ex.Message}";
            return;
        }
        if (!confirm($"リポジトリ: {repositoryName}\nclone先: {path}\n\ncloneを開始しますか？", "cloneの確認"))
        {
            return;
        }

        busy = true;
        UpdateControls();
        StatusText.Text = "clone中です。完了するまでお待ちください。";
        try
        {
            if (!destinationExists)
            {
                CloneDestination.Reserve(path);
            }
            CloneDestination.EnsureEmpty(path);
            var progress = new Progress<string>(message => { if (busy) StatusText.Text = message; });
            await repositoryService.CloneAsync(server.RemoteUrl, repositoryName, path, progress);
            pending = new ProjectProfile(Guid.NewGuid(), name, server.Id, server.RemoteUrl, repositoryName, path);
            TrySavePending();
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"cloneに失敗しました（Loreエラー {ex.ReturnCode}）。途中のファイルが残っている場合があります。clone先を確認してください。";
        }
        catch (Win32Exception ex)
        {
            StatusText.Text = $"clone先を作成できませんでした。既存のパスや親フォルダー、権限を確認してください。\n{ex.Message}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"cloneを完了できませんでした（{ex.GetType().Name}）。途中のファイルが残っている場合があります。clone先を確認してください。\n{ex.Message}";
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (CreatedProject is not null && IsVisible)
            {
                Close();
            }
        }
    }

    private void TrySavePending()
    {
        if (pending is null)
        {
            return;
        }
        try
        {
            var current = store.Load().ToList();
            if (current.Any(project => string.Equals(project.LocalPath, pending.LocalPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("clone先が既に別のプロジェクトに登録されています。");
            }
            current.Add(pending);
            store.Save(current);
            projects.Clear();
            projects.AddRange(current);
            CreatedProject = pending;
            pending = null;
            StatusText.Text = "cloneとプロジェクト登録が完了しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"cloneは完了しましたが、プロジェクト登録に失敗しました。フォルダーは削除されません。登録を再試行してください。\n{ex.Message}";
        }
    }

    private void RetrySaveButton_Click(object sender, RoutedEventArgs e)
    {
        TrySavePending();
        UpdateControls();
        if (CreatedProject is not null && IsVisible)
        {
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            StatusText.Text = "処理中はウィンドウを閉じられません。完了までお待ちください。";
            e.Cancel = true;
        }
        else if (pending is not null && MessageBox.Show(this,
            "clone済みですが、プロジェクトが未登録です。閉じるとこの画面からは登録を再試行できません。閉じますか？",
            "プロジェクト未登録", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }
}
