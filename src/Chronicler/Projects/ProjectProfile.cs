using System.IO;
using Chronicler.Servers;

namespace Chronicler.Projects;

public enum ProjectCloneState { Ready, Pending, Started }

public sealed record ProjectProfile(Guid Id, string Name, Guid ServerId, string ServerUrl, string RepositoryName, string LocalPath,
    string? RepositoryId = null, ProjectCloneState CloneState = ProjectCloneState.Ready);

public static class ProjectValidator
{
    public static bool IsValidRepositoryName(string? name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= 1000
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
            && name.Split('/').All(segment => segment.Length > 0 && !segment.StartsWith('.'));
    }

    public static string RepositoryUrl(string serverUrl, string name)
    {
        if (!ServerProfileValidator.TryValidate("server", serverUrl, out _) || !IsValidRepositoryName(name))
        {
            throw new ArgumentException("サーバーURLまたはリポジトリ名が不正です。");
        }
        return $"{serverUrl.TrimEnd('/')}/{name}";
    }

    public static bool TryNormalizePath(string? value, out string path, out string error)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            error = "clone先は絶対パスで指定してください。";
            return false;
        }
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
            if (Path.GetPathRoot(path) == path)
            {
                error = "ドライブのルートはclone先に指定できません。";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "clone先のパスが不正です。";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
