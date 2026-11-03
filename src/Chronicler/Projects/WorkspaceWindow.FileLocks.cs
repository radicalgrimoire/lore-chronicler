using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LoreVcs;

namespace Chronicler.Projects;

public partial class WorkspaceWindow
{
    private int myLocksVersion;
    private bool myLocksLoading;
    private bool myLocksNeedRefresh = true;

    private void UpdateFileLockControls()
    {
        var ready = activeProject is { CloneState: ProjectCloneState.Ready } && !busy && !fileStatusRunning;
        RefreshMyLocksButton.IsEnabled = ready && !myLocksLoading;
        ReleaseMyLockButton.IsEnabled = ready && !myLocksLoading && MyLocksList.SelectedItem is MyFileLock;
    }

    private void InvalidateMyLocks()
    {
        myLocksVersion++;
        myLocksLoading = false;
        myLocksNeedRefresh = true;
        MyLocksList.ItemsSource = null;
        MyLocksStatusText.Text = activeProject is { CloneState: ProjectCloneState.Ready }
            ? "Your locks have not been loaded."
            : activeProject is null ? "Open a project." : "Available after initial download.";
    }

    private void LoadMyLocksIfVisible()
    {
        if (!closed && !busy && !fileStatusRunning && !myLocksLoading && myLocksNeedRefresh
            && WorkspaceTabs.SelectedItem == StageTab && MyLocksView.Visibility == Visibility.Visible
            && activeProject is { CloneState: ProjectCloneState.Ready } project)
            _ = LoadMyLocksAsync(project);
    }

    private void SwitchChangesViewButton_Click(object sender, RoutedEventArgs e)
    {
        var showLocks = MyLocksView.Visibility != Visibility.Visible;
        ChangesView.Visibility = showLocks ? Visibility.Collapsed : Visibility.Visible;
        MyLocksView.Visibility = showLocks ? Visibility.Visible : Visibility.Collapsed;
        SwitchChangesViewButton.Content = showLocks ? "Back to Changes" : "Show My Locks";
        if (showLocks) LoadMyLocksIfVisible();
    }

    private async Task LoadMyLocksAsync(ProjectProfile project)
    {
        var version = ++myLocksVersion;
        bool IsCurrent() => !closed && version == myLocksVersion && activeProject?.Id == project.Id;
        myLocksLoading = true;
        myLocksNeedRefresh = false;
        MyLocksList.ItemsSource = null;
        MyLocksStatusText.Text = $"Loading your locks in \"{project.Name}\"...";
        UpdateControls();
        try
        {
            await ValidateLockRepositoryAsync(project);
            var entries = await fileLockActionsService.QueryMineAsync(project.LocalPath);
            if (!IsCurrent()) return;
            MyLocksList.ItemsSource = entries;
            MyLocksStatusText.Text = $"Project: {project.Name} / Your locks: {entries.Count} (all branches)";
        }
        catch (LoreError ex)
        {
            if (IsCurrent()) MyLocksStatusText.Text = $"Could not retrieve locks (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (IsFileLockError(ex))
        {
            if (IsCurrent()) MyLocksStatusText.Text = $"Could not retrieve locks: {ex.Message}";
        }
        finally
        {
            if (IsCurrent())
            {
                myLocksLoading = false;
                UpdateControls();
            }
        }
    }

    private async Task ValidateLockRepositoryAsync(ProjectProfile project)
    {
        ReadLoreAttributes(project.LocalPath);
        var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
        ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
    }

    private static bool IsFileLockError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException;

    private void FileTree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not TreeViewItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is TreeViewItem item) item.IsSelected = true;
        else e.Handled = true;
    }

    private void FileTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        AcquireFileLockMenu.IsEnabled = !busy && !fileStatusRunning
            && activeProject is { CloneState: ProjectCloneState.Ready }
            && FileTree.SelectedItem is TreeViewItem { Tag: TreeItemInfo { IsDirectory: false } };
    }

    private async void AcquireFileLock_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || FileTree.SelectedItem is not TreeViewItem { Tag: TreeItemInfo { IsDirectory: false } file }) return;
        await ChangeFileLockAsync(project, () => fileLockActionsService.AcquireAsync(project.LocalPath, file.Path), "Lock acquisition");
    }

    private async void ReleaseMyLock_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || myLocksLoading
            || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || MyLocksList.SelectedItem is not MyFileLock entry) return;
        if (!confirm($"Project: {project.Name}\nFile: {entry.RelativePath}\nBranch ID: {entry.Branch}\n\nRelease this lock?",
            "Release lock")) return;
        await ChangeFileLockAsync(project, () => fileLockActionsService.ReleaseAsync(project.LocalPath, entry), "Lock release");
    }

    private async Task ChangeFileLockAsync(ProjectProfile project, Func<Task> operation, string description)
    {
        busy = true;
        UpdateControls();
        StatusText.Text = $"{description} in progress…";
        try
        {
            await ValidateLockRepositoryAsync(project);
            await operation();
            StatusText.Text = $"{description} completed.";
        }
        catch (LoreError ex)
        {
            StatusText.Text = $"{description} failed (Lore error {ex.ReturnCode}). Check sign-in and permissions.\n{ex.Message}";
        }
        catch (Exception ex) when (IsFileLockError(ex))
        {
            StatusText.Text = $"{description} failed: {ex.Message}";
        }
        finally
        {
            // A failed request may still have changed server state; always read it back without retrying the mutation.
            InvalidateMyLocks();
            fileLocks = null;
            var version = ++lockVersion;
            lockNeedsRefresh = false;
            foreach (var root in FileTree.Items.OfType<TreeViewItem>()) UpdateTreeItem(root);
            await LoadLocksAsync(project, version);
            await LoadMyLocksAsync(project);
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
            LoadHistoryIfVisible();
        }
    }

    private async void RefreshMyLocks_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && !fileStatusRunning && !myLocksLoading
            && activeProject is { CloneState: ProjectCloneState.Ready } project)
            await LoadMyLocksAsync(project);
    }

    private void MyLocksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }
}
