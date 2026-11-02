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
            StatusText.Text = "接続先を選択するか、新規登録してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            StatusText.Text = $"接続先設定を読み込めません。ファイルは変更されていません。\n{ex.Message}";
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

    private bool ConfirmDiscard() => !dirty || confirm("保存していない変更を破棄しますか？", "変更の破棄");

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
            StatusText.Text = "表示名は32文字以内で入力してください。";
            return;
        }
        if (servers.Any(server => server.Id != activeServer?.Id
            && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "同じ表示名の接続先が既にあります。";
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
        StatusText.Text = "接続先を保存しました。";
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (activeServer is null || !ConfirmDiscard()
            || !confirm("この接続先ショートカットを削除しますか？\n既存プロジェクトとLoreの認証情報は削除されません。", "接続先の削除"))
            return;
        var updated = servers.Where(server => server.Id != activeServer.Id).ToList();
        if (!Save(updated)) return;
        updating = true;
        servers.Clear();
        foreach (var server in updated) servers.Add(server);
        updating = false;
        SetForm(null);
        StatusText.Text = "接続先を削除しました。";
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
            StatusText.Text = $"接続先設定を保存できません。ファイルは変更されていません。\n{ex.Message}";
            return false;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmDiscard();
}
