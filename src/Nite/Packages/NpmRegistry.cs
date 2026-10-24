using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nite.Packages;

/// <summary>
/// A minimal npm registry client: abbreviated package metadata plus integrity-checked tarball
/// downloads, cached on disk by integrity hash so reinstalls and other projects are free.
/// </summary>
public sealed class NpmRegistry
{
    public const string DefaultUrl = "https://registry.npmjs.org";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string _registryUrl;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Task<NpmPackument>> _packuments = new();

    public NpmRegistry(HttpClient http, string? registryUrl = null, string? cacheDirectory = null)
    {
        _http = http;
        _registryUrl = (registryUrl ?? DefaultUrl).TrimEnd('/');
        _cacheDirectory = cacheDirectory ?? Path.Combine(NitePaths.CacheDirectory, "tarballs");
    }

    public Task<NpmPackument> GetPackumentAsync(string name, CancellationToken cancellationToken = default) =>
        _packuments.GetOrAdd(name, n => FetchPackumentAsync(n, cancellationToken));

    private async Task<NpmPackument> FetchPackumentAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_registryUrl}/{EncodeName(name)}");
        // The "corgi" document leaves out readmes and other fields an installer never needs.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new NitePackageException($"Package '{name}' was not found in {_registryUrl}.");
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<NpmPackument>(stream, JsonOptions, cancellationToken)
               ?? throw new NitePackageException($"The registry returned an empty document for '{name}'.");
    }

    /// <summary>Downloads a tarball (or reads it from the cache) and verifies its integrity.</summary>
    public async Task<byte[]> GetTarballAsync(NpmDist dist, CancellationToken cancellationToken = default)
    {
        var integrity = Integrity.Parse(dist.Integrity, dist.Shasum);
        var cachePath = Path.Combine(_cacheDirectory, integrity.CacheKey + ".tgz");
        if (File.Exists(cachePath))
        {
            var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            if (integrity.Matches(cached))
            {
                return cached;
            }
        }

        var bytes = await _http.GetByteArrayAsync(dist.Tarball, cancellationToken);
        if (!integrity.Matches(bytes))
        {
            throw new NitePackageException($"Integrity check failed for {dist.Tarball}.");
        }

        Directory.CreateDirectory(_cacheDirectory);
        var temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, cachePath, overwrite: true);
        return bytes;
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
