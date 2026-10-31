using System.IO;

namespace Chronicler.Projects;

public sealed record LocalFileEntry(string Name, string Path, bool IsDirectory, bool IsReparsePoint);

public static class LocalFileTree
{
    public static IReadOnlyList<LocalFileEntry> ReadChildren(string root, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<LocalFileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (string.Equals(root, directory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, ".lore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var attributes = File.GetAttributes(path);
            entries.Add(new LocalFileEntry(name, path,
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0));
        }
        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
