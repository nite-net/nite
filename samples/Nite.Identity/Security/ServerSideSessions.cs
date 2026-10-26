using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Nite.Identity.Security;

/// <summary>
/// Keeps sign-in sessions on the server. The cookie carries only a random session key, so signing
/// out removes the session itself: a copy of the cookie stops working too, which a cookie that
/// carries the whole session cannot promise. Sessions are encrypted with Data Protection before
/// they reach the cache.
/// </summary>
/// <remarks>
/// The cache is <see cref="IDistributedCache"/>: in memory for one instance, or Redis or SQL Server
/// (AddStackExchangeRedisCache, AddDistributedSqlServerCache) when the app runs on several.
/// </remarks>
public sealed class ServerSideSessions(IDistributedCache cache, IDataProtectionProvider dataProtection) : ITicketStore
{
    private const string KeyPrefix = "session:";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Nite.Identity.Sessions");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) =>
        cache.SetAsync(KeyPrefix + key, _protector.Protect(TicketSerializer.Default.Serialize(ticket)), new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30),
        });

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var stored = await cache.GetAsync(KeyPrefix + key);
        if (stored is null)
        {
            return null;
        }
        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(stored));
        }
        catch (CryptographicException)
        {
            // Encrypted with a key that is gone: treat it as signed out.
            return null;
        }
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(KeyPrefix + key);
}

public static class ServerSideSessionsExtensions
{
    /// <summary>Stores the Identity sign-in cookie's sessions in <see cref="IDistributedCache"/>.</summary>
    public static IServiceCollection AddServerSideSessions(this IServiceCollection services)
    {
        services.AddSingleton<ServerSideSessions>();
        services.AddOptions<CookieAuthenticationOptions>(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme)
            .Configure<ServerSideSessions>((options, sessions) => options.SessionStore = sessions);
        return services;
    }
}
