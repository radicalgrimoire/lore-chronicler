using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(directory, "projects.json");
    private readonly ServerProfile server = new(Guid.NewGuid(), "Main", "lore://localhost:41337");

    [Theory]
    [InlineData("repo", true)]
    [InlineData("team/project-1.test", true)]
    [InlineData("../other", false)]
    [InlineData("team//repo", false)]
    [InlineData("team/.hidden", false)]
    [InlineData("repo?x=1", false)]
    [InlineData("", false)]
    public void RepositoryNamesRoundTripSafely(string name, bool expected) =>
        Assert.Equal(expected, ProjectValidator.IsValidRepositoryName(name));

    [Fact]
    public void BuildsUrlAndRequiresAbsoluteNonRootPath()
    {
        Assert.Equal("lore://localhost:41337/team/repo", ProjectValidator.RepositoryUrl(server.RemoteUrl, "team/repo"));
        Assert.Throws<ArgumentException>(() => ProjectValidator.RepositoryUrl(server.RemoteUrl, "../repo"));
        Assert.False(ProjectValidator.TryNormalizePath("relative", out _, out _));
        Assert.False(ProjectValidator.TryNormalizePath(@"C:\", out _, out _));
        Assert.True(ProjectValidator.TryNormalizePath(Path.Combine(directory, "repo"), out var path, out _));
        Assert.Equal(Path.Combine(directory, "repo"), path);
    }

    [Fact]
    public void ProjectSettingsPersistAndDetectConcurrentChanges()
    {
        var first = new ProjectSettingsStore(SettingsPath);
        var other = new ProjectSettingsStore(SettingsPath);
        Assert.Empty(first.Load());
        Assert.Empty(other.Load());
        var project = new ProjectProfile(Guid.NewGuid(), "Project", server.Id, server.RemoteUrl, "team/repo", Path.Combine(directory, "repo"));
        first.Save([project]);
        Assert.Throws<InvalidOperationException>(() => other.Save([]));
        Assert.Equal([project], other.Load());
        Assert.Throws<InvalidDataException>(() => other.Save([project, project with { Id = Guid.NewGuid() }]));
        Assert.Equal([project], first.Load());
    }

    [Fact]
    public void OldProjectWithoutCloneStateIsTreatedAsAlreadyCloned()
    {
        Directory.CreateDirectory(directory);
        var old = new
        {
            Version = 1,
            Projects = new[] { new { Id = Guid.NewGuid(), Name = "old", ServerId = server.Id,
                ServerUrl = server.RemoteUrl, RepositoryName = "repo", LocalPath = Path.Combine(directory, "old") } }
        };
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(old));
        var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
        Assert.Equal(ProjectCloneState.Ready, project.CloneState);
        Assert.Null(project.RepositoryId);
    }

    [Fact]
    public async Task SdkRejectsInvalidRemoteForRepositoryListing() =>
        await Assert.ThrowsAsync<LoreVcs.LoreError>(() => new LoreRepositoryService().ListAsync("not a valid remote"));

    [Fact]
    public void ReservesCloneDirectoryWithoutOverwritingExistingContent()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "repo");
        CloneDestination.Reserve(path);
        var marker = Path.Combine(path, "existing.txt");
        File.WriteAllText(marker, "keep");
        Assert.Throws<Win32Exception>(() => CloneDestination.Reserve(path));
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public void ProjectWindowOffersOnlyProjectFolderSelection()
    {
        RunSta(() =>
        {
            var window = CreateWindow(new FakeRepositoryService());
            Assert.NotNull(window.FindName("BrowseButton"));
            Assert.Null(window.FindName("BrowseParentButton"));
            window.Close();
        });
    }

    [Fact]
    public void ProjectFieldsAreOrderedByNameFolderAndRepository()
    {
        RunSta(() =>
        {
            var window = CreateWindow(new FakeRepositoryService());
            Assert.True(Grid.GetRow(Control<StackPanel>(window, "ProjectNameSection"))
                < Grid.GetRow(Control<StackPanel>(window, "ProjectFolderSection")));
            Assert.True(Grid.GetRow(Control<StackPanel>(window, "ProjectFolderSection"))
                < Grid.GetRow(Control<StackPanel>(window, "ServerSection")));
            Assert.True(Grid.GetRow(Control<StackPanel>(window, "ServerSection"))
                < Grid.GetRow(Control<ListBox>(window, "RepositoryList")));
            window.Close();
        });
    }

    [Fact]
    public void RepositorySelectionPreservesAnEnteredProjectName()
    {
        RunSta(() =>
        {
            var window = CreateWindow(new FakeRepositoryService());
            Control<TextBox>(window, "ProjectName").Text = "Custom name";
            SelectRepository(window);
            Assert.Equal("Custom name", Control<TextBox>(window, "ProjectName").Text);
            window.Close();
        });
    }

    [Fact]
    public void RepositorySelectionPreservesAnExplicitNameMatchingPreviousRepository()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var service = new FakeRepositoryService { Names = ["team/repo", "team/second"] };
            var window = CreateWindow(service);
            SelectRepository(window);
            var name = Control<TextBox>(window, "ProjectName");
            name.Text = "team/repo-custom";
            name.Text = "team/repo";
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 1;
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "clone");
            Click(window, "CreateButton");
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal("team/repo", project.Name);
            Assert.Equal("team/second", project.RepositoryName);
            window.Close();
        });
    }

    [Fact]
    public void RepositorySelectionUpdatesSuggestedNameUntilManuallyEdited()
    {
        RunSta(() =>
        {
            var service = new FakeRepositoryService { Names = ["team/repo", "team/second"] };
            var window = CreateWindow(service);
            SelectRepository(window);
            var name = Control<TextBox>(window, "ProjectName");
            var repositories = Control<ListBox>(window, "RepositoryList");
            Assert.Equal("team/repo", name.Text);
            repositories.SelectedIndex = 1;
            Assert.Equal("team/second", name.Text);
            name.Text = "Custom name";
            repositories.SelectedIndex = 0;
            Assert.Equal("Custom name", name.Text);
            name.Text = "";
            repositories.SelectedIndex = 1;
            Assert.Equal("team/second", name.Text);
            window.Close();
        });
    }

    [Fact]
    public void NewCloneSavesEnteredProjectNameInsteadOfRepositoryName()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var window = CreateWindow(new FakeRepositoryService());
            Control<TextBox>(window, "ProjectName").Text = "Custom name";
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "clone");
            SelectRepository(window);
            Click(window, "CreateButton");
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal("Custom name", project.Name);
            Assert.Equal("team/repo", project.RepositoryName);
            window.Close();
        });
    }

    [Fact]
    public void ExistingCloneSavesEnteredProjectNameInsteadOfRepositoryName()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var info = new FakeProjectRepositoryInfo { Name = "team/repo" };
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(),
                (_, _) => true, info, _ => true);
            Control<TextBox>(window, "ProjectName").Text = "Custom name";
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal("Custom name", project.Name);
            Assert.Equal("team/repo", project.RepositoryName);
            window.Close();
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreatesPendingProjectWithoutCloning(bool existingEmpty)
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "clone");
            if (existingEmpty) Directory.CreateDirectory(path);
            var service = new FakeRepositoryService();
            var window = CreateWindow(service);
            SelectRepository(window);
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal(ProjectCloneState.Pending, project.CloneState);
            Assert.Null(project.RepositoryId);
            Assert.Equal(path, project.LocalPath);
            Assert.Equal(existingEmpty, Directory.Exists(path));
            Assert.Null(service.Path);
            window.Close();
        });
    }

    [Fact]
    public void ChangingServerClearsRepositorySelection()
    {
        RunSta(() =>
        {
            var service = new FakeRepositoryService();
            var window = CreateWindow(service);
            window.Show();
            SelectRepository(window);
            Control<ComboBox>(window, "ServerUrlInput").Text = "lore://other.example:41337";
            Assert.Null(Control<ListBox>(window, "RepositoryList").SelectedItem);
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "clone");
            Click(window, "CreateButton");
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
            window.Close();
        });
    }

    [Fact]
    public void SavedServersAreDistinctAndCanBeSelectedAgainAfterEditing()
    {
        RunSta(() =>
        {
            var store = new ProjectSettingsStore(SettingsPath);
            store.Load();
            store.Save([
                new ProjectProfile(Guid.NewGuid(), "One", server.Id, server.RemoteUrl, "repo", Path.Combine(directory, "one")),
                new ProjectProfile(Guid.NewGuid(), "Two", Guid.NewGuid(), server.RemoteUrl + "/", "repo", Path.Combine(directory, "two")),
                new ProjectProfile(Guid.NewGuid(), "Three", Guid.NewGuid(), "lore://other.example:41337", "repo", Path.Combine(directory, "three"))
            ]);
            var service = new FakeRepositoryService();
            string? authUrl = null;
            var window = new ProjectWindow(server, store, service,
                (_, _) => true, new FakeProjectRepositoryInfo(), url => { authUrl = url; return true; });
            window.Show();
            var savedServers = Control<ComboBox>(window, "ServerUrlInput");
            Assert.Equal(2, savedServers.Items.Count);
            Assert.True(savedServers.IsEnabled);
            SelectRepository(window);
            savedServers.SelectedItem = "lore://other.example:41337";
            Assert.Equal("lore://other.example:41337", savedServers.Text);
            Assert.Empty(Control<ListBox>(window, "RepositoryList").Items);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal("lore://other.example:41337", authUrl);
            Assert.Equal(authUrl, service.ListedServerUrl);
            savedServers.Text = "lore://new.example:41337";
            Assert.Null(savedServers.SelectedItem);
            savedServers.SelectedItem = "lore://other.example:41337";
            Assert.Equal("lore://other.example:41337", savedServers.Text);
            window.Close();
        });
    }

    [Fact]
    public void ShortcutSelectionUsesUrlAndKeepsManualAndProjectCandidates()
    {
        RunSta(() =>
        {
            var store = new ProjectSettingsStore(SettingsPath);
            store.Load();
            store.Save([
                new ProjectProfile(Guid.NewGuid(), "One", server.Id, server.RemoteUrl, "repo", Path.Combine(directory, "one")),
                new ProjectProfile(Guid.NewGuid(), "Two", Guid.NewGuid(), "lore://other.example", "repo", Path.Combine(directory, "two"))
            ]);
            var shortcuts = new ServerSettingsStore(Path.Combine(directory, "servers.json"));
            shortcuts.Load();
            shortcuts.Save([new ServerProfile(Guid.NewGuid(), "Work", server.RemoteUrl)]);
            var service = new FakeRepositoryService();
            string? authUrl = null;
            var window = new ProjectWindow(server, store, service, (_, _) => true,
                new FakeProjectRepositoryInfo(), url => { authUrl = url; return true; }, shortcuts);
            window.Show();
            var input = Control<ComboBox>(window, "ServerUrlInput");
            Assert.Equal(2, input.Items.Count);
            Assert.Equal($"Work({server.RemoteUrl})", input.Items[0].ToString());
            input.SelectedIndex = 0;
            Assert.Equal($"Work({server.RemoteUrl})", input.Text);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal(server.RemoteUrl, authUrl);
            Assert.Equal(server.RemoteUrl, service.ListedServerUrl);
            input.Text = "lore://manual.example";
            Assert.Null(input.SelectedItem);
            Assert.Empty(Control<ListBox>(window, "RepositoryList").Items);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal("lore://manual.example", service.ListedServerUrl);
            input.SelectedIndex = 0;
            Assert.Equal($"Work({server.RemoteUrl})", input.Text);
            Assert.Empty(Control<ListBox>(window, "RepositoryList").Items);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal(server.RemoteUrl, service.ListedServerUrl);
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "clone");
            Click(window, "CreateButton");
            Assert.Equal(server.RemoteUrl, window.CreatedProject!.ServerUrl);
            window.Close();
        });
    }

    [Fact]
    public void InvalidShortcutsDoNotBlockManualServerEntry()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "servers.json"), "{");
            var service = new FakeRepositoryService();
            var window = CreateWindow(service);
            Assert.Contains("Could not load connection shortcuts", Control<TextBlock>(window, "StatusText").Text);
            Control<ComboBox>(window, "ServerUrlInput").Text = "lore://manual.example";
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal("lore://manual.example", service.ListedServerUrl);
            Assert.Equal("{", File.ReadAllText(Path.Combine(directory, "servers.json")));
            window.Close();
        });
    }

    [Fact]
    public void ServerUrlCanBeEnteredWithoutSavedProjects()
    {
        RunSta(() =>
        {
            var window = CreateWindow(new FakeRepositoryService());
            var savedServers = Control<ComboBox>(window, "ServerUrlInput");
            Assert.Empty(savedServers.Items);
            Assert.True(savedServers.IsEnabled);
            Assert.True(savedServers.IsEditable);
            savedServers.Text = "lore://new.example:41337";
            Assert.Equal("lore://new.example:41337", savedServers.Text);
            window.Close();
        });
    }

    [Fact]
    public void FetchingRepositoriesAuthenticatesForTheEnteredServer()
    {
        RunSta(() =>
        {
            string? authUrl = null;
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service,
                (_, _) => true, new FakeProjectRepositoryInfo(), url => { authUrl = url; return true; });
            Control<ComboBox>(window, "ServerUrlInput").Text = "lore://other.example:41337";
            Assert.Null(window.FindName("LoginButton"));
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal("lore://other.example:41337", authUrl);
            Assert.Equal(authUrl, service.ListedServerUrl);
            Assert.Single(Control<ListBox>(window, "RepositoryList").Items);
            window.Close();
        });
    }

    [Fact]
    public void CancellingAuthenticationDoesNotFetchRepositories()
    {
        RunSta(() =>
        {
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service,
                (_, _) => true, new FakeProjectRepositoryInfo(), _ => false);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Null(service.ListedServerUrl);
            Assert.Empty(Control<ListBox>(window, "RepositoryList").Items);
            Assert.Contains("Repository retrieval cancelled", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(Control<Button>(window, "FetchButton").IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void ExistingCloneFolderPopulatesServerWithoutNetworkAccess()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var info = new FakeProjectRepositoryInfo { RemoteUrl = "lore://other.example:41337" };
            var service = new FakeRepositoryService();
            var authCalls = 0;
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service,
                (_, _) => true, info, _ => { authCalls++; return true; });
            window.Show();
            var folder = Control<TextBox>(window, "LocalPath");
            folder.Text = path;
            folder.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            PumpDispatcher();
            Assert.Equal(info.RemoteUrl, Control<ComboBox>(window, "ServerUrlInput").Text);
            Assert.Null(service.ListedServerUrl);
            Assert.Equal(0, info.RemoteCalls);
            Assert.Equal(0, authCalls);
            window.Close();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedFolderServerReadDoesNotOverwriteNewInput(bool changeFolder)
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var completion = new TaskCompletionSource<string>();
            var info = new FakeProjectRepositoryInfo { ReadServerUrl = _ => completion.Task };
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(),
                (_, _) => true, info, _ => true);
            window.Show();
            var folder = Control<TextBox>(window, "LocalPath");
            var url = Control<ComboBox>(window, "ServerUrlInput");
            folder.Text = path;
            folder.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            if (changeFolder) folder.Text = Path.Combine(directory, "other");
            else url.Text = "lore://manual.example:41337";
            completion.SetResult("lore://old.example:41337");
            PumpDispatcher();
            Assert.Equal(changeFolder ? server.RemoteUrl : "lore://manual.example:41337", url.Text);
            window.Close();
        });
    }

    [Fact]
    public void FolderServerReadReportsErrorsWithoutReplacingUrl()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var info = new FakeProjectRepositoryInfo { ReadServerUrl = _ => Task.FromException<string>(new IOException("config error")) };
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(),
                (_, _) => true, info, _ => true);
            var folder = Control<TextBox>(window, "LocalPath");
            folder.Text = path;
            folder.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            PumpDispatcher();
            Assert.Equal(server.RemoteUrl, Control<ComboBox>(window, "ServerUrlInput").Text);
            Assert.Contains("config error", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void FolderServerReadMustFinishBeforeFetchingRepositories()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var completion = new TaskCompletionSource<string>();
            var info = new FakeProjectRepositoryInfo { ReadServerUrl = _ => completion.Task };
            var service = new FakeRepositoryService();
            var authCalls = 0;
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service,
                (_, _) => true, info, _ => { authCalls++; return true; });
            window.Show();
            var folder = Control<TextBox>(window, "LocalPath");
            folder.Text = path;
            folder.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Click(window, "FetchButton");
            Assert.Equal(0, authCalls);
            Assert.Null(service.ListedServerUrl);
            Assert.Contains("Reading the folder's server URL", Control<TextBlock>(window, "StatusText").Text);
            completion.SetResult("lore://other.example:41337");
            PumpDispatcher();
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal(1, authCalls);
            Assert.Equal("lore://other.example:41337", service.ListedServerUrl);
            window.Close();
        });
    }

    [Fact]
    public void NewCloneFolderDoesNotReplaceServerUrl()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var info = new FakeProjectRepositoryInfo
            {
                ReadServerUrl = _ => throw new InvalidOperationException("Unexpected configuration read")
            };
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(),
                (_, _) => true, info, _ => true);
            var folder = Control<TextBox>(window, "LocalPath");
            folder.Text = directory;
            folder.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            PumpDispatcher();
            Assert.Equal(server.RemoteUrl, Control<ComboBox>(window, "ServerUrlInput").Text);
            Assert.DoesNotContain("Unexpected", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Theory]
    [InlineData("file")]
    [InlineData("hidden")]
    [InlineData("directory")]
    public void NonEmptyDestinationIsNeverRegisteredOrChanged(string type)
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(path);
            var marker = Path.Combine(path, "keep");
            if (type == "directory") Directory.CreateDirectory(marker);
            else
            {
                File.WriteAllText(marker, "keep");
                if (type == "hidden") File.SetAttributes(marker, FileAttributes.Hidden);
            }
            var service = new FakeRepositoryService();
            var window = CreateWindow(service);
            SelectRepository(window);
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Contains("clone destination", Control<TextBlock>(window, "StatusText").Text);
            Assert.Null(service.Path);
            if (type != "directory")
            {
                Assert.Equal("keep", File.ReadAllText(marker));
                File.SetAttributes(marker, FileAttributes.Normal);
            }
            window.Close();
        });
    }

    [Fact]
    public void ExistingCloneIsRegisteredWithoutCloning()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            File.WriteAllText(Path.Combine(path, "keep"), "keep");
            var info = new FakeProjectRepositoryInfo { Name = "team/repo", RemoteUrl = "lore://other.example:41337" };
            string? authUrl = null;
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service,
                (_, _) => true, info, url => { authUrl = url; return true; });
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal(ProjectCloneState.Ready, project.CloneState);
            Assert.Equal(info.RepositoryId, project.RepositoryId);
            Assert.Equal(info.RemoteUrl, project.ServerUrl);
            Assert.Equal(info.RemoteUrl, authUrl);
            Assert.Equal("team/repo", project.Name);
            Assert.Null(service.Path);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "keep")));
            window.Close();
        });
    }

    [Fact]
    public void InvalidExistingCloneDoesNotRegister()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var info = new FakeProjectRepositoryInfo { ServerRepositoryId = "ABCDEF1234567890ABCDEF1234567890" };
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(), (_, _) => true, info, _ => true);
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Contains("server repository", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void CancellingExistingCloneAuthenticationDoesNotRegister()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            var info = new FakeProjectRepositoryInfo();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), new FakeRepositoryService(), (_, _) => true, info, _ => false);
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CreateButton");
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal(0, info.RemoteCalls);
            Assert.Contains("Project creation cancelled", Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    private ProjectWindow CreateWindow(FakeRepositoryService service) =>
        new(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => true, new FakeProjectRepositoryInfo(), _ => true);

    private static void SelectRepository(ProjectWindow window)
    {
        Click(window, "FetchButton");
        PumpDispatcher();
        Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeRepositoryService : IRepositoryService
    {
        public string? Path { get; private set; }
        public string? ListedServerUrl { get; private set; }
        public IReadOnlyList<string> Names { get; init; } = ["team/repo"];
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl)
        {
            ListedServerUrl = serverUrl;
            return Task.FromResult(Names);
        }
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
        {
            Path = path;
            return Task.CompletedTask;
        }
    }
}
