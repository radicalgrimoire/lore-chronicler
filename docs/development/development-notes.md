# Development notes

## Principles

- Use C# and WPF for a Windows-only desktop application.
- Use the official C# SDK, `LoreVcs`, to integrate with Lore. Reference the SDK as a NuGet package.
- Make Lore operations generally conform to Lore CLI behavior.
- Assume the application will be used alongside game engines and similar tools, and be mindful of memory use and responsiveness during operations.
- Since Lore is under development, update it while verifying version compatibility between the SDK and native library.

## Current scope

Record a server URL for each project and support the Lore SDK's interactive login. Do not connect at startup; open a browser only when login is selected on the login screen. When creating a project, show the login screen while retrieving the repository list or verifying an existing clone, allowing continuation with existing credentials. While login is in progress, prevent setting changes and duplicate logins, and show errors in the UI when they occur.

At startup, the workspace shows the Projects, Changes, and Update Log tabs on the right, and the local file tree of the selected project on the left. The Projects tab manages the list of projects across all servers and project creation and switching. The Update Log displays up to 100 revisions from the current local branch of the open project. If data is missing when history is retrieved, retrieve it from the server; reload history after switching a project or branch. The Changes tab displays changed items and their parent folders in one tree using their actual hierarchy, and switches among all changes, unstaged changes, and staged changes with a display filter. Deleted files and folders used to operate on their descendant changes collectively can be selected. Staging a folder scans recursively, and unstaging includes its descendants. Confirm before an operation that items outside the display may be included. Perform operations offline and retrieve status again after each attempt regardless of success. Omit unresolved conflicts from the list and indicate that they exist. Load the left-side tree when a folder is expanded, exclude `.lore`, do not expand link targets, and do not highlight change status. Retrieve change status with an offline status scan when opening a project and refreshing. The scan updates Lore's local management data. Clearly show loading and failure states, and discard stale results. Get Latest checks remote history and cancels when histories have diverged or local history is ahead. Sync against the explicit remote revision that was checked, avoiding automatic merges caused by implicit sync. Do not specify forced reset. The server tree, change status, and checkout are not supported.

Retrieve branch lists locally and from the server when switching projects, after cloning completes, and when the user requests retrieval; display the current branch. The Branches tab creates branches and archives non-current branches, with explicit confirmation for deletion. Switch a local branch to its local revision, and switch to a server-only branch from the server. Leave change handling to Lore's normal behavior and do not use forced reset. Clearly indicate when the server list cannot be retrieved or switching fails.

If the SDK explicitly indicates an unauthenticated state during prerequisite checks for Get Latest or push, start browser authentication and retry the prerequisite check once. Do not authenticate automatically for operations that cannot distinguish connection and permission errors, and do not automatically retry after data-modifying work begins.

When a project is opened and refreshed, query Lore locks on every branch on the server for lock indicators in the tree. Show a lock icon only for files, and list the acquirer and branch ID in the hover text. Show an ID and warning if a name cannot be resolved. The Changes tab also switches to a separate view of the current user's locks across branches; returning to Changes retains its input state. Clearly show loading and failure states, and discard stale results. Windows file-in-use state is out of scope.

Display a staged-item count below the Changes tab list that does not depend on the filter. After entering a message and confirming the targets, allow all staged changes to be committed offline. Cancel if the status scan after the confirmation dialog detects a mismatch with displayed targets or unresolved conflicts. Since Lore records the contents of files re-edited after staging at commit time, state this clearly in the UI. Invalidate status and history after execution, and prompt for verification before retrying if the result cannot be confirmed. Keep message drafts per project only while the application is running.

Before push, check the endpoint and head of the current branch, request user confirmation, and check the branch and head again immediately before execution. Cancel if the server is ahead or histories have diverged, and do not specify force or automatic merge. Branch creation and restoration, along with sending link targets and layers, follow Lore's normal behavior. SDK calls and state checks are not atomic and cannot prevent concurrent changes from other processes. Check the server revision after execution and indicate the possibility of a partial send on failure.

The project creation screen handles registering an existing clone and preparing a new clone. Enter the server URL for a new clone directly or select it from endpoints of registered projects. Do not save suggestions independently; they disappear when no project for the same server remains. A new project registers a server, repository, and folder, then performs its initial clone with the common “Get Latest” operation. Once cloned, the same button updates it. A project is a Chronicler-specific management unit that associates a server, repository, and clone destination on a PC. Use an empty clone destination. Do not retry automatically after failure or cancellation, and do not delete the folder automatically. Unregistering does not delete the clone destination.

Do not store credentials in settings files. The Lore SDK manages credentials. If a settings file is corrupt, do not overwrite it; also detect modifications by another process and refuse to save. Deleting settings is not logout. Logout and cancellation of the login process are not currently supported.

Login success with SDK 0.12.0 has been confirmed in a user's actual environment. Retrieving repository lists, cloning, Get Latest, and login again after expiration have not been verified there. Successful login does not guarantee repository access.

## References for implementing Lore operations (for AI and developers)

When adding a GUI operation corresponding to a Lore CLI command, do not infer SDK APIs or behavior from the command name. Check the following:

1. In the [Lore CLI command implementations](https://github.com/EpicGames/lore/tree/main/lore-client/src/cli/commands), check the target command's arguments, the Lore APIs it calls, and its output.
2. In [Lore's C API header](https://github.com/EpicGames/lore/blob/main/lore-capi/lore.h) and [the core implementation](https://github.com/EpicGames/lore/tree/main/lore/src), check API arguments, events, and behavior on success and failure.
3. In the [C# SDK README](https://github.com/EpicGames/lore-dotnet#readme) and [samples](https://github.com/EpicGames/lore-dotnet/tree/main/examples), check how to invoke it from C#. For an existing Chronicler example, see the [login implementation](../../src/Chronicler/Authentication/LoreLoginService.cs).
4. Confirm the behavior in the source of the [LoreVcs version used by this project](../../src/Chronicler/Chronicler.csproj).

Read required data from SDK events inside callbacks, and dispatch UI updates to the UI thread. Do not block the UI during long-running operations, and show failures to the user. Do not store sensitive information such as tokens or login URLs in logs or settings files. Verify write and destructive operations in implementation and tests using a trial repository.

## Future candidates

1. Verify repository list retrieval, cloning, and re-login after expiration in a trial repository in a real environment.
2. Independently test retrieving local repository status through the SDK.
3. Display folder selection and the list of changed files. Scanning updates local management data.
4. Assume a large number of files and verify list virtualization and memory use.
5. Verify push behavior in a trial repository.

Stage/Unstage implementation was tracked in [issue #5](https://github.com/radicalgrimoire/chronicler/issues/5). Testing with large numbers of files is tracked by the [scan and operation time](https://github.com/radicalgrimoire/chronicler/issues/8) and [memory usage](https://github.com/radicalgrimoire/chronicler/issues/7) issues.

## Memory measurement

Compare immediately after startup, while idle, during status retrieval for a large number of files, and after retrieval, using the same environment and repository. Consider the effects of both the GUI and Lore native library, and set target values based on measurements.
