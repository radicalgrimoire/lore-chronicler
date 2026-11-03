using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Chronicler.Servers;

public partial class ServerShortcutsWindow : Window
{
    private readonly ServerSettingsStore store;
    private readonly Func<string, string, bool> confirm;
    private readonly ObservableCollection<ServerProfile> servers = [];
    private ServerProfile? activeServer;
    private bool available;
    private bool updating;
    private bool dirty;

    public ServerShortcutsWindow(ServerSettingsStore store, Func<string, string, bool>? confirm = null)
    {
        this.store = store;
        this.confirm = confirm ?? ((message, title) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        InitializeComponent();
        ServerList.ItemsSource = servers;
        try
        {
            foreach (var server in store.Load())
                servers.Add(server);
            available = true;
            StatusText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"Could not load connections. The settings file was not changed.\n{ex.Message}";
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        ServerList.IsEnabled = NewButton.IsEnabled = NameInput.IsEnabled = UrlInput.IsEnabled = SaveButton.IsEnabled = available;
        RemoveButton.IsEnabled = available && activeServer is not null;
    }

    private void SetForm(ServerProfile? server)
    {
        updating = true;
        activeServer = server;
        ServerList.SelectedItem = server;
        NameInput.Text = server?.Name ?? string.Empty;
        UrlInput.Text = server?.RemoteUrl ?? string.Empty;
        dirty = false;
        updating = false;
        UpdateControls();
    }

    private bool ConfirmDiscard() => !dirty || confirm("Discard unsaved changes?", "Discard changes");

    private void Inputs_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updating && NameInput is not null && UrlInput is not null)
            dirty = NameInput.Text != (activeServer?.Name ?? string.Empty)
                || UrlInput.Text != (activeServer?.RemoteUrl ?? string.Empty);
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        var selected = ServerList.SelectedItem as ServerProfile;
        if (ConfirmDiscard()) SetForm(selected);
        else
        {
            updating = true;
            ServerList.SelectedItem = activeServer;
            updating = false;
        }
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        SetForm(null);
        NameInput.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        var url = UrlInput.Text.Trim();
        if (!ServerProfileValidator.TryValidate(name, url, out var error))
        {
            StatusText.Text = error;
            return;
        }
        if (name != activeServer?.Name && new StringInfo(name).LengthInTextElements > 32)
        {
            StatusText.Text = "Display names must be at most 32 characters.";
            return;
        }
        if (servers.Any(server => server.Id != activeServer?.Id
            && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "A connection with this display name already exists.";
            return;
        }
        var profile = new ServerProfile(activeServer?.Id ?? Guid.NewGuid(), name, url);
        var updated = servers.ToList();
        var index = activeServer is null ? updated.Count : updated.IndexOf(activeServer);
        if (activeServer is null) updated.Add(profile);
        else updated[index] = profile;
        if (!Save(updated)) return;
        updating = true;
        servers.Clear();
        foreach (var server in updated) servers.Add(server);
        updating = false;
        SetForm(profile);
        StatusText.Text = "Connection saved.";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null || !ConfirmDiscard()
            || !confirm("Delete this connection shortcut?\nExisting projects and Lore credentials will not be deleted.", "Delete connection"))
            return;
        var updated = servers.Where(server => server.Id != activeServer.Id).ToList();
        if (!Save(updated)) return;
        updating = true;
        servers.Clear();
        foreach (var server in updated) servers.Add(server);
        updating = false;
        SetForm(null);
        StatusText.Text = "Connection deleted.";
    }

    private bool Save(IReadOnlyCollection<ServerProfile> updated)
    {
        try
        {
            store.Save(updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"Could not save connections. The settings file was not changed.\n{ex.Message}";
            return false;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmDiscard();
}
