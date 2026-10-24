using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Configuration;

namespace Nite.Packages;

/// <summary>
/// Installs <c>nite.json</c> dependencies into a regular <c>node_modules</c> folder, so esbuild,
/// TypeScript and editors all find packages the usual way. Resolution is recorded in
/// <c>nite.lock.json</c>; with a current lockfile an install never asks the registry for metadata.
/// </summary>
/// <remarks>
/// Deliberately small: registry packages only (no git, file: or npm: aliases), optional
/// dependencies are skipped, install scripts never run, and hoisting is "top level unless that
/// would conflict, otherwise nested under the dependent".
/// </remarks>
public sealed class PackageInstaller
{
    public const string LockFileName = "nite.lock.json";
    private const string StateFileName = ".nite-state.json";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public PackageInstaller(HttpClient http, ILogger<PackageInstaller>? logger = null)
    {
        _http = http;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Makes <c>node_modules</c> match the lockfile, resolving a new lockfile if needed.</summary>
    /// <returns>The lockfile that is now installed.</returns>
    /// <param name="update">
    /// Packages to move to the newest versions their ranges allow (<c>nite update</c>); an empty
    /// collection means all of them. When null, versions already in the lockfile are kept wherever
    /// they still satisfy their ranges, and the lockfile is only re-resolved if nite.json changed.
    /// </param>
    /// <param name="seedVersions">
    /// Versions to prefer on top of the lockfile's, such as those an imported project's own
    /// lockfile (package-lock.json, yarn.lock…) recorded.
    /// </param>
    public async Task<NiteLockFile> InstallAsync(string projectDirectory, NiteConfig config, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? update = null, IReadOnlyDictionary<string, HashSet<string>>? seedVersions = null)
    {
        // The dev server and `nite install` may run at once; only one of them may touch node_modules.
        await using var installLock = await AcquireInstallLockAsync(projectDirectory, cancellationToken);

        var registry = new NpmRegistry(_http, NiteSettings.Load(projectDirectory).Registry, logger: _logger);
        var lockPath = Path.Combine(projectDirectory, LockFileName);
        var requires = new SortedDictionary<string, string>(config.AllDependencies.ToDictionary(), StringComparer.Ordinal);

        var lockFile = NiteLockFile.TryLoad(lockPath);
        if (update is not null || lockFile is null || !lockFile.Requires.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(requires))
        {
            if (requires.Count > 0)
            {
                _logger.LogInformation("Resolving {Count} package(s) from {Registry}", requires.Count, registry.Settings.DefaultRegistry.TrimEnd('/'));
            }

            // Versions to keep: everything already locked, minus whatever is being updated.
            var preferred = new Dictionary<string, List<SemVersion>>(StringComparer.Ordinal);
            foreach (var (name, versions) in seedVersions ?? new Dictionary<string, HashSet<string>>())
            {
                preferred[name] = versions.Select(v => SemVersion.TryParse(v, out var parsed) ? parsed : null).OfType<SemVersion>().ToList();
            }
            if (lockFile is not null && update is not { Count: 0 })
            {
                foreach (var (location, package) in lockFile.Packages)
                {
                    var name = PackageNameAt(location);
                    if (update?.Contains(name) == true || !SemVersion.TryParse(package.Version, out var version))
                    {
                        continue;
                    }
                    if (!preferred.TryGetValue(name, out var locked))
                    {
                        preferred[name] = locked = [];
                    }
                    locked.Add(version);
                }
            }

            lockFile = await ResolveAsync(registry, requires, preferred, cancellationToken);
            lockFile.Save(lockPath);
        }

        await MaterializeAsync(registry, projectDirectory, lockFile, cancellationToken);
        return lockFile;
    }

    private async Task<NiteLockFile> ResolveAsync(NpmRegistry registry, IReadOnlyDictionary<string, string> requires,
        IReadOnlyDictionary<string, List<SemVersion>> preferred, CancellationToken cancellationToken)
    {
        var placed = new SortedDictionary<string, NiteLockedPackage>(StringComparer.Ordinal);
        var queue = new Queue<(string Parent, string Name, string Range, bool IsPeer)>();
        foreach (var (name, range) in requires)
        {
            queue.Enqueue(("", name, range, false));
        }

        while (queue.Count > 0)
        {
            var (parent, name, rangeText, isPeer) = queue.Dequeue();
            var packument = await registry.GetPackumentAsync(name, cancellationToken);
            var range = ParseRange(name, rangeText, packument);

            var visible = VisibleLocations(parent, name).FirstOrDefault(placed.ContainsKey);
            if (visible is not null && range.IsSatisfiedBy(SemVersion.Parse(placed[visible].Version)))
            {
                continue;
            }

            if (visible is not null && isPeer)
            {
                _logger.LogWarning("{Parent} wants peer {Name}@{Range} but {Version} is installed",
                    PackageNameAt(parent), name, rangeText, placed[visible].Version);
                continue;
            }

            var rootLocation = "node_modules/" + name;
            if (parent.Length == 0 && placed.ContainsKey(rootLocation))
            {
                // A direct dependency was already placed (as someone's peer); the user's range wins.
                placed.Remove(rootLocation);
            }
            var location = placed.ContainsKey(rootLocation) ? $"{parent}/node_modules/{name}" : rootLocation;

            var versions = packument.Versions.Keys
                .Select(v => SemVersion.TryParse(v, out var parsed) ? parsed : null)
                .OfType<SemVersion>();
            var chosen = (preferred.TryGetValue(name, out var kept) ? range.MaxSatisfying(kept) : null)
                ?? range.MaxSatisfying(versions)
                ?? throw new NitePackageException($"No version of '{name}' satisfies '{rangeText}'.");
            var manifest = packument.Versions[chosen.ToString()];

            placed[location] = new NiteLockedPackage
            {
                Version = manifest.Version,
                Resolved = manifest.Dist.Tarball,
                Integrity = manifest.Dist.Integrity ?? "sha1-" + Convert.ToBase64String(Convert.FromHexString(manifest.Dist.Shasum ?? "")),
            };

            foreach (var (dependency, dependencyRange) in manifest.Dependencies ?? [])
            {
                if (manifest.OptionalDependencies?.ContainsKey(dependency) == true)
                {
                    continue;
                }
                queue.Enqueue((location, dependency, dependencyRange, false));
            }

            foreach (var (peer, peerRange) in manifest.PeerDependencies ?? [])
            {
                if (manifest.PeerDependenciesMeta?.GetValueOrDefault(peer)?.Optional == true)
                {
                    continue;
                }
                // Like npm 7+, missing peers are installed; they resolve from where the dependent sits.
                queue.Enqueue((location, peer, peerRange, true));
            }
        }

        return new NiteLockFile
        {
            Requires = new Dictionary<string, string>(requires),
            Packages = new Dictionary<string, NiteLockedPackage>(placed),
        };
    }

    private async Task MaterializeAsync(NpmRegistry registry, string projectDirectory, NiteLockFile lockFile, CancellationToken cancellationToken)
    {
        var nodeModules = Path.Combine(projectDirectory, "node_modules");
        var statePath = Path.Combine(nodeModules, StateFileName);
        var fingerprint = lockFile.Fingerprint();
        var previous = InstallState.TryLoad(statePath);
        if (previous?.Fingerprint == fingerprint && lockFile.Packages.Keys.All(p => File.Exists(Path.Combine(projectDirectory, p, "package.json"))))
        {
            return;
        }

        // Remove packages a previous nite install created that the lockfile no longer lists.
        foreach (var stale in previous?.Packages.Except(lockFile.Packages.Keys) ?? [])
        {
            DeleteDirectory(Path.Combine(projectDirectory, stale));
        }

        var installed = 0;
        // Parents before children, so replacing a parent folder never deletes a fresh nested install.
        var pending = lockFile.Packages.OrderBy(p => p.Key.Length).ToList();
        foreach (var level in pending.GroupBy(p => p.Key.Split("/node_modules/").Length).OrderBy(g => g.Key))
        {
            await Parallel.ForEachAsync(level, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (entry, token) =>
            {
                var (location, package) = entry;
                var directory = Path.Combine(projectDirectory, location);
                if (InstalledVersion(directory) == package.Version)
                {
                    return;
                }

                var tarball = await registry.GetTarballAsync(new NpmDist { Tarball = package.Resolved, Integrity = package.Integrity }, token);
                if (Directory.Exists(directory))
                {
                    // Keep nested node_modules; they are separate lockfile entries.
                    foreach (var child in Directory.EnumerateFileSystemEntries(directory).Where(c => Path.GetFileName(c) != "node_modules"))
                    {
                        if (Directory.Exists(child)) DeleteDirectory(child);
                        else File.Delete(child);
                    }
                }
                Tarball.Extract(tarball, directory);
                Interlocked.Increment(ref installed);
            });
        }

        if (installed > 0)
        {
            _logger.LogInformation("Installed {Count} package(s) into node_modules", installed);
        }

        Directory.CreateDirectory(nodeModules);
        new InstallState { Fingerprint = fingerprint, Packages = lockFile.Packages.Keys.ToList() }.Save(statePath);
    }

    private async Task<FileStream> AcquireInstallLockAsync(string projectDirectory, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(projectDirectory, ".nite");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "install.lock");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var waiting = false;

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (!waiting)
                {
                    waiting = true;
                    _logger.LogInformation("Waiting for another Nite install in {Directory} to finish", projectDirectory);
                }
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone, which is what we wanted.
        }
    }

    internal static SemVerRange ParseRange(string name, string rangeText, NpmPackument packument)
    {
        var text = rangeText.Trim();
        if (text.Length == 0 || text == "latest")
        {
            text = packument.DistTags.GetValueOrDefault("latest") ?? "*";
        }
        else if (packument.DistTags.TryGetValue(text, out var tagged))
        {
            text = tagged;
        }

        if (SemVerRange.TryParse(text, out var range))
        {
            return range;
        }

        // Alternatives can name dist-tags too (@tailwindcss/typography's peer range is
        // ">=3.0.0 || >=4.0.0 || insiders"): use the tagged version, or drop a tag that is not published.
        var alternatives = text.Split("||").Select(a => a.Trim())
            .Select(a => packument.DistTags.TryGetValue(a, out var version) ? version : a)
            .Where(a => SemVerRange.TryParse(a, out _))
            .ToList();
        return alternatives.Count > 0 && SemVerRange.TryParse(string.Join(" || ", alternatives), out range)
            ? range
            : throw new NitePackageException($"'{name}@{rangeText}' is not supported. Nite installs registry versions and ranges only.");
    }

    /// <summary>Where Node resolution would look for <paramref name="name"/> from <paramref name="parent"/>, nearest first.</summary>
    internal static IEnumerable<string> VisibleLocations(string parent, string name)
    {
        var segments = parent.Length == 0 ? [] : parent.Split("/node_modules/");
        for (var depth = segments.Length; depth >= 1; depth--)
        {
            yield return string.Join("/node_modules/", segments.Take(depth)) + "/node_modules/" + name;
        }
        yield return "node_modules/" + name;
    }

    private static string PackageNameAt(string location)
    {
        var index = location.LastIndexOf("node_modules/", StringComparison.Ordinal);
        return index < 0 ? "<root>" : location[(index + "node_modules/".Length)..];
    }

    private static string? InstalledVersion(string directory)
    {
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class InstallState
    {
        public string Fingerprint { get; init; } = "";
        public List<string> Packages { get; init; } = [];

        public static InstallState? TryLoad(string path)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), NiteConfig.JsonOptions) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, NiteConfig.JsonOptions));
    }
}

/// <summary><c>nite.lock.json</c>: the exact packages and where they sit in <c>node_modules</c>.</summary>
public sealed class NiteLockFile
{
    public int LockfileVersion { get; init; } = 1;
    public Dictionary<string, string> Requires { get; init; } = [];
    public Dictionary<string, NiteLockedPackage> Packages { get; init; } = [];

    public static NiteLockFile? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var lockFile = JsonSerializer.Deserialize<NiteLockFile>(File.ReadAllText(path), NiteConfig.JsonOptions);
            return lockFile?.LockfileVersion == 1 ? lockFile : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var ordered = new NiteLockFile
        {
            Requires = Requires.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(),
            Packages = Packages.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(ordered, NiteConfig.JsonOptions) + "\n");
    }

    /// <summary>A stable hash of the installed set, used for cache-busting dependency URLs.</summary>
    public string Fingerprint()
    {
        var builder = new StringBuilder();
        foreach (var (location, package) in Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(location).Append('@').Append(package.Version).Append(' ').Append(package.Integrity).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }
}

public sealed class NiteLockedPackage
{
    public string Version { get; init; } = "";
    public string Resolved { get; init; } = "";
    public string Integrity { get; init; } = "";
}
