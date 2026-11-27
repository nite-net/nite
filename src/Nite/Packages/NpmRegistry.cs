using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Configuration;
using Nite.Observability;

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
    private readonly bool _fullMetadata;

    /// <param name="cacheDirectory">The cache root; defaults to the user cache directory.</param>
    /// <param name="fullMetadata">
    /// Ask for the full package document instead of the abbreviated one. It is two to three times
    /// larger, but it is the only one with <see cref="NpmPackument.Time"/>, which a minimum release
    /// age needs.
    /// </param>
    public NpmRegistry(HttpClient http, RegistrySettings? settings = null, string? cacheDirectory = null, ILogger? logger = null, bool fullMetadata = false)
    {
        _http = http;
        _settings = settings ?? RegistrySettings.Default;
        _cacheDirectory = cacheDirectory ?? NiteDirectories.Current.Cache;
        _logger = logger ?? NullLogger.Instance;
        _fullMetadata = fullMetadata;
    }

    public RegistrySettings Settings => _settings;

    /// <summary>Whether packuments carry publish times (<see cref="NpmPackument.Time"/>).</summary>
    public bool FullMetadata => _fullMetadata;

    public Task<NpmPackument> GetPackumentAsync(string name, CancellationToken cancellationToken = default) =>
        _packuments.GetOrAdd(name, n => FetchPackumentAsync(n, cancellationToken));

    private async Task<NpmPackument> FetchPackumentAsync(string name, CancellationToken cancellationToken)
    {
        var registry = _settings.RegistryFor(name);
        var url = new Uri(registry + EncodeName(name));
        var (bodyPath, etagPath) = MetadataPaths(registry, name);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!_fullMetadata)
        {
            // The "corgi" document leaves out readmes and other fields an installer never needs.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.npm.install-v1+json"));
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", _fullMetadata ? 1.0 : 0.8));
        // Package documents compress well (the full one for @types/node is over 10 MB as is).
        // Asked for here rather than on the handler, so tarballs are never decoded.
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
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

        var body = await ReadBodyAsync(response, cancellationToken);
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
                NiteTelemetry.Tarballs.Add(1, new KeyValuePair<string, object?>("nite.cache", "hit"));
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
        NiteTelemetry.Tarballs.Add(1, new KeyValuePair<string, object?>("nite.cache", "miss"));
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
        var file = Path.Combine(_cacheDirectory, "metadata", registryKey, EncodeName(name)) + (_fullMetadata ? ".full" : "");
        return (file + ".json", file + ".etag");
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
        {
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var body = new MemoryStream();
        await gzip.CopyToAsync(body, cancellationToken);
        return body.ToArray();
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

    /// <summary>
    /// When each version was published, plus <c>created</c> and <c>modified</c>. Only the full
    /// document has it (see <see cref="NpmRegistry.FullMetadata"/>); some private registries leave
    /// it out there too.
    /// </summary>
    public Dictionary<string, JsonElement>? Time { get; init; }

    /// <summary>When <paramref name="version"/> was published, if the registry says.</summary>
    public DateTimeOffset? PublishedAt(string version) =>
        Time?.GetValueOrDefault(version) is { ValueKind: JsonValueKind.String } value && value.TryGetDateTimeOffset(out var published)
            ? published
            : null;
}

public sealed class NpmVersion : IJsonOnDeserialized
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public Dictionary<string, string>? Dependencies { get; init; }
    public Dictionary<string, string>? OptionalDependencies { get; init; }
    public Dictionary<string, string>? PeerDependencies { get; init; }
    public Dictionary<string, NpmPeerMeta>? PeerDependenciesMeta { get; init; }

    /// <summary>
    /// Set by the registry when the package has a preinstall, install or postinstall script (or a
    /// binding.gyp, which implies one). Nite never runs them. The full document has no such flag,
    /// so there it is worked out from <see cref="Scripts"/> and <see cref="Gypfile"/>.
    /// </summary>
    public bool HasInstallScript { get; set; }

    /// <summary>The package's scripts; only in the full document.</summary>
    public Dictionary<string, JsonElement>? Scripts { get; init; }

    /// <summary>Operating systems the package runs on, npm style (<c>linux</c>, <c>!win32</c>).</summary>
    [JsonConverter(typeof(StringListConverter))]
    public List<string>? Os { get; init; }

    /// <summary>Processors the package runs on (<c>x64</c>, <c>arm64</c>).</summary>
    [JsonConverter(typeof(StringListConverter))]
    public List<string>? Cpu { get; init; }

    /// <summary>C libraries the package runs with (<c>glibc</c>, <c>musl</c>); only in the full document.</summary>
    [JsonConverter(typeof(StringListConverter))]
    public List<string>? Libc { get; init; }

    /// <summary>Whether the package builds a native addon with node-gyp; only in the full document.</summary>
    public JsonElement? Gypfile { get; init; }

    public NpmDist Dist { get; init; } = new();

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Scripts is { } scripts && (scripts.ContainsKey("preinstall") || scripts.ContainsKey("install") || scripts.ContainsKey("postinstall"))
            || Gypfile is { ValueKind: JsonValueKind.True })
        {
            HasInstallScript = true;
        }
    }
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

/// <summary>
/// Reads a list of strings that old manifests sometimes give as one string (<c>"os": "darwin"</c>),
/// and ignores anything else rather than failing the whole document.
/// </summary>
internal sealed class StringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return [reader.GetString()!];
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String) list.Add(reader.GetString()!);
                    else reader.Skip();
                }
                return list;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
