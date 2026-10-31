using System.Diagnostics;

namespace Chronicler.Authentication;

public interface ILoginBrowser
{
    void Open(string url);
}

public sealed class LoginBrowser : ILoginBrowser
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    public static bool IsSafeUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
    }
}
