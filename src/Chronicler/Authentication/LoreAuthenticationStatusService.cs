using System.Collections.Concurrent;
using System.Globalization;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Authentication;

public sealed record StoredAuthIdentity(string AuthUrl, string Resource, DateTimeOffset? ExpiresAt);

public sealed record AuthenticationStatus(bool HasIdentity, DateTimeOffset? ExpiresAt)
{
    public string GetText(DateTimeOffset now)
    {
        if (!HasIdentity) return "Not signed in";
        if (ExpiresAt is not { } expires) return "Credentials available (expiration unknown)";
        if (expires <= now) return "Expired";
        var date = expires.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"Authenticated: {date}";
    }

    public static AuthenticationStatus FromIdentities(string repositoryId, IEnumerable<StoredAuthIdentity> identities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var stored = identities.ToArray();
        var repositories = stored.Where(identity =>
            string.Equals(identity.Resource, repositoryId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (repositories.Length == 0) return new AuthenticationStatus(false, null);
        var authUrls = repositories.Select(identity => identity.AuthUrl).ToHashSet(StringComparer.Ordinal);
        // A valid login token can renew an expired repository token on the next remote operation.
        var expires = repositories.Concat(stored.Where(identity =>
                string.IsNullOrEmpty(identity.Resource) && authUrls.Contains(identity.AuthUrl)))
            .Max(identity => identity.ExpiresAt);
        return new AuthenticationStatus(true, expires);
    }
}

public interface IAuthenticationStatusService
{
    Task<AuthenticationStatus> ReadAsync(string repositoryId);
}

public sealed class LoreAuthenticationStatusService : IAuthenticationStatusService
{
    public async Task<AuthenticationStatus> ReadAsync(string repositoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var identities = new ConcurrentQueue<StoredAuthIdentity>();
        using var globals = new LoreGlobalArgs { Offline = true };
        using var args = new LoreAuthListArgs { WithToken = false };
        await Lore.AuthList(globals, args).Callback((ev, _) =>
        {
            if (ev.Tag != LoreEventTag.AUTH_IDENTITY) return;
            var identity = ev.GetData<LoreAuthIdentityEventDataFFI>();
            identities.Enqueue(new StoredAuthIdentity(identity.AuthUrl, identity.Resource,
                identity.Expires == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(checked((long)identity.Expires))));
        }).WaitAsync();
        return AuthenticationStatus.FromIdentities(repositoryId, identities);
    }
}
