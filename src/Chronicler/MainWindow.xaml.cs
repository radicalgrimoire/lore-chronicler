using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Chronicler.Authentication;
using Chronicler.Projects;
using Chronicler.Servers;
using LoreVcs;

namespace Chronicler;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ServerProfile> servers = [];
    private readonly ILoginService loginService;
    private readonly ILoginBrowser loginBrowser;
    private readonly ServerSettingsStore store;
    private readonly ProjectSettingsStore projectStore;
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
        projectStore = new ProjectSettingsStore(Path.Combine(store.DirectoryPath, "projects.json"));
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
            FooterStatus.Text = "Save a server before signing in. No connection is made at startup.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            settingsAvailable = false;
            FooterStatus.Text = "Could not load server settings. The settings file was not changed.";
            MessageBox.Show(this, $"{FooterStatus.Text}\n\n{ex.Message}", "Settings error",
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
        ProjectsButton.IsEnabled = editable && activeServer is not null;
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
        return !dirty || MessageBox.Show(this, "Discard unsaved changes?", "Unsaved changes",
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
            MessageBox.Show(this, error, "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (servers.Any(s => s.Id != activeServer?.Id
            && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A server with this display name already exists.", "Invalid input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (activeServer is not null && !string.Equals(activeServer.RemoteUrl, remoteUrl, StringComparison.OrdinalIgnoreCase)
            && !CanChangeServer(activeServer.Id))
        {
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
        FooterStatus.Text = "Server saved.";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null || !ConfirmDiscard())
        {
            return;
        }
        if (!CanChangeServer(activeServer.Id))
        {
            return;
        }
        if (MessageBox.Show(this, "Delete this server setting?\nLore credentials will not be deleted.",
            "Delete server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
        FooterStatus.Text = "Server deleted.";
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
            MessageBox.Show(this, $"Could not save server settings.\n\n{ex.Message}",
                "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool CanChangeServer(Guid serverId)
    {
        try
        {
            if (projectStore.Load().Any(project => project.ServerId == serverId))
            {
                MessageBox.Show(this, "This server has registered projects. It cannot be deleted or its URL changed.",
                    "Server settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Could not verify project settings. Server settings were not changed.\n\n{ex.Message}",
                "Settings error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null)
        {
            return;
        }
        if (dirty)
        {
            MessageBox.Show(this, "Save the server settings before opening projects.", "Unsaved changes",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenWorkspace(activeServer);
    }

    private void OpenWorkspace(ServerProfile server)
    {
        var window = new WorkspaceWindow(server, projectStore, new LoreRepositoryService()) { Owner = this };
        window.ShowDialog();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null)
        {
            return;
        }
        if (dirty)
        {
            MessageBox.Show(this, "Save the server settings before signing in.", "Unsaved changes",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        authenticating = true;
        loginUrl = null;
        browserError = null;
        LoginStatus.Text = "Connecting to the server and starting sign-in…";
        UserCodeText.Text = string.Empty;
        OpenBrowserButton.Visibility = Visibility.Collapsed;
        UpdateControls();
        var progress = new Progress<LoginProgress>(OnLoginProgress);
        var loginSucceeded = false;
        try
        {
            var userName = await loginService.LoginAsync(activeServer.RemoteUrl, progress);
            LoginStatus.Text = string.IsNullOrWhiteSpace(userName)
                ? "Sign-in completed."
                : $"Signed in as {userName}.";
            FooterStatus.Text = "Credentials are managed by the Lore SDK.";
            loginSucceeded = true;
        }
        catch (LoreError ex)
        {
            LoginStatus.Text = $"Sign-in failed (Lore error {ex.ReturnCode}). Check the server connection, certificate, and SDK compatibility.";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"Could not start or complete sign-in ({ex.GetType().Name}). Check the SDK and environment.";
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
        if (loginSucceeded && IsVisible && activeServer is not null)
        {
            OpenWorkspace(activeServer);
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
                browserError = "Cannot open the browser because the authentication URL is not secure HTTPS.";
                LoginStatus.Text = browserError;
                return;
            }
            var firstUrl = loginUrl is null;
            loginUrl = progress.Url;
            UserCodeText.Text = string.IsNullOrEmpty(progress.UserCode)
                ? string.Empty
                : $"Authorization code: {progress.UserCode}";
            OpenBrowserButton.Visibility = Visibility.Visible;
            if (firstUrl)
            {
                OpenLoginBrowser();
            }
        }
        else if (progress.RemainingSeconds is { } remaining && browserError is null)
        {
            LoginStatus.Text = $"Complete sign-in in your browser (about {remaining} seconds remaining).";
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
            LoginStatus.Text = "Complete sign-in in your browser.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            browserError = "Could not open the browser. Check your default browser and try again.";
            LoginStatus.Text = browserError;
        }
    }

    private void OpenBrowserButton_Click(object sender, RoutedEventArgs e) => OpenLoginBrowser();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (authenticating && MessageBox.Show(this, "Sign-in is in progress. Exit Chronicler?",
            "Sign-in in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
