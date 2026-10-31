namespace Chronicler.Servers;

public sealed record ServerProfile(Guid Id, string Name, string RemoteUrl);

public static class ServerProfileValidator
{
    public static bool TryValidate(string? name, string? remoteUrl, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "サーバー名を入力してください。";
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
            error = "サーバーURLは lore://hostname:41337 の形式で入力してください。認証情報やリポジトリのパスは含められません。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
