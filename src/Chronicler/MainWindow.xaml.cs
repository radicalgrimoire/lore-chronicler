using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Chronicler.Authentication;
using Chronicler.Servers;
using LoreVcs;

namespace Chronicler;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ServerProfile> servers = [];
    private readonly ILoginService loginService;
    private readonly ILoginBrowser loginBrowser;
    private readonly ServerSettingsStore store;
    private ServerProfile? activeServer;
    private bool updatingInputs;
    private bool dirty;
    private bool authenticating;
    private bool settingsAvailable;
    private string? loginUrl;
    private string? browserError;

    public MainWindow() : this(new ServerSettingsStore(ServerSettingsStore.DefaultPath), new LoreLoginService(), new LoginBrowser())
    {
    }

    public MainWindow(ServerSettingsStore store, ILoginService loginService, ILoginBrowser loginBrowser)
    {
        this.store = store;
        this.loginService = loginService;
        this.loginBrowser = loginBrowser;
        InitializeComponent();
        ServerList.ItemsSource = servers;
        LoadServers();
        UpdateControls();
    }

    private void LoadServers()
    {
        try
        {
            foreach (var server in store.Load())
            {
                servers.Add(server);
            }
            settingsAvailable = true;
            FooterStatus.Text = "サーバーを保存してからログインしてください。起動時に接続は行いません。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            settingsAvailable = false;
            FooterStatus.Text = "サーバー設定を読み込めません。設定ファイルは変更されていません。";
            MessageBox.Show(this, $"{FooterStatus.Text}\n\n{ex.Message}", "設定の読み込みエラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateControls()
    {
        var editable = settingsAvailable && !authenticating;
        ServerList.IsEnabled = editable;
        NameInput.IsEnabled = editable;
        UrlInput.IsEnabled = editable;
        NewButton.IsEnabled = editable;
        SaveButton.IsEnabled = editable;
        RemoveButton.IsEnabled = editable && activeServer is not null;
        LoginButton.IsEnabled = editable && activeServer is not null;
    }

    private void SetForm(ServerProfile? server)
    {
        updatingInputs = true;
        activeServer = server;
        NameInput.Text = server?.Name ?? string.Empty;
        UrlInput.Text = server?.RemoteUrl ?? string.Empty;
        updatingInputs = false;
        dirty = false;
        LoginStatus.Text = string.Empty;
        UserCodeText.Text = string.Empty;
        UpdateControls();
    }

    private bool ConfirmDiscard()
    {
        return !dirty || MessageBox.Show(this, "保存していない変更を破棄しますか？", "未保存の変更",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void Inputs_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updatingInputs && NameInput is not null && UrlInput is not null)
        {
            dirty = NameInput.Text != (activeServer?.Name ?? string.Empty)
                || UrlInput.Text != (activeServer?.RemoteUrl ?? string.Empty);
        }
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingInputs)
        {
            return;
        }
        if (!ConfirmDiscard())
        {
            updatingInputs = true;
            ServerList.SelectedItem = activeServer;
            updatingInputs = false;
            return;
        }
        SetForm(ServerList.SelectedItem as ServerProfile);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }
        updatingInputs = true;
        ServerList.SelectedItem = null;
        updatingInputs = false;
        SetForm(null);
        NameInput.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        var remoteUrl = UrlInput.Text.Trim();
        if (!ServerProfileValidator.TryValidate(name, remoteUrl, out var error))
        {
            MessageBox.Show(this, error, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (servers.Any(s => s.Id != activeServer?.Id
            && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "同じ表示名のサーバーが既にあります。", "入力エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profile = new ServerProfile(activeServer?.Id ?? Guid.NewGuid(), name, remoteUrl);
        var updated = servers.Where(s => s.Id != profile.Id).ToList();
        var index = activeServer is null ? updated.Count : servers.IndexOf(activeServer);
        updated.Insert(index, profile);
        if (!SaveServers(updated))
        {
            return;
        }
        updatingInputs = true;
        servers.Clear();
        foreach (var server in updated)
        {
            servers.Add(server);
        }
        ServerList.SelectedItem = profile;
        updatingInputs = false;
        SetForm(profile);
        FooterStatus.Text = "サーバー情報を保存しました。";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null || !ConfirmDiscard())
        {
            return;
        }
        if (MessageBox.Show(this, "このサーバー設定を削除しますか？\nLoreの認証情報は削除されません。",
            "サーバーの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var updated = servers.Where(s => s.Id != activeServer.Id).ToList();
        if (!SaveServers(updated))
        {
            return;
        }
        updatingInputs = true;
        servers.Clear();
        foreach (var server in updated)
        {
            servers.Add(server);
        }
        updatingInputs = false;
        SetForm(null);
        FooterStatus.Text = "サーバー情報を削除しました。";
    }

    private bool SaveServers(IReadOnlyCollection<ServerProfile> updated)
    {
        try
        {
            store.Save(updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            MessageBox.Show(this, $"サーバー情報を保存できませんでした。\n\n{ex.Message}",
                "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null)
        {
            return;
        }
        if (dirty)
        {
            MessageBox.Show(this, "ログインする前にサーバー設定を保存してください。", "未保存の変更",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        authenticating = true;
        loginUrl = null;
        browserError = null;
        LoginStatus.Text = "サーバーに接続してログインを開始しています…";
        UserCodeText.Text = string.Empty;
        OpenBrowserButton.Visibility = Visibility.Collapsed;
        UpdateControls();
        var progress = new Progress<LoginProgress>(OnLoginProgress);
        try
        {
            var userName = await loginService.LoginAsync(activeServer.RemoteUrl, progress);
            LoginStatus.Text = string.IsNullOrWhiteSpace(userName)
                ? "ログインが完了しました。"
                : $"{userName} としてログインしました。";
            FooterStatus.Text = "認証情報はLore SDKが管理します。";
        }
        catch (LoreError ex)
        {
            LoginStatus.Text = $"ログインに失敗しました（Loreエラー {ex.ReturnCode}）。サーバーへの接続、証明書、SDKの互換性を確認してください。";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"ログインを開始または完了できませんでした（{ex.GetType().Name}）。SDKと実行環境を確認してください。";
        }
        finally
        {
            authenticating = false;
            loginUrl = null;
            browserError = null;
            UserCodeText.Text = string.Empty;
            OpenBrowserButton.Visibility = Visibility.Collapsed;
            UpdateControls();
        }
    }

    private void OnLoginProgress(LoginProgress progress)
    {
        if (!authenticating)
        {
            return;
        }
        if (progress.Url is not null)
        {
            if (!LoginBrowser.IsSafeUrl(progress.Url))
            {
                browserError = "認証先URLが安全なHTTPS URLではないため、ブラウザーを開けません。";
                LoginStatus.Text = browserError;
                return;
            }
            var firstUrl = loginUrl is null;
            loginUrl = progress.Url;
            UserCodeText.Text = string.IsNullOrEmpty(progress.UserCode)
                ? string.Empty
                : $"認証コード: {progress.UserCode}";
            OpenBrowserButton.Visibility = Visibility.Visible;
            if (firstUrl)
            {
                OpenLoginBrowser();
            }
        }
        else if (progress.RemainingSeconds is { } remaining && browserError is null)
        {
            LoginStatus.Text = $"ブラウザーでログインを完了してください（残り約{remaining}秒）。";
        }
    }

    private void OpenLoginBrowser()
    {
        if (loginUrl is null)
        {
            return;
        }
        try
        {
            loginBrowser.Open(loginUrl);
            browserError = null;
            LoginStatus.Text = "ブラウザーでログインを完了してください。";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            browserError = "ブラウザーを開けませんでした。既定のブラウザーを確認して、もう一度お試しください。";
            LoginStatus.Text = browserError;
        }
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e) => OpenLoginBrowser();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (authenticating && MessageBox.Show(this, "ログイン処理中です。Chroniclerを終了しますか？",
            "ログイン処理中", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
        }
    }
}
