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
            throw new ArgumentException("The server URL or repository name is invalid.");
        }
        return $"{serverUrl.TrimEnd('/')}/{name}";
    }

    public static bool TryNormalizePath(string? value, out string path, out string error)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            error = "Specify an absolute path for the clone destination.";
            return false;
        }
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
            if (Path.GetPathRoot(path) == path)
            {
                error = "A drive root cannot be used as a clone destination.";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The clone destination path is invalid.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
