using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nite.Configuration;

/// <summary>
/// Effective settings, merged from (highest first):
/// <list type="number">
/// <item><c>NITE_*</c> environment variables (<c>NITE_REGISTRY</c>)</item>
/// <item>the project's <c>nite.json</c> (<c>registry</c>)</item>
/// <item>the project's <c>.npmrc</c></item>
/// <item>the user's <c>config.json</c> in the config directory</item>
/// <item>the user's <c>~/.npmrc</c> (or <c>$NPM_CONFIG_USERCONFIG</c>)</item>
/// <item>built-in defaults</item>
/// </list>
/// Credentials only come from .npmrc files, so tokens stay where npm users already keep them.
/// </summary>
public sealed partial class NiteSettings
{
    public const string RegistryKey = "registry";
    public const string InitTemplateKey = "init.template";

    private readonly Dictionary<string, SettingValue> _values;

    private NiteSettings(Dictionary<string, SettingValue> values, RegistrySettings registry)
    {
        _values = values;
        Registry = registry;
    }

    public RegistrySettings Registry { get; }

    /// <summary>Every effective value with where it came from; credentials are marked secret.</summary>
    public IEnumerable<SettingValue> Values => _values.Values.OrderBy(v => v.Key, StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value.Value : null;

    public SettingValue? GetWithOrigin(string key) => _values.GetValueOrDefault(key);

    /// <summary>Keys <c>nite config set</c> accepts: <c>registry</c>, <c>@scope:registry</c> and <c>init.template</c>.</summary>
    public static bool IsKnownKey(string key) =>
        key is RegistryKey or InitTemplateKey || ScopeRegistryKey().IsMatch(key);

    /// <summary>Loads settings for a project (or just the user's, when <paramref name="projectDirectory"/> is null).</summary>
    public static NiteSettings Load(string? projectDirectory = null, NiteDirectories? directories = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        directories ??= NiteDirectories.Current;
        var values = new Dictionary<string, SettingValue>(StringComparer.Ordinal);
        var npmrcValues = new Dictionary<string, string>(StringComparer.Ordinal);

        void Set(string key, string value, string origin, bool secret = false) =>
            values[key] = new SettingValue(key, value, origin, secret);

        void ApplyNpmrc(string path)
        {
            foreach (var (key, value) in Npmrc.Load(path, environment))
            {
                npmrcValues[key] = value;
                if (key == RegistryKey || ScopeRegistryKey().IsMatch(key))
                {
                    Set(key, value, path);
                }
                else if (key.StartsWith("//", StringComparison.Ordinal))
                {
                    Set(key, value, path, secret: true);
                }
            }
        }

        // Lowest precedence first; later layers overwrite.
        Set(RegistryKey, RegistrySettings.NpmRegistry, "default");
        Set(InitTemplateKey, "spa", "default");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ApplyNpmrc(environment("NPM_CONFIG_USERCONFIG") is { Length: > 0 } userNpmrc ? userNpmrc : Path.Combine(home, ".npmrc"));

        foreach (var (key, value) in ReadUserConfig(directories.ConfigFile))
        {
            Set(key, value, directories.ConfigFile);
        }

        if (projectDirectory is not null)
        {
            ApplyNpmrc(Path.Combine(projectDirectory, ".npmrc"));
            var niteJson = Path.Combine(projectDirectory, NiteConfig.FileName);
            if (File.Exists(niteJson) && NiteConfig.Load(projectDirectory).Registry is { Length: > 0 } projectRegistry)
            {
                Set(RegistryKey, projectRegistry, niteJson);
            }
        }

        if (environment("NITE_REGISTRY") is { Length: > 0 } envRegistry)
        {
            Set(RegistryKey, envRegistry, "NITE_REGISTRY");
        }

        var scopes = values.Values
            .Where(v => ScopeRegistryKey().IsMatch(v.Key))
            .ToDictionary(v => v.Key[..v.Key.IndexOf(':')], v => v.Value, StringComparer.Ordinal);
        var registry = new RegistrySettings(values[RegistryKey].Value, scopes, RegistryCredential.From(npmrcValues));
        return new NiteSettings(values, registry);
    }

    /// <summary>Sets a key in the user's config.json (creating it if needed).</summary>
    public static void SetUserValue(string key, string value, NiteDirectories? directories = null)
    {
        if (!IsKnownKey(key))
        {
            throw new InvalidOperationException(UnknownKeyMessage(key));
        }
        var path = (directories ?? NiteDirectories.Current).ConfigFile;
        var values = ReadUserConfig(path);
        values[key] = value;
        WriteUserConfig(path, values);
    }

    /// <returns>Whether the key was set.</returns>
    public static bool UnsetUserValue(string key, NiteDirectories? directories = null)
    {
        var path = (directories ?? NiteDirectories.Current).ConfigFile;
        var values = ReadUserConfig(path);
        if (!values.Remove(key))
        {
            return false;
        }
        WriteUserConfig(path, values);
        return true;
    }

    public static string UnknownKeyMessage(string key) =>
        key.StartsWith("//", StringComparison.Ordinal) || key.Contains("_auth", StringComparison.Ordinal) || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            ? $"'{key}' is a credential. Put it in ~/.npmrc (or the project's .npmrc), where npm keeps it too."
            : $"Unknown setting '{key}'. Known: registry, @scope:registry, init.template.";

    private static SortedDictionary<string, string> ReadUserConfig(string path)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && IsKnownKey(property.Name))
                {
                    values[property.Name] = property.Value.GetString()!;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
        }
        return values;
    }

    private static void WriteUserConfig(string path, IReadOnlyDictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(values, NiteConfig.JsonOptions) + "\n");
    }

    [GeneratedRegex(@"^@[a-z0-9][a-z0-9._~-]*:registry$")]
    private static partial Regex ScopeRegistryKey();
}

/// <summary>A setting's effective value and where it came from (a file path, an environment variable, or "default").</summary>
public sealed record SettingValue(string Key, string Value, string Origin, bool Secret);
