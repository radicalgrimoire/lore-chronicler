using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LoreVcs;

namespace Chronicler.Projects;

public partial class WorkspaceWindow
{
    private void ClearBranches()
    {
        BranchList.ItemsSource = null;
        BranchesList.ItemsSource = null;
        BranchesStatusText.Text = activeProject switch
        {
            null => "Open a project to view branches.",
            { CloneState: ProjectCloneState.Pending } => "Branches are available after the initial download.",
            { CloneState: ProjectCloneState.Started } => "Branches are unavailable because the clone did not complete.",
            _ => "Branches have not been loaded."
        };
        CurrentBranchText.Text = "Branch:";
    }

    private void DisplayBranches(BranchListResult result)
    {
        BranchList.ItemsSource = result.Branches;
        BranchesList.ItemsSource = result.Branches;
        var current = result.Branches.Where(branch => branch.IsCurrent).ToArray();
        CurrentBranchText.Text = current.Length == 1
            ? $"Branch: {current[0].Name}"
            : "Branch:";
        BranchList.SelectedItem = current.Length == 1 ? current[0] : null;
        BranchesList.SelectedItem = current.Length == 1 ? current[0] : null;
        BranchesStatusText.Text = result.RemoteAvailable
            ? $"Showing {result.Branches.Count} branches."
            : $"Showing {result.Branches.Count} local branches. Server branches are unavailable.";
    }

    private async Task<string?> RefreshBranchesAsync(ProjectProfile project)
    {
        ClearBranches();
        try
        {
            var result = await ReadBranchesAsync(project);
            if (closed || activeProject?.Id != project.Id) return null;
            DisplayBranches(result);
            return result.RemoteAvailable
                ? BranchesLoadedMessage
                : "Showing local branches. Could not retrieve server branches; check connection, sign-in, and permissions.";
        }
        catch (LoreError ex)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "Branch:";
            if (!closed && activeProject?.Id == project.Id)
                BranchesStatusText.Text = $"Could not retrieve branches (Lore error {ex.ReturnCode}).";
            return $"Could not retrieve branches (Lore error {ex.ReturnCode}).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            if (!closed && activeProject?.Id == project.Id)
                CurrentBranchText.Text = "Branch:";
            if (!closed && activeProject?.Id == project.Id)
                BranchesStatusText.Text = $"Could not retrieve branches: {ex.Message}";
            return $"Could not retrieve branches: {ex.Message}";
        }
    }

    private void BranchesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void NewBranchNameText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void BranchesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(BranchesList, source) is ListBoxItem item)
            item.IsSelected = true;
        else
            BranchesList.SelectedItem = null;
    }

    private void BranchesList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = BranchesList.SelectedItem as BranchEntry;
        SwitchBranchMenu.IsEnabled = SwitchSelectedBranchButton.IsEnabled && selected is not null;
        DeleteBranchMenu.IsEnabled = DeleteBranchButton.IsEnabled && selected is not null;
        BranchMenuSeparator.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        CreateBranchMenu.IsEnabled = activeProject is { CloneState: ProjectCloneState.Ready } && !busy;
    }

    private async void RefreshBranchesTab_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;
        busy = true;
        UpdateControls();
        BranchesStatusText.Text = "Retrieving branches…";
        try
        {
            var message = await RefreshBranchesAsync(project);
            StatusText.Text = message ?? StatusText.Text;
        }
        finally
        {
            busy = false;
            UpdateControls();
            LoadPendingFileStatus();
        }
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e)
    {
        var name = NewBranchNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            BranchesStatusText.Text = "Enter a branch name.";
            NewBranchNameText.Focus();
            return;
        }
        await CreateBranchAsync(name);
    }

    private async void CreateBranchMenu_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready }) return;
        var dialog = new CreateBranchWindow { Owner = this };
        if (dialog.ShowDialog() == true) await CreateBranchAsync(dialog.BranchName);
    }

    private async Task CreateBranchAsync(string name)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project) return;

        busy = true;
        UpdateControls();
        BranchesStatusText.Text = $"Creating branch \"{name}\"…";
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            await branchService.CreateAsync(project.LocalPath, name);
            NewBranchNameText.Clear();
            StatusText.Text = $"Created branch \"{name}\".";
            await RefreshBranchesAsync(project);
        }
        catch (LoreError ex)
        {
            BranchesStatusText.Text = $"Could not create branch (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            BranchesStatusText.Text = $"Could not create branch: {ex.Message}";
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private async void SwitchSelectedBranch_Click(object sender, RoutedEventArgs e)
    {
        if (busy || fileStatusRunning || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || BranchesList.SelectedItem is not BranchEntry { IsCurrent: false } branch) return;
        var source = branch.IsLocal ? "local revision" : "server revision";
        if (!confirm($"Project: {project.Name}\nSwitch to: {branch.Name} ({source})\n\nFiles may be added, changed, or deleted. Switching fails if Lore cannot preserve uncommitted changes.\n\nSwitch branches?", "Switch branch"))
            return;
        await SwitchBranchAsync(project, branch);
    }

    private async void DeleteBranch_Click(object sender, RoutedEventArgs e)
    {
        if (busy || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || BranchesList.SelectedItem is not BranchEntry { IsCurrent: false } branch) return;
        if (!confirm($"Project: {project.Name}\nBranch: {branch.Name}\n\nDelete this branch? The branch will be archived and hidden from the normal list.", "Delete branch"))
            return;

        busy = true;
        UpdateControls();
        BranchesStatusText.Text = $"Deleting branch \"{branch.Name}\"…";
        try
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
            await branchService.DeleteAsync(project.LocalPath, branch);
            StatusText.Text = $"Deleted branch \"{branch.Name}\".";
            await RefreshBranchesAsync(project);
        }
        catch (LoreError ex)
        {
            BranchesStatusText.Text = $"Could not delete branch (Lore error {ex.ReturnCode}).\n{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            BranchesStatusText.Text = $"Could not delete branch: {ex.Message}";
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }
}
