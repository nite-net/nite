using System.Net.Http.Headers;
using System.Text;

namespace Nite.Configuration;

/// <summary>
/// Which registry serves which package, and which credentials go with which registry. Credentials
/// follow npm's "nerf dart" keys (<c>//npm.example.com/path/:_authToken=…</c>): a request only carries
/// a credential when its URL is under that host and path, so a token never leaks to another host
/// (a tarball on a CDN, say).
/// </summary>
public sealed class RegistrySettings
{
    public const string NpmRegistry = "https://registry.npmjs.org/";

    public static RegistrySettings Default { get; } = new(NpmRegistry, new Dictionary<string, string>(), []);

    internal RegistrySettings(string defaultRegistry, IReadOnlyDictionary<string, string> scopes, IReadOnlyList<RegistryCredential> credentials)
    {
        DefaultRegistry = Normalize(defaultRegistry);
        Scopes = scopes.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value), StringComparer.Ordinal);
        Credentials = credentials;
    }

    /// <summary>The registry for unscoped packages and scopes without their own, ending in <c>/</c>.</summary>
    public string DefaultRegistry { get; }

    /// <summary>Scope (<c>@mycompany</c>) → registry URL ending in <c>/</c>.</summary>
    public IReadOnlyDictionary<string, string> Scopes { get; }

    internal IReadOnlyList<RegistryCredential> Credentials { get; }

    public string RegistryFor(string packageName)
    {
        if (packageName.StartsWith('@') && packageName.IndexOf('/') is var slash and > 0
            && Scopes.TryGetValue(packageName[..slash], out var scoped))
        {
            return scoped;
        }
        return DefaultRegistry;
    }

    /// <summary>The Authorization header for a URL, from the most specific matching credential, if any.</summary>
    public AuthenticationHeaderValue? AuthorizationFor(Uri url)
    {
        var key = NerfDart(url);
        var credential = Credentials
            .Where(c => key.StartsWith(c.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Prefix.Length)
            .FirstOrDefault();
        return credential?.ToHeader();
    }

    /// <summary><c>https://npm.example.com:8443/a/b</c> → <c>//npm.example.com:8443/a/b</c>.</summary>
    internal static string NerfDart(Uri url) =>
        "//" + url.Host + (url.IsDefaultPort ? "" : ":" + url.Port) + url.AbsolutePath;

    private static string Normalize(string url) => url.EndsWith('/') ? url : url + "/";
}

/// <summary>A credential from <c>.npmrc</c>, scoped to a host and path prefix such as <c>//npm.example.com/</c>.</summary>
internal sealed record RegistryCredential(string Prefix, string? Token, string? Basic)
{
    public AuthenticationHeaderValue? ToHeader() =>
        !string.IsNullOrEmpty(Token) ? new AuthenticationHeaderValue("Bearer", Token)
        : !string.IsNullOrEmpty(Basic) ? new AuthenticationHeaderValue("Basic", Basic)
        : null;

    /// <summary>Collects credentials from .npmrc keys: <c>:_authToken</c>, <c>:_auth</c>, or <c>:username</c> + <c>:_password</c>.</summary>
    public static List<RegistryCredential> From(IReadOnlyDictionary<string, string> values)
    {
        var byPrefix = new Dictionary<string, (string? Token, string? Auth, string? User, string? Password)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            var colon = key.LastIndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var prefix = key[..colon];
            if (!prefix.EndsWith('/'))
            {
                prefix += "/";
            }
            var entry = byPrefix.GetValueOrDefault(prefix);
            switch (key[(colon + 1)..])
            {
                case "_authToken": entry.Token = value; break;
                case "_auth": entry.Auth = value; break;
                case "username": entry.User = value; break;
                case "_password": entry.Password = value; break;
                default: continue;
            }
            byPrefix[prefix] = entry;
        }

        var credentials = new List<RegistryCredential>();
        foreach (var (prefix, entry) in byPrefix)
        {
            var basic = entry.Auth;
            if (basic is null && entry.User is not null && entry.Password is not null)
            {
                // npm stores _password base64-encoded; the header wants base64("user:password").
                var password = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Password));
                basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{entry.User}:{password}"));
            }
            credentials.Add(new RegistryCredential(prefix, entry.Token, basic));
        }
        return credentials;
    }
}
