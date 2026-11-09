using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chronicler.Authentication;
using Chronicler.Projects;
using Chronicler.Servers;
using static Chronicler.Tests.MainWindowTests;

namespace Chronicler.Tests;

public sealed class AuthenticationStatusTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private const string FirstId = "aabb0000000000000000000000000000";
    private const string SecondId = "ccdd0000000000000000000000000000";

    [Fact]
    public void UsesLaterExpiryOnlyFromMatchingRepositoryAndAuthenticationServer()
    {
        StoredAuthIdentity[] identities =
        [
            new("https://auth.example", "aabb", Now.AddMinutes(-1)),
            new("https://auth.example", "", Now.AddHours(2)),
            new("https://auth.example", "", Now.AddHours(1)),
            new("https://other.example", "", Now.AddDays(10)),
            new("https://auth.example", "other-repo", Now.AddDays(20))
        ];
        var status = AuthenticationStatus.FromIdentities("AABB", identities);
        Assert.True(status.HasIdentity);
        Assert.Equal(Now.AddHours(2), status.ExpiresAt);
        Assert.Equal("Authenticated: 2026-11-02 21:00", status.GetText(Now));

        status = AuthenticationStatus.FromIdentities("aabb",
            [new("https://auth.example", "aabb", Now.AddHours(3)), new("https://auth.example", "", Now)]);
        Assert.Equal(Now.AddHours(3), status.ExpiresAt);
    }

    [Fact]
    public void DistinguishesMissingIdentityUnknownExpiryAndExpiredIncludingExactBoundary()
    {
        var missing = AuthenticationStatus.FromIdentities("aabb", [new("https://auth.example", "", Now.AddDays(1))]);
        Assert.False(missing.HasIdentity);
        Assert.Equal("Not signed in", missing.GetText(Now));
        Assert.Equal("Credentials available (expiration unknown)",
            AuthenticationStatus.FromIdentities("aabb", [new("https://auth.example", "aabb", null)]).GetText(Now));
        var status = new AuthenticationStatus(true, Now);
        Assert.Equal("Authenticated: 2026-11-02 19:00", status.GetText(Now.AddTicks(-1)));
        Assert.Equal("Expired", status.GetText(Now));
        Assert.Equal("Expired", status.GetText(Now.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => AuthenticationStatus.FromIdentities("", []));
    }

    [Fact]
    public void OpenProjectAndLoginRefreshStatusWithoutStartingLoginAutomatically()
    {
        RunSta(() =>
        {
            var service = new FakeStatusService();
            var loginCount = 0;
            var window = CreateWindow(service, _ =>
            {
                loginCount++;
                service.Read = _ => Task.FromResult(new AuthenticationStatus(true, Now.AddYears(10)));
                return true;
            });
            window.Show();
            var label = Control<TextBlock>(window, "AuthenticationStatusText");
            var header = Assert.IsType<StackPanel>(label.Parent);
            Assert.Same(Control<TextBlock>(window, "ActiveProjectText"), header.Children[0]);
            Assert.Same(label, header.Children[1]);
            Assert.Same(Control<TextBlock>(window, "CurrentBranchText"), header.Children[2]);
            Assert.Same(Control<ComboBox>(window, "BranchList").Parent, header.Children[3]);
            var list = Control<ListBox>(window, "ProjectList");
            Assert.Empty(service.RepositoryIds);
            Assert.Empty(label.Text);

            list.SelectedIndex = 0;
            Assert.Empty(label.Text);
            Click(window, "SwitchProjectButton");
            Assert.Equal("Not signed in", label.Text);
            Assert.Equal(Brushes.Firebrick, label.Foreground);
            Assert.Equal(0, loginCount);
            Click(window, "LoginButton");
            Assert.StartsWith("Authenticated: ", label.Text);
            Assert.Equal(Brushes.DarkGreen, label.Foreground);
            Assert.Equal(1, loginCount);
            Assert.Equal([FirstId, FirstId], service.RepositoryIds);

            service.Read = _ => Task.FromResult(new AuthenticationStatus(true, Now.AddYears(-10)));
            list.SelectedIndex = 1;
            Assert.StartsWith("Authenticated: ", label.Text);
            Click(window, "SwitchProjectButton");
            Assert.Equal("Expired", label.Text);
            Assert.Equal(Brushes.Firebrick, label.Foreground);
            Assert.Equal(1, loginCount);
            list.SelectedIndex = 2;
            Assert.Equal("Expired", label.Text);
            Click(window, "SwitchProjectButton");
            Assert.Equal("Available after initial download", label.Text);
            Assert.Equal(3, service.RepositoryIds.Count);
            list.SelectedIndex = -1;
            Assert.Equal("Available after initial download", label.Text);
            list.SelectedIndex = 2;
            Click(window, "RemoveProjectButton");
            Assert.Empty(label.Text);
            Assert.Empty(Control<TextBlock>(window, "ActiveProjectText").Text);
            window.Close();
        });
    }

    [Fact]
    public void ReadFailuresAreVisibleAndDelayedResultsCannotOverwriteOpenProjectOrClosedWindow()
    {
        RunSta(() =>
        {
            var delayed = new TaskCompletionSource<AuthenticationStatus>();
            var service = new FakeStatusService
            {
                Read = id => id == FirstId ? delayed.Task
                    : Task.FromException<AuthenticationStatus>(new IOException("cache read failed"))
            };
            var window = CreateWindow(service, _ => false);
            var label = Control<TextBlock>(window, "AuthenticationStatusText");
            var list = Control<ListBox>(window, "ProjectList");
            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            Assert.Equal("Checking authentication...", label.Text);
            list.SelectedIndex = 1;
            Click(window, "SwitchProjectButton");
            Assert.Contains("cache read failed", label.Text);
            Assert.DoesNotContain("Not signed in", label.Text);
            delayed.SetResult(new AuthenticationStatus(true, Now.AddYears(10)));
            PumpDispatcher();
            Assert.Contains("cache read failed", label.Text);

            delayed = new TaskCompletionSource<AuthenticationStatus>();
            list.SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            var beforeClose = label.Text;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            PumpDispatcher();
            Assert.True(closed);
            delayed.SetResult(new AuthenticationStatus(false, null));
            PumpDispatcher();
            Assert.Equal(beforeClose, label.Text);
        });
    }

    [Fact]
    public void ActivationRefreshesExternalAuthenticationChanges()
    {
        RunSta(() =>
        {
            var service = new FakeStatusService();
            var window = CreateWindow(service, _ => false);
            window.Show();
            Control<ListBox>(window, "ProjectList").SelectedIndex = 0;
            Click(window, "SwitchProjectButton");
            var label = Control<TextBlock>(window, "AuthenticationStatusText");
            Assert.Equal("Not signed in", label.Text);
            service.Read = _ => Task.FromResult(new AuthenticationStatus(true, null));
            typeof(Window).GetMethod("OnActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [EventArgs.Empty]);
            Assert.Equal("Credentials available (expiration unknown)", label.Text);
            window.Close();
        });
    }

    private WorkspaceWindow CreateWindow(FakeStatusService service, Func<string, bool> authenticate)
    {
        var server = new ServerProfile(Guid.NewGuid(), "Server", "lore://localhost:41337");
        Directory.CreateDirectory(Path.Combine(directory, "first", ".lore"));
        Directory.CreateDirectory(Path.Combine(directory, "second", ".lore"));
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save([
            new(Guid.NewGuid(), "First", server.Id, server.RemoteUrl, "first", Path.Combine(directory, "first"), FirstId),
            new(Guid.NewGuid(), "Second", server.Id, server.RemoteUrl, "second", Path.Combine(directory, "second"), SecondId),
            new(Guid.NewGuid(), "Pending", server.Id, server.RemoteUrl, "pending", Path.Combine(directory, "pending"),
                CloneState: ProjectCloneState.Pending)
        ]);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), confirm: (_, _) => true, authenticate: authenticate,
            authenticationStatusService: service, repositoryInfo: new FakeRepositoryInfo(),
            branchService: new FakeBranchService(), fileStatusService: new FakeFileStatusService(),
            fileLockService: new FakeFileLockService(), pushService: new FakePushStatusService());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class UnusedRepositoryService : IRepositoryService
    {
        public Task<IReadOnlyList<string>> ListAsync(string serverUrl)
            => throw new InvalidOperationException("Unexpected network access");
        public Task CloneAsync(string serverUrl, string repositoryName, string path, IProgress<string> progress)
            => throw new InvalidOperationException("Unexpected clone");
    }

    private sealed class FakeStatusService : IAuthenticationStatusService
    {
        public List<string> RepositoryIds { get; } = [];
        public Func<string, Task<AuthenticationStatus>> Read { get; set; }
            = _ => Task.FromResult(new AuthenticationStatus(false, null));

        public Task<AuthenticationStatus> ReadAsync(string repositoryId)
        {
            RepositoryIds.Add(repositoryId);
            return Read(repositoryId);
        }
    }

    private sealed class FakeRepositoryInfo : IProjectRepositoryInfo
    {
        private const string Url = "lore://localhost:41337";
        public Task<string> ReadServerUrlAsync(string path) => Task.FromResult(Url);
        public Task<LocalRepositoryInfo> ReadLocalAsync(string path) =>
            Task.FromResult(new LocalRepositoryInfo(Url, Path.GetFileName(path) == "first" ? FirstId : SecondId));
        public Task<RemoteRepositoryInfo> ReadRemoteAsync(string path) => throw new InvalidOperationException("Unexpected remote access");
    }

    private sealed class FakeBranchService : IBranchService
    {
        public Task<BranchListResult> ListAsync(string path) =>
            Task.FromResult(new BranchListResult([new BranchEntry("1", "main", true, false, true)], false));
        public Task SwitchAsync(string path, BranchEntry branch) => throw new InvalidOperationException("Unexpected switch");
    }
}
