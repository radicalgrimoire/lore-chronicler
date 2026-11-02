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
        Assert.Equal("認証済み（期限 2026/11/02 21:00）", status.GetText(Now));

        status = AuthenticationStatus.FromIdentities("aabb",
            [new("https://auth.example", "aabb", Now.AddHours(3)), new("https://auth.example", "", Now)]);
        Assert.Equal(Now.AddHours(3), status.ExpiresAt);
    }

    [Fact]
    public void DistinguishesMissingIdentityUnknownExpiryAndExpiredIncludingExactBoundary()
    {
        var missing = AuthenticationStatus.FromIdentities("aabb", [new("https://auth.example", "", Now.AddDays(1))]);
        Assert.False(missing.HasIdentity);
        Assert.Equal("未ログイン", missing.GetText(Now));
        Assert.Equal("認証情報あり（期限不明）",
            AuthenticationStatus.FromIdentities("aabb", [new("https://auth.example", "aabb", null)]).GetText(Now));
        var status = new AuthenticationStatus(true, Now);
        Assert.Equal("認証済み（期限 2026/11/02 19:00）", status.GetText(Now.AddTicks(-1)));
        Assert.Equal("期限切れ（期限 2026/11/02 19:00）", status.GetText(Now));
        Assert.Equal("期限切れ（期限 2026/11/02 19:00）", status.GetText(Now.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => AuthenticationStatus.FromIdentities("", []));
    }

    [Fact]
    public void SelectionAndLoginRefreshStatusWithoutStartingLoginAutomatically()
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
            Assert.Equal(Orientation.Vertical, header.Orientation);
            Assert.Same(Control<TextBlock>(window, "ServerText"), header.Children[0]);
            Assert.Same(label, header.Children[1]);
            Assert.Equal(HorizontalAlignment.Right, header.HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Right, label.HorizontalAlignment);
            var list = Control<ListBox>(window, "ProjectList");
            Assert.Empty(service.RepositoryIds);
            Assert.Contains("プロジェクトを選択", label.Text);

            list.SelectedIndex = 0;
            Assert.Equal("未ログイン", label.Text);
            Assert.Equal(Brushes.Firebrick, label.Foreground);
            Assert.Equal(0, loginCount);
            Click(window, "LoginButton");
            Assert.StartsWith("認証済み（期限 ", label.Text);
            Assert.Equal(Brushes.DarkGreen, label.Foreground);
            Assert.Equal(1, loginCount);
            Assert.Equal([FirstId, FirstId], service.RepositoryIds);

            service.Read = _ => Task.FromResult(new AuthenticationStatus(true, Now.AddYears(-10)));
            list.SelectedIndex = 1;
            Assert.StartsWith("期限切れ（期限 ", label.Text);
            Assert.Equal(Brushes.Firebrick, label.Foreground);
            Assert.Equal(1, loginCount);
            list.SelectedIndex = 2;
            Assert.Contains("初回取得後", label.Text);
            Assert.Equal(3, service.RepositoryIds.Count);
            list.SelectedIndex = -1;
            Assert.Contains("プロジェクトを選択", label.Text);
            window.Close();
        });
    }

    [Fact]
    public void ReadFailuresAreVisibleAndDelayedResultsCannotOverwriteSelectionOrClosedWindow()
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
            Assert.Contains("確認しています", label.Text);
            list.SelectedIndex = 1;
            Assert.Contains("cache read failed", label.Text);
            Assert.DoesNotContain("未ログイン", label.Text);
            delayed.SetResult(new AuthenticationStatus(true, Now.AddYears(10)));
            PumpDispatcher();
            Assert.Contains("cache read failed", label.Text);

            delayed = new TaskCompletionSource<AuthenticationStatus>();
            list.SelectedIndex = 0;
            var beforeClose = label.Text;
            window.Close();
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
            var label = Control<TextBlock>(window, "AuthenticationStatusText");
            Assert.Equal("未ログイン", label.Text);
            service.Read = _ => Task.FromResult(new AuthenticationStatus(true, null));
            typeof(Window).GetMethod("OnActivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [EventArgs.Empty]);
            Assert.Equal("認証情報あり（期限不明）", label.Text);
            window.Close();
        });
    }

    private WorkspaceWindow CreateWindow(FakeStatusService service, Func<string, bool> authenticate)
    {
        var server = new ServerProfile(Guid.NewGuid(), "Server", "lore://localhost:41337");
        var store = new ProjectSettingsStore(Path.Combine(directory, "projects.json"));
        store.Load();
        store.Save([
            new(Guid.NewGuid(), "First", server.Id, server.RemoteUrl, "first", Path.Combine(directory, "first"), FirstId),
            new(Guid.NewGuid(), "Second", server.Id, server.RemoteUrl, "second", Path.Combine(directory, "second"), SecondId),
            new(Guid.NewGuid(), "Pending", server.Id, server.RemoteUrl, "pending", Path.Combine(directory, "pending"),
                CloneState: ProjectCloneState.Pending)
        ]);
        return new WorkspaceWindow(server, store, new UnusedRepositoryService(), authenticate: authenticate,
            authenticationStatusService: service);
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
}
