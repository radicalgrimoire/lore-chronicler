using System.IO;
using System.Windows.Controls;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class ServerShortcutsWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private string ServerPath => Path.Combine(directory, "servers.json");
    private string ProjectPath => Path.Combine(directory, "projects.json");

    [Fact]
    public void AddsEditsAndRemovesShortcutsWithoutChangingProjects()
    {
        RunSta(() =>
        {
            var profile = new ProjectProfile(Guid.NewGuid(), "Project", Guid.NewGuid(), "lore://localhost",
                "repo", Path.Combine(directory, "repo"));
            var projects = new ProjectSettingsStore(ProjectPath);
            projects.Load();
            projects.Save([profile]);
            var originalProjectSettings = File.ReadAllBytes(ProjectPath);
            var store = new ServerSettingsStore(ServerPath);
            var window = new ServerShortcutsWindow(store, (_, _) => true);
            Control<TextBox>(window, "NameInput").Text = "Work";
            Control<TextBox>(window, "UrlInput").Text = "lore://localhost";
            Click(window, "SaveButton");
            var saved = Assert.Single(store.Load());
            Control<TextBox>(window, "UrlInput").Text = "lore://other.example";
            Click(window, "SaveButton");
            Assert.Equal(saved.Id, Assert.Single(store.Load()).Id);
            Assert.Equal("lore://other.example", Assert.Single(store.Load()).RemoteUrl);
            Assert.Equal(profile, Assert.Single(projects.Load()));
            Click(window, "RemoveButton");
            Assert.Empty(store.Load());
            Assert.Equal(profile, Assert.Single(projects.Load()));
            Assert.Equal(originalProjectSettings, File.ReadAllBytes(ProjectPath));
            window.Close();
        });
    }

    [Fact]
    public void EnforcesTextElementLimitWithoutRejectingLegacyNames()
    {
        RunSta(() =>
        {
            var old = new ServerProfile(Guid.NewGuid(), new string('x', 33), "lore://localhost");
            var store = new ServerSettingsStore(ServerPath);
            store.Load();
            store.Save([old]);
            var window = new ServerShortcutsWindow(store, (_, _) => true);
            Control<ListBox>(window, "ServerList").SelectedIndex = 0;
            Control<TextBox>(window, "UrlInput").Text = "lore://new.example";
            Click(window, "SaveButton");
            Assert.Equal(old.Name, Assert.Single(store.Load()).Name);

            Click(window, "NewButton");
            Control<TextBox>(window, "NameInput").Text = string.Concat(Enumerable.Repeat("😀", 32));
            Control<TextBox>(window, "UrlInput").Text = "lore://emoji.example";
            Click(window, "SaveButton");
            Assert.Equal(2, store.Load().Count);
            Click(window, "NewButton");
            Control<TextBox>(window, "NameInput").Text = new string('a', 33);
            Control<TextBox>(window, "UrlInput").Text = "lore://too-long.example";
            Click(window, "SaveButton");
            Assert.Contains("at most 32", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal(2, store.Load().Count);
            window.Close();
        });
    }

    [Fact]
    public void DetectsConflictsAndDoesNotDiscardUnconfirmedEdits()
    {
        RunSta(() =>
        {
            var store = new ServerSettingsStore(ServerPath);
            var window = new ServerShortcutsWindow(store, (_, _) => false);
            Control<TextBox>(window, "NameInput").Text = "New";
            Control<TextBox>(window, "UrlInput").Text = "lore://localhost";
            Click(window, "NewButton");
            Assert.Equal("New", Control<TextBox>(window, "NameInput").Text);
            var other = new ServerSettingsStore(ServerPath);
            other.Load();
            other.Save([new ServerProfile(Guid.NewGuid(), "Other", "lore://other.example")]);
            Click(window, "SaveButton");
            Assert.Contains("Could not save connections", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("Other", Assert.Single(other.Load()).Name);
            Control<TextBox>(window, "NameInput").Text = "";
            Control<TextBox>(window, "UrlInput").Text = "";
            window.Close();
        });
    }

    [Fact]
    public void InvalidSettingsAreReportedAndNotOverwritten()
    {
        RunSta(() =>
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(ServerPath, "{");
            var window = new ServerShortcutsWindow(new ServerSettingsStore(ServerPath));
            Assert.False(Control<Button>(window, "SaveButton").IsEnabled);
            Assert.Contains("Could not load connections", Control<TextBlock>(window, "StatusText").Text);
            Assert.Equal("{", File.ReadAllText(ServerPath));
            window.Close();
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
