using System.IO;
using Chronicler.Servers;
using Chronicler.Settings;

namespace Chronicler.Projects;

public sealed class ProjectSettingsStore(string filePath)
{
    private readonly AtomicSettingsFile<ProjectSettings> file = new(filePath);
    public string DirectoryPath => Path.GetDirectoryName(filePath)
        ?? throw new InvalidOperationException("プロジェクト設定の保存先が不正です。");
    public static string DefaultPath => Path.Combine(
        Path.GetDirectoryName(ServerSettingsStore.DefaultPath)!, "projects.json");

    public IReadOnlyList<ProjectProfile> Load()
    {
        var settings = file.Load();
        if (settings is null)
        {
            return [];
        }
        Validate(settings);
        return settings.Projects!;
    }

    public void Save(IReadOnlyCollection<ProjectProfile> projects)
    {
        var settings = new ProjectSettings { Version = 1, Projects = projects.ToList() };
        Validate(settings);
        file.Save(settings);
    }

    private static void Validate(ProjectSettings settings)
    {
        if (settings.Version != 1 || settings.Projects is null)
        {
            throw new InvalidDataException("プロジェクト設定の形式またはバージョンが不正です。");
        }
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in settings.Projects)
        {
            if (project is null || project.Id == Guid.Empty || project.ServerId == Guid.Empty
                || string.IsNullOrWhiteSpace(project.Name) || project.Name != project.Name.Trim()
                || !ServerProfileValidator.TryValidate("server", project.ServerUrl, out _)
                || !ProjectValidator.IsValidRepositoryName(project.RepositoryName)
                || !Enum.IsDefined(project.CloneState)
                || (project.CloneState != ProjectCloneState.Ready && project.RepositoryId is not null)
                || (project.RepositoryId is not null
                    && (project.RepositoryId.Length != 32 || !project.RepositoryId.All(Uri.IsHexDigit)
                        || project.RepositoryId.All(c => c == '0')))
                || !ProjectValidator.TryNormalizePath(project.LocalPath, out var path, out _)
                || !ids.Add(project.Id) || !paths.Add(path))
            {
                throw new InvalidDataException("プロジェクト設定に不正または重複した項目があります。");
            }
        }
    }

    private sealed class ProjectSettings
    {
        public int Version { get; set; }
        public List<ProjectProfile>? Projects { get; set; }
    }
}
