using System.ComponentModel;
using System.Windows;
using LoreVcs;

namespace Chronicler.Authentication;

public partial class LoginWindow : Window
{
    private readonly string serverUrl;
    private readonly ILoginService service;
    private readonly ILoginBrowser browser;
    private bool busy;
    private string? loginUrl;

    public LoginWindow(string serverUrl, ILoginService? service = null, ILoginBrowser? browser = null,
        bool autoStart = false)
    {
        this.serverUrl = serverUrl;
        this.service = service ?? new LoreLoginService();
        this.browser = browser ?? new LoginBrowser();
        InitializeComponent();
        ServerText.Text = $"サーバー: {serverUrl}";
        if (autoStart)
        {
            Loaded += (_, _) => Login_Click(this, new RoutedEventArgs());
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        busy = true;
        LoginButton.IsEnabled = ContinueButton.IsEnabled = CancelButton.IsEnabled = false;
        StatusText.Text = "ログインを開始しています…";
        try
        {
            var progress = new Progress<LoginProgress>(OnProgress);
            await service.LoginAsync(serverUrl, progress);
            busy = false;
            DialogResult = true;
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"ログインに失敗しました（Loreエラー {ex.ReturnCode}）。接続と証明書を確認してください。";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            StatusText.Text = $"ログインできません: {ex.Message}";
        }
        finally
        {
            busy = false;
            LoginButton.IsEnabled = ContinueButton.IsEnabled = CancelButton.IsEnabled = true;
            BrowserButton.Visibility = Visibility.Collapsed;
            loginUrl = null;
            CodeText.Text = string.Empty;
        }
    }

    private void OnProgress(LoginProgress progress)
    {
        if (!busy) return;
        if (progress.Url is not null)
        {
            if (!LoginBrowser.IsSafeUrl(progress.Url))
            {
                StatusText.Text = "安全なHTTPS認証先ではないため、ブラウザーを開けません。";
                return;
            }
            var first = loginUrl is null;
            loginUrl = progress.Url;
            CodeText.Text = string.IsNullOrEmpty(progress.UserCode) ? string.Empty : $"認証コード: {progress.UserCode}";
            BrowserButton.Visibility = Visibility.Visible;
            if (first) OpenBrowser();
        }
        else if (progress.RemainingSeconds is { } remaining)
        {
            StatusText.Text = $"ブラウザーでログインを完了してください（残り約{remaining}秒）。";
        }
    }

    private void OpenBrowser()
    {
        if (loginUrl is null) return;
        try
        {
            browser.Open(loginUrl);
            StatusText.Text = "ブラウザーでログインを完了してください。";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "ブラウザーを開けませんでした。既定のブラウザーを確認して再試行してください。";
        }
    }

    private void Browser_Click(object sender, RoutedEventArgs e) => OpenBrowser();
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            StatusText.Text = "ログイン処理中です。完了までお待ちください。";
        }
    }
}
