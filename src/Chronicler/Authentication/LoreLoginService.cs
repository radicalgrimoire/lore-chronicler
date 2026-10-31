using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Authentication;

public sealed record LoginProgress(string? Url = null, string? UserCode = null, ulong? RemainingSeconds = null);

public interface ILoginService
{
    Task<string?> LoginAsync(string remoteUrl, IProgress<LoginProgress> progress);
}

public sealed class LoreLoginService : ILoginService
{
    public async Task<string?> LoginAsync(string remoteUrl, IProgress<LoginProgress> progress)
    {
        string? userName = null;
        using var globals = new LoreGlobalArgs();
        using var args = new LoreAuthLoginInteractiveArgs { RemoteUrl = remoteUrl, NoBrowser = true };
        await Lore.AuthLoginInteractive(globals, args)
            .Callback((loreEvent, _) =>
            {
                switch (loreEvent.Tag)
                {
                    case LoreEventTag.AUTH_URL:
                        var url = loreEvent.GetData<LoreAuthUrlEventDataFFI>();
                        progress.Report(new LoginProgress(Url: url.Url, UserCode: url.UserCode));
                        break;
                    case LoreEventTag.AUTH_PENDING:
                        var pending = loreEvent.GetData<LoreAuthPendingEventDataFFI>();
                        progress.Report(new LoginProgress(RemainingSeconds: pending.RemainingSecs));
                        break;
                    case LoreEventTag.AUTH_USER_INFO:
                        var user = loreEvent.GetData<LoreAuthUserInfoEventDataFFI>();
                        userName = user.Name;
                        break;
                }
            })
            .WaitAsync();
        return userName;
    }
}
