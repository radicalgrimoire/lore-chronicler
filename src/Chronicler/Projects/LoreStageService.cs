using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public interface IStageService
{
    Task StageAsync(string root, string path);
    Task UnstageAsync(string root, IReadOnlyList<string> paths);
}

public sealed class LoreStageService : IStageService
{
    public Task StageAsync(string root, string path) => Task.Run(async () =>
    {
        ValidatePaths(root, [path]);
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A link cannot be staged directly.");
        using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
        using var args = new LoreFileStageArgs { Paths = [path], Scan = true };
        string? error = null;
        var ended = false;
        var sync = new object();
        await Lore.FileStage(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.ERROR) error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                if (ev.Tag == LoreEventTag.FILE_STAGE_END) ended = true;
            }
        }).WaitAsync();
        if (error is not null) throw new InvalidDataException($"Could not complete staging: {error}");
        if (!ended) throw new InvalidDataException("Could not verify staging completion.");
    });

    public Task UnstageAsync(string root, IReadOnlyList<string> paths) => Task.Run(async () =>
    {
        ValidatePaths(root, paths);
        using var globals = new LoreGlobalArgs { RepositoryPath = root, Offline = true };
        using var args = new LoreFileUnstageArgs { Paths = paths.ToArray() };
        string? error = null;
        var ended = false;
        var sync = new object();
        await Lore.FileUnstage(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.ERROR) error ??= ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                if (ev.Tag == LoreEventTag.FILE_UNSTAGE_END) ended = true;
            }
        }).WaitAsync();
        if (error is not null) throw new InvalidDataException($"Could not complete unstaging: {error}");
        if (!ended) throw new InvalidDataException("Could not verify unstaging completion.");
    });

    private static void ValidatePaths(string root, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) throw new ArgumentException("No item is selected.", nameof(paths));
        var fullRoot = Path.GetFullPath(root);
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
            if (relative != ".") LoreFileStatusService.ResolvePath(fullRoot, relative);
        }
    }
}
