# Viewing the update log

The update log shows revisions on the current local branch of the open project.

![Update log for a sample project](../images/revision-history.png)

1. If necessary, create a project and complete its initial download as described in [Creating projects](projects.md).
2. On the **Projects** tab, select the project and choose **Switch project**, or double-click it.
3. Select the **Update log** tab. The history loads when you open the tab.
4. Review the revision number, timestamp, message, author, and revision ID. Up to 100 revisions are shown, newest first.
5. Select **Refresh history** to reload history after changes made outside Chronicler.
6. Select a revision and choose **Show changed files**, or right-click the revision and choose the same command. A separate window shows its comment, author, revision ID, and changes relative to its direct parent. Added, deleted, modified, moved, and copied entries are identified where available; folder entries are marked separately. Use **Refresh** in that window to retry loading.
7. Select a changed file and choose **Show file diff** to compare its state in the selected revision with its direct parent. The initial implementation supports UTF-8 text only; folders, binary files, files over 1 MB, and files over 2,000 lines show an explanation instead. The diff is read from Lore's stored revision content and does not change workspace files.

The log is for the current local branch, not necessarily the latest server state. Use **Get latest** to update the project when appropriate; if history data is not available locally, Lore may retrieve it from the server. A project awaiting its initial download has no available history until cloning completes.

For author identities in the history, the application asks Lore's user information service for display names. This may require a server connection and authentication. If a name cannot be resolved, the original identity remains visible and the update log shows a warning. Use **Refresh history** to try again.

The screenshot uses sample project and history data.
