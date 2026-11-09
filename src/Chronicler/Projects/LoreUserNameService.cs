using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record UserNameResult(IReadOnlyDictionary<string, string> Names, string? Warning);

public interface IUserNameService
{
    Task<UserNameResult> ResolveAsync(string path, IReadOnlyList<string> userIds);
}

public sealed class LoreUserNameService : IUserNameService
{
    public Task<UserNameResult> ResolveAsync(string path, IReadOnlyList<string> userIds) =>
        Task.Run(() => ResolveCoreAsync(path, userIds));

    private static async Task<UserNameResult> ResolveCoreAsync(string path, IReadOnlyList<string> userIds)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userIds.Count == 0) return new UserNameResult(names, null);

        var sync = new object();
        string? warning = null;
        try
        {
            using var operation = await LoreRepositoryOperationGate.AcquireAsync(path);
            using var globals = new LoreGlobalArgs { RepositoryPath = path, WorkingDirectory = Path.GetFullPath(path) };
            using var args = new LoreAuthUserInfoArgs { UserIds = userIds.ToArray() };
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
                    lock (sync) warning ??= message;
                }
            }).WaitAsync();
        }
        catch (LoreError ex)
        {
            warning = $"Lore error {ex.ReturnCode}: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            warning = ex.Message;
        }

        return new UserNameResult(names, warning);
    }
}
