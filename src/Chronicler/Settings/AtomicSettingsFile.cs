using System.IO;
using System.Text.Json;

namespace Chronicler.Settings;

public sealed class AtomicSettingsFile<T>(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private byte[]? loadedContents;
    private bool loaded;

    public T? Load()
    {
        var contents = ReadContents();
        var value = contents is null ? default : JsonSerializer.Deserialize<T>(contents, Options)
            ?? throw new InvalidDataException("The settings file is empty.");
        loadedContents = contents;
        loaded = true;
        return value;
    }

    public void Save(T value)
    {
        if (!loaded)
        {
            throw new InvalidOperationException("Load the settings before saving.");
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("The settings save location is invalid.");
        Directory.CreateDirectory(directory);
        using var settingsLock = new FileStream(filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadContents();
        if (!(loadedContents is null && current is null)
            && (loadedContents is null || current is null || !loadedContents.AsSpan().SequenceEqual(current)))
        {
            throw new InvalidOperationException("The settings were changed by another process. Restart Chronicler to avoid overwriting them.");
        }
        var contents = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        var tempFile = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
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
}
