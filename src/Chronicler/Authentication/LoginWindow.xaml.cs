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
        ServerText.Text = $"Server: {serverUrl}";
        if (autoStart)
        {
            Loaded += (_, _) => Login_Click(this, new RoutedEventArgs());
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        busy = true;
        LoginButton.IsEnabled = ContinueButton.IsEnabled = CancelButton.IsEnabled = false;
        StatusText.Text = "Starting sign-in…";
        try
        {
            var progress = new Progress<LoginProgress>(OnProgress);
            await service.LoginAsync(serverUrl, progress);
            busy = false;
            DialogResult = true;
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"Sign-in failed (Lore error {ex.ReturnCode}). Check the connection and certificate.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            StatusText.Text = $"Could not sign in: {ex.Message}";
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
                StatusText.Text = "Cannot open the browser because the authentication URL is not secure HTTPS.";
                return;
            }
            var first = loginUrl is null;
            loginUrl = progress.Url;
            CodeText.Text = string.IsNullOrEmpty(progress.UserCode) ? string.Empty : $"Authorization code: {progress.UserCode}";
            BrowserButton.Visibility = Visibility.Visible;
            if (first) OpenBrowser();
        }
        else if (progress.RemainingSeconds is { } remaining)
        {
            StatusText.Text = $"Complete sign-in in your browser (about {remaining} seconds remaining).";
        }
    }

    private void OpenBrowser()
    {
        if (loginUrl is null) return;
        try
        {
            browser.Open(loginUrl);
            StatusText.Text = "Complete sign-in in your browser.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText.Text = "Could not open the browser. Check your default browser and try again.";
        }
    }

    private void Browser_Click(object sender, RoutedEventArgs e) => OpenBrowser();
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            StatusText.Text = "Sign-in is in progress. Please wait.";
        }
    }
}
