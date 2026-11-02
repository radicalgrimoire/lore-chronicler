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
    private bool closed;
    private bool projectNameEdited;
    private bool settingSuggestedProjectName;
    private int folderReadVersion;
    private int? activeFolderReadVersion;
    private string? listedServerUrl;
    public ProjectProfile? CreatedProject { get; private set; }

    public ProjectWindow(ServerProfile server, ProjectSettingsStore store, IRepositoryService repositoryService,
        Func<string, string, bool>? confirm = null, IProjectRepositoryInfo? repositoryInfo = null,
        Func<string, bool>? authenticate = null, ServerSettingsStore? serverStore = null)
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
            var servers = new List<object>();
            string? serverSettingsError = null;
            try
            {
                serverStore ??= new ServerSettingsStore(Path.Combine(store.DirectoryPath, "servers.json"));
                foreach (var shortcut in serverStore.Load().OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                    servers.Add(new ServerShortcutOption(shortcut.Name, shortcut.RemoteUrl));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                serverSettingsError = $"接続先ショートカットを読み込めません。ファイルは変更されていません。\n{ex.Message}";
            }
            foreach (var project in store.Load().OrderBy(project => project.ServerUrl, StringComparer.OrdinalIgnoreCase))
            {
                if (!servers.Any(item => ProjectRepositoryInfo.SameServer(
                    item is ServerShortcutOption shortcut ? shortcut.Url : (string)item, project.ServerUrl)))
                    servers.Add(project.ServerUrl);
            }
            ServerUrlInput.ItemsSource = servers;
            available = true;
            StatusText.Text = serverSettingsError ?? "フォルダーを指定してください。";
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
        FetchButton.IsEnabled = ServerUrlInput.IsEnabled = editable;
        RepositoryList.IsEnabled = ProjectName.IsEnabled = LocalPath.IsEnabled = editable;
        BrowseButton.IsEnabled = CreateButton.IsEnabled = editable;
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeFolderReadVersion is not null)
        {
            StatusText.Text = "フォルダーのサーバーURLを読み取り中です。完了までお待ちください。";
            return;
        }
        var url = ServerUrlInput.SelectedItem is ServerShortcutOption shortcut
            && ServerUrlInput.Text == shortcut.ToString() ? shortcut.Url : ServerUrlInput.Text.Trim();
        if (!ServerProfileValidator.TryValidate("server", url, out var error))
        {
            StatusText.Text = error;
            return;
        }
        busy = true;
        RepositoryList.ItemsSource = null;
        listedServerUrl = null;
        UpdateControls();
        try
        {
            if (!authenticate(url))
            {
                StatusText.Text = "リポジトリ一覧の取得を中止しました。";
                return;
            }
            StatusText.Text = "リポジトリ一覧を取得しています…";
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
        CancelFolderRead();
        RepositoryList.ItemsSource = null;
        listedServerUrl = null;
        UpdateControls();
    }

    private void ServerUrlInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepositoryList is null) return;
        CancelFolderRead();
        RepositoryList.ItemsSource = null;
        listedServerUrl = null;
        UpdateControls();
    }

    private void RepositoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RepositoryList.SelectedItem is string name
            && !projectNameEdited)
        {
            settingSuggestedProjectName = true;
            try
            {
                ProjectName.Text = name;
            }
            finally
            {
                settingSuggestedProjectName = false;
            }
        }
        UpdateControls();
    }

    private void ProjectName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!settingSuggestedProjectName && sender is TextBox textBox)
            projectNameEdited = !string.IsNullOrWhiteSpace(textBox.Text);
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "プロジェクトフォルダーを選択" };
        if (dialog.ShowDialog(this) == true)
        {
            LocalPath.Text = dialog.FolderName;
            await ReadFolderServerAsync();
        }
    }

    private void CancelFolderRead()
    {
        folderReadVersion++;
        if (activeFolderReadVersion is not null)
        {
            activeFolderReadVersion = null;
            StatusText.Text = "フォルダーまたはサーバーURLを確認してください。";
        }
    }

    private void LocalPath_TextChanged(object sender, TextChangedEventArgs e) => CancelFolderRead();

    private async void LocalPath_LostFocus(object sender, RoutedEventArgs e) => await ReadFolderServerAsync();

    private async Task ReadFolderServerAsync()
    {
        var version = ++folderReadVersion;
        if (!available || busy || !ProjectValidator.TryNormalizePath(LocalPath.Text, out var path, out _))
            return;
        var previousUrl = ServerUrlInput.Text;
        bool IsCurrent() => !closed && !busy && version == folderReadVersion && ServerUrlInput.Text == previousUrl;
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(Path.Combine(path, ".lore")); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("選択したフォルダーの .lore はフォルダーではありません。");

            activeFolderReadVersion = version;
            StatusText.Text = "フォルダーのサーバーURLを読み取り中です。";
            var url = await repositoryInfo.ReadServerUrlAsync(path);
            if (!IsCurrent()) return;
            if (!ServerProfileValidator.TryValidate("server", url, out var error))
                throw new InvalidOperationException(error);
            ServerUrlInput.Text = url;
            StatusText.Text = "LoreリポジトリのサーバーURLを読み取りました。";
        }
        catch (LoreError ex)
        {
            if (IsCurrent())
                StatusText.Text = $"LoreのサーバーURLを読み取れません（Loreエラー {ex.ReturnCode}）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent())
                StatusText.Text = $"LoreのサーバーURLを読み取れません: {ex.Message}";
        }
        finally
        {
            if (activeFolderReadVersion == version)
                activeFolderReadVersion = null;
        }
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeFolderReadVersion is not null)
        {
            StatusText.Text = "フォルダーのサーバーURLを読み取り中です。完了までお待ちください。";
            return;
        }
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

    private void Window_Closed(object? sender, EventArgs e) => closed = true;

    private sealed record ServerShortcutOption(string Name, string Url)
    {
        public override string ToString() => $"{Name}（{Url}）";
    }
}
