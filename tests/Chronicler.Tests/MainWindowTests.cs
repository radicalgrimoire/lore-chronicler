using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Chronicler.Authentication;
using Chronicler.Servers;

namespace Chronicler.Tests;

public class MainWindowTests
{
    [Fact]
    public void SavesProfilesAndStartsLoginOnlyOnRequest()
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ServerSettingsStore(Path.Combine(directory, "servers.json"));
                var login = new FakeLoginService();
                var browser = new FakeBrowser();
                var window = new MainWindow(store, login, browser);
                var name = Control<TextBox>(window, "NameInput");
                var url = Control<TextBox>(window, "UrlInput");
                var list = Control<ListBox>(window, "ServerList");

                Assert.Equal(0, login.CallCount);
                Assert.Empty(browser.Opened);
                name.Text = "Main";
                url.Text = "lore://localhost:41337";
                Click(window, "SaveButton");
                Assert.Single(store.Load());
                Assert.NotNull(list.SelectedItem);

                Click(window, "NewButton");
                name.Text = "Test";
                url.Text = "lore://example.com:41337";
                Click(window, "SaveButton");
                Assert.Equal(2, store.Load().Count);
                url.Text = "lore://example.com:41338";
                Click(window, "SaveButton");
                Assert.Equal("lore://example.com:41338", store.Load()[1].RemoteUrl);

                Click(window, "LoginButton");
                Assert.Equal(1, login.CallCount);
                Assert.Equal("lore://example.com:41338", login.RemoteUrl);
                Assert.False(Control<Button>(window, "LoginButton").IsEnabled);
                Assert.Empty(browser.Opened);

                login.Progress!.Report(new LoginProgress(Url: "https://auth.example.com/login", UserCode: "ABCD"));
                PumpDispatcher();
                Assert.Equal(["https://auth.example.com/login"], browser.Opened);
                Assert.Contains("ABCD", Control<TextBlock>(window, "UserCodeText").Text);
                login.Progress.Report(new LoginProgress(Url: "https://auth.example.com/login", UserCode: "ABCD"));
                PumpDispatcher();
                Assert.Single(browser.Opened);

                login.Completion.SetResult("Test User");
                PumpDispatcher();
                Assert.Contains("Test User", Control<TextBlock>(window, "LoginStatus").Text);
                Assert.Equal(string.Empty, Control<TextBlock>(window, "UserCodeText").Text);
                Assert.True(Control<Button>(window, "LoginButton").IsEnabled);
                window.Close();
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void ReportsLoginFailureAndDoesNotOpenUnsafeUrl()
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ServerSettingsStore(Path.Combine(directory, "servers.json"));
                store.Load();
                store.Save([new ServerProfile(Guid.NewGuid(), "Main", "lore://localhost")]);
                var login = new FakeLoginService();
                var browser = new FakeBrowser();
                var window = new MainWindow(store, login, browser);
                var list = Control<ListBox>(window, "ServerList");
                list.SelectedIndex = 0;
                Click(window, "LoginButton");
                login.Progress!.Report(new LoginProgress(Url: "http://example.com/login"));
                PumpDispatcher();
                Assert.Empty(browser.Opened);
                Assert.Contains("安全", Control<TextBlock>(window, "LoginStatus").Text);

                login.Completion.SetException(new LoreVcs.LoreError(42));
                PumpDispatcher();
                Assert.Contains("42", Control<TextBlock>(window, "LoginStatus").Text);
                Assert.True(Control<Button>(window, "LoginButton").IsEnabled);
                Assert.Equal(Visibility.Collapsed, Control<Button>(window, "OpenBrowserButton").Visibility);
                window.Close();
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        });
    }

    [Fact]
    public void BrowserFailureOffersRetry()
    {
        RunSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "Chronicler.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new ServerSettingsStore(Path.Combine(directory, "servers.json"));
                store.Load();
                store.Save([new ServerProfile(Guid.NewGuid(), "Main", "lore://localhost")]);
                var login = new FakeLoginService();
                var browser = new FakeBrowser { Fail = true };
                var window = new MainWindow(store, login, browser);
                Control<ListBox>(window, "ServerList").SelectedIndex = 0;
                Click(window, "LoginButton");
                login.Progress!.Report(new LoginProgress(Url: "https://auth.example.com/login"));
                PumpDispatcher();
                Assert.Contains("開けません", Control<TextBlock>(window, "LoginStatus").Text);
                Assert.Equal(Visibility.Visible, Control<Button>(window, "OpenBrowserButton").Visibility);

                browser.Fail = false;
                Click(window, "OpenBrowserButton");
                Assert.Single(browser.Opened);
                login.Completion.SetResult(null);
                PumpDispatcher();
                Assert.Contains("完了", Control<TextBlock>(window, "LoginStatus").Text);
                window.Close();
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        });
    }

    internal static T Control<T>(Window window, string name) where T : FrameworkElement
        => Assert.IsType<T>(window.FindName(name));

    internal static void Click(Window window, string name)
        => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private sealed class FakeLoginService : ILoginService
    {
        public int CallCount { get; private set; }
        public string? RemoteUrl { get; private set; }
        public IProgress<LoginProgress>? Progress { get; private set; }
        public TaskCompletionSource<string?> Completion { get; } = new();

        public Task<string?> LoginAsync(string remoteUrl, IProgress<LoginProgress> progress)
        {
            CallCount++;
            RemoteUrl = remoteUrl;
            Progress = progress;
            return Completion.Task;
        }
    }

    private sealed class FakeBrowser : ILoginBrowser
    {
        public List<string> Opened { get; } = [];
        public bool Fail { get; set; }
        public void Open(string url)
        {
            if (Fail)
            {
                throw new Win32Exception("Browser unavailable");
            }
            Opened.Add(url);
        }
    }
}
