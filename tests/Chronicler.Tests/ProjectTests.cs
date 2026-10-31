using System.ComponentModel;
using System.IO;
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
            SelectRepository(window);
            Control<TextBox>(window, "ServerUrlInput").Text = "lore://other.example:41337";
            Assert.Null(Control<ListBox>(window, "RepositoryList").SelectedItem);
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "clone");
            Click(window, "CreateButton");
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
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
            Assert.Contains("空ではありません", Control<TextBlock>(window, "StatusText").Text);
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
            Assert.Contains("一致しません", Control<TextBlock>(window, "StatusText").Text);
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
            Assert.Contains("中止", Control<TextBlock>(window, "StatusText").Text);
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
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl) => Task.FromResult<IReadOnlyList<string>>(["team/repo"]);
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
        {
            Path = path;
            return Task.CompletedTask;
        }
    }
}
