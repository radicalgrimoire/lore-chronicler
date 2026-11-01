using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record FileLockOwner(string Id, string? Name, string Branch);
public sealed record FileLockResult(IReadOnlyDictionary<string, IReadOnlyList<FileLockOwner>> Files, string? Warning);

public interface IFileLockService
{
    Task<FileLockResult> QueryAsync(string path);
}

public sealed class LoreFileLockService : IFileLockService
{
    public Task<FileLockResult> QueryAsync(string path) => Task.Run(() => QueryCoreAsync(path));

    private static async Task<FileLockResult> QueryCoreAsync(string path)
    {
        var locks = new List<(string Path, string Owner, string Branch)>();
        var sync = new object();
        string? queryError = null;
        ulong? expectedCount = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var query = new LoreLockFileQueryArgs();
        await Lore.LockFileQuery(globals, query).Callback((ev, _) =>
        {
            if (ev.Tag == LoreEventTag.LOCK_FILE_QUERY_BEGIN)
            {
                var count = ev.GetData<LoreLockFileQueryBeginEventDataFFI>().Count;
                lock (sync) expectedCount = count;
            }
            else if (ev.Tag == LoreEventTag.LOCK_FILE_QUERY)
            {
                var data = ev.GetData<LoreLockFileQueryEventDataFFI>();
                lock (sync) locks.Add((data.Path, data.Owner, Convert.ToHexString(data.Branch.Data)));
            }
            else if (ev.Tag == LoreEventTag.ERROR)
            {
                var message = ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                lock (sync) queryError ??= message;
            }
        }).WaitAsync();
        if (queryError is not null) throw new InvalidDataException($"ロック一覧を完全に取得できません: {queryError}");
        if (expectedCount is null || expectedCount != (ulong)locks.Count)
            throw new InvalidDataException("Loreからロック一覧を完全に取得できませんでした。");

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string? warning = null;
        var owners = locks.Select(entry => entry.Owner).Where(owner => !string.IsNullOrWhiteSpace(owner))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (owners.Length > 0)
        {
            try
            {
                string? authError = null;
                using var args = new LoreAuthUserInfoArgs { UserIds = owners };
                await Lore.AuthUserInfo(globals, args).Callback((ev, _) =>
                {
                    if (ev.Tag == LoreEventTag.AUTH_USER_INFO)
                    {
                        var data = ev.GetData<LoreAuthUserInfoEventDataFFI>();
                        lock (sync) names[data.Id] = data.Name;
                    }
                    else if (ev.Tag == LoreEventTag.ERROR)
                    {
                        var message = ev.GetData<LoreErrorEventDataFFI>().ErrorInner;
                        lock (sync) authError ??= message;
                    }
                }).WaitAsync();
                if (authError is not null) warning = $"取得者名を一部確認できません: {authError}";
            }
            catch (LoreError ex)
            {
                warning = $"取得者名を確認できません（Loreエラー {ex.ReturnCode}）: {ex.Message}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                warning = $"取得者名を確認できません: {ex.Message}";
            }
        }

        var files = new Dictionary<string, List<FileLockOwner>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in locks)
        {
            var fullPath = LoreFileStatusService.ResolvePath(path, entry.Path);
            if (!files.TryGetValue(fullPath, out var list)) files.Add(fullPath, list = []);
            names.TryGetValue(entry.Owner, out var name);
            list.Add(new FileLockOwner(entry.Owner, name, entry.Branch));
        }
        if (warning is null && locks.Any(entry => !names.TryGetValue(entry.Owner, out var name) || string.IsNullOrWhiteSpace(name)))
            warning = "一部の取得者名を確認できないため、ユーザーIDを表示しています。";
        return new FileLockResult(files.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyList<FileLockOwner>)entry.Value.ToArray(), StringComparer.OrdinalIgnoreCase), warning);
    }
}
