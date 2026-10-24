using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Configuration;

namespace Nite.Packages;

/// <summary>
/// A minimal npm registry client: abbreviated package metadata plus integrity-checked tarball
/// downloads. Tarballs are cached by integrity hash, so reinstalls and other projects are free;
/// metadata is cached with its ETag and revalidated, so an unchanged package costs a 304.
/// Scoped packages go to their scope's registry, and credentials only go to the hosts they name.
/// </summary>
public sealed class NpmRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly RegistrySettings _settings;
    private readonly string _cacheDirectory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Task<NpmPackument>> _packuments = new();

    /// <param name="cacheDirectory">The cache root; defaults to the user cache directory.</param>
    public NpmRegistry(HttpClient http, RegistrySettings? settings = null, string? cacheDirectory = null, ILogger? logger = null)
    {
        _http = http;
        _settings = settings ?? RegistrySettings.Default;
        _cacheDirectory = cacheDirectory ?? NiteDirectories.Current.Cache;
        _logger = logger ?? NullLogger.Instance;
    }

    public RegistrySettings Settings => _settings;

    public Task<NpmPackument> GetPackumentAsync(string name, CancellationToken cancellationToken = default) =>
        _packuments.GetOrAdd(name, n => FetchPackumentAsync(n, cancellationToken));

    private async Task<NpmPackument> FetchPackumentAsync(string name, CancellationToken cancellationToken)
    {
        var registry = _settings.RegistryFor(name);
        var url = new Uri(registry + EncodeName(name));
        var (bodyPath, etagPath) = MetadataPaths(registry, name);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The "corgi" document leaves out readmes and other fields an installer never needs.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));
        request.Headers.Authorization = _settings.AuthorizationFor(url);
        if (File.Exists(bodyPath) && File.Exists(etagPath))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(etagPath, cancellationToken));
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && File.Exists(bodyPath))
        {
            _logger.LogDebug("{Package}: metadata unchanged (304)", name);
            return Deserialize(name, await File.ReadAllBytesAsync(bodyPath, cancellationToken));
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NitePackageException($"Package '{name}' was not found in {registry}.");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new NitePackageException(request.Headers.Authorization is null
                ? $"{registry} wants credentials for '{name}' ({(int)response.StatusCode}). Add an _authToken for it to ~/.npmrc or the project's .npmrc."
                : $"{registry} rejected the credentials for '{name}' ({(int)response.StatusCode}). Check the token in .npmrc.");
        }
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var packument = Deserialize(name, body);
        if (response.Headers.ETag is { } etag)
        {
            await WriteAtomicAsync(bodyPath, body, cancellationToken);
            await WriteAtomicAsync(etagPath, System.Text.Encoding.UTF8.GetBytes(etag.ToString()), cancellationToken);
        }
        return packument;
    }

    /// <summary>Downloads a tarball (or reads it from the cache) and verifies its integrity.</summary>
    public async Task<byte[]> GetTarballAsync(NpmDist dist, CancellationToken cancellationToken = default)
    {
        var integrity = Integrity.Parse(dist.Integrity, dist.Shasum);
        var cachePath = Path.Combine(_cacheDirectory, "tarballs", integrity.CacheKey + ".tgz");
        if (File.Exists(cachePath))
        {
            var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            if (integrity.Matches(cached))
            {
                return cached;
            }
        }

        var url = new Uri(dist.Tarball);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = _settings.AuthorizationFor(url);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new NitePackageException($"{url.Host} refused the download of {dist.Tarball} ({(int)response.StatusCode}). Check the credentials for it in .npmrc.");
        }
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!integrity.Matches(bytes))
        {
            throw new NitePackageException($"Integrity check failed for {dist.Tarball}.");
        }

        await WriteAtomicAsync(cachePath, bytes, cancellationToken);
        return bytes;
    }

    /// <summary>Checks every cached tarball against the hash in its name and deletes the ones that do not match.</summary>
    public static (int Checked, int Removed) VerifyTarballCache(string cacheDirectory)
    {
        var directory = Path.Combine(cacheDirectory, "tarballs");
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        int checkedCount = 0, removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.tgz"))
        {
            checkedCount++;
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf('-');
            var valid = dash > 0 && new Integrity(name[..dash], Convert.FromHexString(name[(dash + 1)..])).Matches(File.ReadAllBytes(file));
            if (!valid)
            {
                File.Delete(file);
                removed++;
            }
        }
        return (checkedCount, removed);
    }

    private (string Body, string ETag) MetadataPaths(string registry, string name)
    {
        // One folder per registry, so a private registry's "react" never shadows the public one.
        var registryKey = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(registry)))[..12];
        var file = Path.Combine(_cacheDirectory, "metadata", registryKey, EncodeName(name));
        return (file + ".json", file + ".etag");
    }

    private static NpmPackument Deserialize(string name, byte[] body) =>
        JsonSerializer.Deserialize<NpmPackument>(body, JsonOptions)
        ?? throw new NitePackageException($"The registry returned an empty document for '{name}'.");

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private static string EncodeName(string name) =>
        name.StartsWith('@') ? "@" + Uri.EscapeDataString(name[1..]) : Uri.EscapeDataString(name);

    private sealed record Integrity(string Algorithm, byte[] Digest)
    {
        public string CacheKey => $"{Algorithm}-{Convert.ToHexStringLower(Digest)}";

        public static Integrity Parse(string? sri, string? shasum)
        {
            // Prefer the strongest SRI hash; very old packages only publish a sha1 "shasum".
            if (!string.IsNullOrEmpty(sri))
            {
                var best = sri.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('-', 2))
                    .Where(part => part.Length == 2 && part[0] is "sha512" or "sha384" or "sha256" or "sha1")
                    .OrderByDescending(part => part[0] switch { "sha512" => 4, "sha384" => 3, "sha256" => 2, _ => 1 })
                    .FirstOrDefault();
                if (best is not null)
                {
                    return new Integrity(best[0], Convert.FromBase64String(best[1]));
                }
            }
            if (!string.IsNullOrEmpty(shasum))
            {
                return new Integrity("sha1", Convert.FromHexString(shasum));
            }
            throw new NitePackageException("The registry did not provide an integrity hash for a tarball.");
        }

        public bool Matches(byte[] data)
        {
            var actual = Algorithm switch
            {
                "sha512" => SHA512.HashData(data),
                "sha384" => SHA384.HashData(data),
                "sha256" => SHA256.HashData(data),
                _ => SHA1.HashData(data),
            };
            return CryptographicOperations.FixedTimeEquals(actual, Digest);
        }
    }
}

public sealed class NpmPackument
{
    public string Name { get; init; } = "";

    [JsonPropertyName("dist-tags")]
    public Dictionary<string, string> DistTags { get; init; } = [];

    public Dictionary<string, NpmVersion> Versions { get; init; } = [];
}

public sealed class NpmVersion
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public Dictionary<string, string>? Dependencies { get; init; }
    public Dictionary<string, string>? OptionalDependencies { get; init; }
    public Dictionary<string, string>? PeerDependencies { get; init; }
    public Dictionary<string, NpmPeerMeta>? PeerDependenciesMeta { get; init; }
    public NpmDist Dist { get; init; } = new();
}

public sealed class NpmPeerMeta
{
    public bool Optional { get; init; }
}

public sealed class NpmDist
{
    public string Tarball { get; init; } = "";
    public string? Integrity { get; init; }
    public string? Shasum { get; init; }
}

public sealed class NitePackageException(string message) : Exception(message);
