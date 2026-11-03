namespace Chronicler.Servers;

public sealed record ServerProfile(Guid Id, string Name, string RemoteUrl);

public static class ServerProfileValidator
{
    public static bool TryValidate(string? name, string? remoteUrl, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a display name.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteUrl)
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || !string.Equals(uri.Scheme, "lore", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not ("" or "/")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Port == 0)
        {
            error = "Enter a server URL such as lore://hostname:41337 without credentials or a repository path.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
