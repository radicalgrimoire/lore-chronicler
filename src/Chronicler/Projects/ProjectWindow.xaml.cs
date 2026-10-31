using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Chronicler.Authentication;
using Chronicler.Servers;
using LoreVcs;
using Microsoft.Win32;

namespace Chronicler.Projects;

public partial class ProjectWindow : Window
{
    private readonly ProjectSettingsStore store;
    private readonly IRepositoryService repositoryService;
    private readonly IProjectRepositoryInfo repositoryInfo;
    private readonly Func<string, string, bool> confirm;
    private readonly Func<string, bool> authenticate;
    private bool available;
    private bool busy;
    private string? listedServerUrl;
    public ProjectProfile? CreatedProject { get; private set; }

    public ProjectWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null, IProjectRepositoryInfo? repositoryInfo = null,
        Func<string, bool>? authenticate = null)
    {
        this.store = store;
        this.repositoryService = repositoryService;
        this.repositoryInfo = repositoryInfo ?? new ProjectRepositoryInfo();
        this.confirm = confirm ?? ((message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        this.authenticate = authenticate ?? (url => new LoginWindow(url) { Owner = this }.ShowDialog() == true);
        InitializeComponent();
        ServerUrlInput.Text = server.RemoteUrl;
        try
        {
            store.Load();
            available = true;
            StatusText.Text = "フォルダーを指定してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"プロジェクト設定を読み込めません。ファイルは変更されていません。\n{ex.Message}";
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        var editable = available && !busy;
        FetchButton.IsEnabled = ServerUrlInput.IsEnabled = LoginButton.IsEnabled = editable;
        RepositoryList.IsEnabled = ProjectName.IsEnabled = LocalPath.IsEnabled = editable;
        BrowseButton.IsEnabled = BrowseParentButton.IsEnabled = CreateButton.IsEnabled = editable;
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlInput.Text.Trim();
        if (!ServerProfileValidator.TryValidate("server", url, out var error))
        {
            StatusText.Text = error;
            return;
        }
        busy = true;
        RepositoryList.ItemsSource = null;
        listedServerUrl = null;
        UpdateControls();
        StatusText.Text = "リポジトリ一覧を取得しています…";
        try
        {
            var names = await repositoryService.ListAsync(url);
            RepositoryList.ItemsSource = names;
            listedServerUrl = url;
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

    private void ServerUrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RepositoryList is null) return;
        RepositoryList.ItemsSource = null;
        listedServerUrl = null;
        UpdateControls();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var url = ServerUrlInput.Text.Trim();
        if (!ServerProfileValidator.TryValidate("server", url, out var error))
        {
            StatusText.Text = error;
            return;
        }
        authenticate(url);
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
        var dialog = new OpenFolderDialog { Title = "プロジェクトフォルダーを選択" };
        if (dialog.ShowDialog(this) == true) LocalPath.Text = dialog.FolderName;
    }

    private void BrowseParentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "プロジェクトフォルダーの親を選択" };
        if (dialog.ShowDialog(this) == true)
        {
            var folderName = (RepositoryList.SelectedItem as string)?.Split('/').LastOrDefault();
            LocalPath.Text = string.IsNullOrEmpty(folderName) ? dialog.FolderName : Path.Combine(dialog.FolderName, folderName);
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ProjectValidator.TryNormalizePath(LocalPath.Text, out var path, out var error))
        {
            StatusText.Text = error;
            return;
        }
        busy = true;
        UpdateControls();
        try
        {
            var current = store.Load().ToList();
            if (current.Any(project => string.Equals(project.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("このフォルダーは既にプロジェクトとして登録されています。");

            var lorePath = Path.Combine(path, ".lore");
            bool hasLore;
            try { File.GetAttributes(lorePath); hasLore = true; }
            catch (FileNotFoundException) { hasLore = false; }
            catch (DirectoryNotFoundException) { hasLore = false; }

            ProjectProfile project;
            if (hasLore)
            {
                var local = await repositoryInfo.ReadLocalAsync(path);
                if (!ServerProfileValidator.TryValidate("server", local.RemoteUrl, out error))
                    throw new InvalidOperationException(error);
                if (!authenticate(local.RemoteUrl))
                {
                    StatusText.Text = "プロジェクトの作成を中止しました。";
                    return;
                }
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(path), local.RemoteUrl, local.RepositoryId);
                var remote = await repositoryInfo.ReadRemoteAsync(path);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                var name = string.IsNullOrWhiteSpace(ProjectName.Text) ? remote.Name : ProjectName.Text.Trim();
                project = new ProjectProfile(Guid.NewGuid(), name, Guid.NewGuid(), local.RemoteUrl,
                    remote.Name, path, local.RepositoryId);
                if (!confirm($"サーバー: {local.RemoteUrl}\nリポジトリ: {remote.Name}\nフォルダー: {path}\n\n既存のcloneを登録しますか？", "プロジェクト作成"))
                    return;
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(path), local.RemoteUrl, local.RepositoryId);
            }
            else
            {
                var exists = CloneDestination.ExistsAndIsEmpty(path);
                if (!exists)
                {
                    var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("親フォルダーを確認できません。");
                    if ((File.GetAttributes(parent) & FileAttributes.Directory) == 0)
                        throw new InvalidOperationException("親フォルダーが存在しません。");
                }
                if (listedServerUrl is null || RepositoryList.SelectedItem is not string repositoryName
                    || !ProjectValidator.IsValidRepositoryName(repositoryName) || string.IsNullOrWhiteSpace(ProjectName.Text))
                    throw new InvalidOperationException("新規cloneにはサーバー、リポジトリ、プロジェクト名を指定してください。");
                project = new ProjectProfile(Guid.NewGuid(), ProjectName.Text.Trim(), Guid.NewGuid(), listedServerUrl,
                    repositoryName, path, CloneState: ProjectCloneState.Pending);
                if (!confirm($"サーバー: {listedServerUrl}\nリポジトリ: {repositoryName}\nフォルダー: {path}\n\nプロジェクトを作成しますか？ ファイルの取得は行いません。", "プロジェクト作成"))
                    return;
                CloneDestination.ExistsAndIsEmpty(path);
            }
            current = store.Load().ToList();
            if (current.Any(item => string.Equals(item.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("このフォルダーは既にプロジェクトとして登録されています。");
            current.Add(project);
            store.Save(current);
            CreatedProject = project;
            StatusText.Text = "プロジェクトを作成しました。";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"リポジトリを確認できません（Loreエラー {ex.ReturnCode}）。ログイン状態、接続とアクセス権を確認してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            StatusText.Text = $"プロジェクトを作成できません: {ex.Message}";
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (CreatedProject is not null && IsVisible) Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            StatusText.Text = "処理中はウィンドウを閉じられません。完了までお待ちください。";
            e.Cancel = true;
        }
    }
}
