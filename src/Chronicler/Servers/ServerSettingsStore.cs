using System.IO;
using System.Text.Json;

namespace Chronicler.Servers;

public sealed class ServerSettingsStore(string filePath)
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private byte[]? loadedContents;
    private bool loaded;

    public static string DefaultPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException("Windowsのローカルアプリケーションデータフォルダーを取得できません。");
            }
            return Path.Combine(localAppData, "Chronicler", "servers.json");
        }
    }

    public IReadOnlyList<ServerProfile> Load()
    {
        var contents = ReadContents();
        if (contents is null)
        {
            loadedContents = null;
            loaded = true;
            return [];
        }

        var settings = JsonSerializer.Deserialize<ServerSettings>(contents, JsonOptions)
            ?? throw new InvalidDataException("サーバー設定が空です。");
        Validate(settings);
        loadedContents = contents;
        loaded = true;
        return settings.Servers!;
    }

    private byte[]? ReadContents()
    {
        try
        {
            return File.ReadAllBytes(filePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Save(IReadOnlyCollection<ServerProfile> servers)
    {
        var settings = new ServerSettings { Version = CurrentVersion, Servers = servers.ToList() };
        Validate(settings);
        if (!loaded)
        {
            throw new InvalidOperationException("保存する前にサーバー設定を読み込んでください。");
        }
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("サーバー設定の保存先が不正です。");
        Directory.CreateDirectory(directory);
        using var settingsLock = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var currentContents = ReadContents();
        if (!(loadedContents is null && currentContents is null)
            && (loadedContents is null || currentContents is null || !loadedContents.AsSpan().SequenceEqual(currentContents)))
        {
            throw new InvalidOperationException("サーバー設定が別のプロセスで変更されました。上書きを避けるため、Chroniclerを再起動してください。");
        }

        var contents = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var tempFile = Path.Combine(directory, $".servers-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempFile, filePath, overwrite: true);
            loadedContents = contents;
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static void Validate(ServerSettings settings)
    {
        if (settings.Version != CurrentVersion || settings.Servers is null)
        {
            throw new InvalidDataException("サーバー設定の形式またはバージョンが不正です。");
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
                throw new InvalidDataException("サーバー設定に不正または重複した項目があります。");
            }
        }
    }

    private sealed class ServerSettings
    {
        public int Version { get; set; }
        public List<ServerProfile>? Servers { get; set; }
    }
}
