using System.IO;
using Chronicler.Settings;

namespace Chronicler.Servers;

public sealed class ServerSettingsStore(string filePath)
{
    private const int CurrentVersion = 1;
    private readonly AtomicSettingsFile<ServerSettings> file = new(filePath);
    public string DirectoryPath => Path.GetDirectoryName(filePath)
        ?? throw new InvalidOperationException("The server settings save location is invalid.");

    public static string DefaultPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException("Could not locate the Windows local application data folder.");
            }
            return Path.Combine(localAppData, "Chronicler", "servers.json");
        }
    }

    public IReadOnlyList<ServerProfile> Load()
    {
        var settings = file.Load();
        if (settings is null)
        {
            return [];
        }
        Validate(settings);
        return settings.Servers!;
    }

    public void Save(IReadOnlyCollection<ServerProfile> servers)
    {
        var settings = new ServerSettings { Version = CurrentVersion, Servers = servers.ToList() };
        Validate(settings);
        file.Save(settings);
    }

    private static void Validate(ServerSettings settings)
    {
        if (settings.Version != CurrentVersion || settings.Servers is null)
        {
            throw new InvalidDataException("The server settings format or version is invalid.");
        }

        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.Servers)
        {
            if (server is null || server.Id == Guid.Empty
                || !ServerProfileValidator.TryValidate(server.Name, server.RemoteUrl, out _)
                || server.Name != server.Name.Trim()
                || server.RemoteUrl != server.RemoteUrl.Trim()
                || !ids.Add(server.Id)
                || !names.Add(server.Name))
            {
                throw new InvalidDataException("Server settings contain invalid or duplicate entries.");
            }
        }
    }

    private sealed class ServerSettings
    {
        public int Version { get; set; }
        public List<ServerProfile>? Servers { get; set; }
    }
}
