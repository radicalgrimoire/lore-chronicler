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
                serverSettingsError = $"Could not load connection shortcuts. The settings file was not changed.\n{ex.Message}";
            }
            foreach (var project in store.Load().OrderBy(project => project.ServerUrl, StringComparer.OrdinalIgnoreCase))
            {
                if (!servers.Any(item => ProjectRepositoryInfo.SameServer(
                    item is ServerShortcutOption shortcut ? shortcut.Url : (string)item, project.ServerUrl)))
                    servers.Add(project.ServerUrl);
            }
            ServerUrlInput.ItemsSource = servers;
            available = true;
            StatusText.Text = serverSettingsError ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"Could not load project settings. The settings file was not changed.\n{ex.Message}";
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
            StatusText.Text = "Reading the folder's server URL. Please wait.";
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
                StatusText.Text = "Repository retrieval cancelled.";
                return;
            }
            StatusText.Text = "Retrieving repositories…";
            var names = await repositoryService.ListAsync(url);
            RepositoryList.ItemsSource = names;
            listedServerUrl = url;
            StatusText.Text = names.Count == 0 ? "No repositories available." : $"Retrieved {names.Count} repositories.";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"Could not retrieve repositories (Lore error {ex.ReturnCode}). Check sign-in, permissions, connection, and certificates.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            StatusText.Text = $"Could not retrieve repositories ({ex.GetType().Name}). Check the SDK and environment.";
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
        var dialog = new OpenFolderDialog { Title = "Choose project folder" };
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
            StatusText.Text = "Check the folder or server URL.";
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
                throw new InvalidOperationException("The selected folder's .lore entry is not a directory.");

            activeFolderReadVersion = version;
            StatusText.Text = "Reading the folder's server URL.";
            var url = await repositoryInfo.ReadServerUrlAsync(path);
            if (!IsCurrent()) return;
            if (!ServerProfileValidator.TryValidate("server", url, out var error))
                throw new InvalidOperationException(error);
            ServerUrlInput.Text = url;
            StatusText.Text = "Read the Lore repository's server URL.";
        }
        catch (LoreError ex)
        {
            if (IsCurrent())
                StatusText.Text = $"Could not read the Lore server URL (Lore error {ex.ReturnCode}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (IsCurrent())
                StatusText.Text = $"Could not read the Lore server URL: {ex.Message}";
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
            StatusText.Text = "Reading the folder's server URL. Please wait.";
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
                throw new InvalidOperationException("This folder is already registered as a project.");

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
                    StatusText.Text = "Project creation cancelled.";
                    return;
                }
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(path), local.RemoteUrl, local.RepositoryId);
                var remote = await repositoryInfo.ReadRemoteAsync(path);
                ProjectRepositoryInfo.ValidateRemote(local, remote);
                var name = string.IsNullOrWhiteSpace(ProjectName.Text) ? remote.Name : ProjectName.Text.Trim();
                project = new ProjectProfile(Guid.NewGuid(), name, Guid.NewGuid(), local.RemoteUrl,
                    remote.Name, path, local.RepositoryId);
                if (!confirm($"Server: {local.RemoteUrl}\nRepository: {remote.Name}\nFolder: {path}\n\nRegister this existing clone?", "Create project"))
                    return;
                ProjectRepositoryInfo.Validate(await repositoryInfo.ReadLocalAsync(path), local.RemoteUrl, local.RepositoryId);
            }
            else
            {
                var exists = CloneDestination.ExistsAndIsEmpty(path);
                if (!exists)
                {
                    var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Could not determine the parent folder.");
                    if ((File.GetAttributes(parent) & FileAttributes.Directory) == 0)
                        throw new InvalidOperationException("The parent folder does not exist.");
                }
                if (listedServerUrl is null || RepositoryList.SelectedItem is not string repositoryName
                    || !ProjectValidator.IsValidRepositoryName(repositoryName) || string.IsNullOrWhiteSpace(ProjectName.Text))
                    throw new InvalidOperationException("Select a server and repository and enter a project name for the new clone.");
                project = new ProjectProfile(Guid.NewGuid(), ProjectName.Text.Trim(), Guid.NewGuid(), listedServerUrl,
                    repositoryName, path, CloneState: ProjectCloneState.Pending);
                if (!confirm($"Server: {listedServerUrl}\nRepository: {repositoryName}\nFolder: {path}\n\nCreate this project? Files will not be downloaded yet.", "Create project"))
                    return;
                CloneDestination.ExistsAndIsEmpty(path);
            }
            current = store.Load().ToList();
            if (current.Any(item => string.Equals(item.LocalPath, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This folder is already registered as a project.");
            current.Add(project);
            store.Save(current);
            CreatedProject = project;
            StatusText.Text = "Project created.";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"Could not verify the repository (Lore error {ex.ReturnCode}). Check sign-in, connection, and permissions.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            StatusText.Text = $"Could not create project: {ex.Message}";
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
            StatusText.Text = "Cannot close while an operation is in progress. Please wait.";
            e.Cancel = true;
        }

    }

    private void Window_Closed(object? sender, EventArgs e) => closed = true;

    private sealed record ServerShortcutOption(string Name, string Url)
    {
        public override string ToString() => $"{Name}({Url})";
    }
}
