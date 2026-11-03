using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LoreVcs;

namespace Chronicler.Projects;

public partial class RevisionChangesWindow : Window
{
    private readonly Func<Task<IReadOnlyList<RevisionChange>>> readChanges;
    private readonly Func<RevisionChange, Task<FileDiff>>? readDiff;
    private bool closed;
    private bool loading;

    public RevisionChangesWindow(string projectName, RevisionHistoryEntry revision,
        Func<Task<IReadOnlyList<RevisionChange>>> readChanges,
        Func<RevisionChange, Task<FileDiff>>? readDiff = null)
    {
        this.readChanges = readChanges;
        this.readDiff = readDiff;
        InitializeComponent();
        RevisionSummary.Text = $"Project: {projectName} / Revision {revision.Number} / {revision.DisplayTimestamp}";
        RevisionMessage.Text = $"Message: {revision.DisplayMessage}";
        RevisionAuthor.Text = $"Author: {revision.DisplayAuthor}";
        RevisionId.Text = $"Revision ID: {revision.Revision}";
        ShowFileDiffButton.IsEnabled = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (loading || closed) return;
        loading = true;
        RetryButton.IsEnabled = false;
        RevisionChangesList.ItemsSource = null;
        ChangesStatus.Text = "Loading changed files (connecting to the server if needed)…";
        try
        {
            var changes = await readChanges();
            if (closed) return;
            RevisionChangesList.ItemsSource = changes;
            ShowFileDiffButton.IsEnabled = false;
            ChangesStatus.Text = changes.Count == 0 ? "No changes in this revision." : $"Showing {changes.Count} changes.";
        }
        catch (LoreError ex)
        {
            if (!closed) ChangesStatus.Text = $"Could not retrieve revision changes (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (!closed) ChangesStatus.Text = $"Could not retrieve revision changes: {ex.Message}";
        }
        finally
        {
            loading = false;
            if (!closed) RetryButton.IsEnabled = true;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void RevisionChangesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ShowFileDiffButton.IsEnabled = readDiff is not null && RevisionChangesList.SelectedItem is RevisionChange { IsFile: true };

    private void RevisionChangesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(RevisionChangesList, source) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void RevisionChangesList_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        ShowFileDiffMenu.IsEnabled = ShowFileDiffButton.IsEnabled;

    private void ShowFileDiff_Click(object sender, RoutedEventArgs e)
    {
        if (readDiff is null || RevisionChangesList.SelectedItem is not RevisionChange { IsFile: true } change) return;
        new FileDiffWindow(change, () => readDiff(change)) { Owner = this }.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closed(object? sender, EventArgs e) => closed = true;
}
