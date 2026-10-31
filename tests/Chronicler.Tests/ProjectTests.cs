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
    public void RepositoryNamesRoundTripSafely(string name, bool expected)
    {
        Assert.Equal(expected, ProjectValidator.IsValidRepositoryName(name));
    }

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
    public async Task SdkRejectsInvalidRemoteForRepositoryListing()
    {
        await Assert.ThrowsAsync<LoreVcs.LoreError>(() => new LoreRepositoryService().ListAsync("not a valid remote"));
    }

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
    public void FetchCloneAndRegisterProject()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var service = new FakeRepositoryService();
            var store = new ProjectSettingsStore(SettingsPath);
            var window = new ProjectWindow(server, store, service, (_, _) => true);
            Assert.Equal(0, service.ListCalls);
            Click(window, "FetchButton");
            PumpDispatcher();
            Assert.Equal(1, service.ListCalls);
            var list = Control<ListBox>(window, "RepositoryList");
            list.SelectedIndex = 0;
            var path = Path.Combine(directory, "clone");
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Equal(path, service.Path);
            Assert.True(Directory.Exists(path));
            Assert.False(Control<Button>(window, "FetchButton").IsEnabled);
            Directory.CreateDirectory(Path.Combine(path, ".lore"));
            service.CloneResult.SetResult();
            PumpDispatcher();
            var project = Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.Equal("team/repo", project.RepositoryName);
            Assert.Equal(server.Id, project.ServerId);
            Assert.Equal(path, project.LocalPath);
            Assert.Equal(project, window.CreatedProject);
            Assert.True(Directory.Exists(Path.Combine(path, ".lore")));
            window.Close();
        });
    }

    [Fact]
    public void CloneFailureKeepsDestinationAndDoesNotRegisterProject()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var service = new FakeRepositoryService();
            var store = new ProjectSettingsStore(SettingsPath);
            var window = new ProjectWindow(server, store, service, (_, _) => true);
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            var path = Path.Combine(directory, "failed");
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            File.WriteAllText(Path.Combine(path, "partial.txt"), "keep");
            service.CloneResult.SetException(new LoreVcs.LoreError(42));
            PumpDispatcher();
            Assert.Contains("42", Control<TextBlock>(window, "StatusText").Text);
            Assert.True(File.Exists(Path.Combine(path, "partial.txt")));
            Assert.Empty(new ProjectSettingsStore(SettingsPath).Load());
            window.Close();
        });
    }

    [Fact]
    public void ExistingEmptyDestinationIsClonedAndRegistered()
    {
        RunSta(() =>
        {
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(path);
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => true);
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Equal(path, service.Path);
            service.CloneResult.SetResult();
            PumpDispatcher();
            Assert.Equal(path, Assert.Single(new ProjectSettingsStore(SettingsPath).Load()).LocalPath);
            window.Close();
        });
    }

    [Theory]
    [InlineData("file")]
    [InlineData("hidden")]
    [InlineData("directory")]
    public void NonEmptyDestinationIsNeverClonedOrChanged(string entryType)
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "existing");
            Directory.CreateDirectory(path);
            var marker = Path.Combine(path, "keep");
            if (entryType == "directory")
            {
                Directory.CreateDirectory(marker);
            }
            else
            {
                File.WriteAllText(marker, "keep");
                if (entryType == "hidden")
                {
                    File.SetAttributes(marker, FileAttributes.Hidden);
                }
            }
            var service = new FakeRepositoryService();
            var confirmed = false;
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) =>
            {
                confirmed = true;
                return true;
            });
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Null(service.Path);
            Assert.False(confirmed);
            Assert.Contains("空ではありません", Control<TextBlock>(window, "StatusText").Text);
            if (entryType == "directory")
            {
                Assert.True(Directory.Exists(marker));
            }
            else
            {
                Assert.Equal("keep", File.ReadAllText(marker));
                File.SetAttributes(marker, FileAttributes.Normal);
            }
            window.Close();
        });
    }

    [Fact]
    public void ExistingFileCannotBeUsedAsCloneDestination()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "file");
            File.WriteAllText(path, "keep");
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => true);
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Null(service.Path);
            Assert.Contains("フォルダーではありません", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("keep", File.ReadAllText(path));
            window.Close();
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DestinationChangedDuringConfirmationIsNotCloned(bool existed)
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "changed");
            if (existed)
            {
                Directory.CreateDirectory(path);
            }
            var marker = Path.Combine(path, "keep.txt");
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) =>
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(marker, "keep");
                return true;
            });
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Null(service.Path);
            Assert.Equal("keep", File.ReadAllText(marker));
            Assert.NotEmpty(Control<TextBlock>(window, "StatusText").Text);
            window.Close();
        });
    }

    [Fact]
    public void DecliningConfirmationDoesNotCreateDestination()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "new");
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => false);
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            Assert.Null(service.Path);
            Assert.False(Directory.Exists(path));
            window.Close();
        });
    }

    [Fact]
    public void SuccessfulCloneCanRetryFailedRegistration()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => true);
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            var path = Path.Combine(directory, "retry");
            Control<TextBox>(window, "LocalPath").Text = path;
            Click(window, "CloneButton");
            using (var locked = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                service.CloneResult.SetResult();
                PumpDispatcher();
                Assert.Contains("cloneは完了", Control<TextBlock>(window, "StatusText").Text);
                Assert.Equal(Visibility.Visible, Control<Button>(window, "RetrySaveButton").Visibility);
            }
            Click(window, "RetrySaveButton");
            Assert.Single(new ProjectSettingsStore(SettingsPath).Load());
            Assert.True(Directory.Exists(path));
            window.Close();
        });
    }

    [Fact]
    public void SuccessfulCloneClosesVisibleCreationWindow()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            var service = new FakeRepositoryService();
            var window = new ProjectWindow(server, new ProjectSettingsStore(SettingsPath), service, (_, _) => true);
            window.Show();
            Click(window, "FetchButton");
            PumpDispatcher();
            Control<ListBox>(window, "RepositoryList").SelectedIndex = 0;
            Control<TextBox>(window, "LocalPath").Text = Path.Combine(directory, "visible");
            Click(window, "CloneButton");
            service.CloneResult.SetResult();
            PumpDispatcher();
            Assert.NotNull(window.CreatedProject);
            Assert.False(window.IsVisible);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRepositoryService : IRepositoryService
    {
        public int ListCalls { get; private set; }
        public string? Path { get; private set; }
        public TaskCompletionSource CloneResult { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(string serverUrl)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<string>>(["team/repo"]);
        }

        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
        {
            Path = path;
            return CloneResult.Task;
        }
    }
}
