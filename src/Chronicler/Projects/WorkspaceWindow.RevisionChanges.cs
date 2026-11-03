using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Chronicler.Projects;

public partial class WorkspaceWindow
{
    private void RevisionHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void RevisionHistoryList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(RevisionHistoryList, source) is ListBoxItem item)
            item.IsSelected = true;
        else
            e.Handled = true;
    }

    private void RevisionHistoryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ShowRevisionChangesMenu.IsEnabled = ShowRevisionChangesButton.IsEnabled;
    }

    private void ShowRevisionChanges_Click(object sender, RoutedEventArgs e)
    {
        if (busy || historyLoading || activeProject is not { CloneState: ProjectCloneState.Ready } project
            || RevisionHistoryList.SelectedItem is not RevisionHistoryEntry revision) return;

        async Task VerifyRepositoryAsync()
        {
            ReadLoreAttributes(project.LocalPath);
            var local = await repositoryInfo.ReadLocalAsync(project.LocalPath);
            ProjectRepositoryInfo.Validate(local, project.ServerUrl, project.RepositoryId);
        }

        var window = new RevisionChangesWindow(project.Name, revision, async () =>
        {
            await VerifyRepositoryAsync();
            return await revisionChangesService.ReadAsync(project.LocalPath, revision.Revision);
        }, async change =>
        {
            await VerifyRepositoryAsync();
            return await fileDiffService.ReadAsync(project.LocalPath, revision.Revision, change);
        }) { Owner = this };
        window.Show();
    }
}
