using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nite;

/// <summary>
/// The contents of <c>nite.json</c>. Both the middleware and the <c>nite</c> CLI read it, so dev
/// and production builds agree on what gets compiled.
/// </summary>
public sealed class NiteConfig
{
    public const string FileName = "nite.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // These options write files (lockfile, manifest), never HTML, so "+" needn't become +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Folder (relative to the project) that holds client source. Default <c>Client</c>.</summary>
    public string ClientRoot { get; init; } = "Client";

    /// <summary>Folder under <see cref="ClientRoot"/> whose modules can be used as islands.</summary>
    public string Islands { get; init; } = "islands";

    /// <summary>
    /// Extra modules (relative to <see cref="ClientRoot"/>) that pages load with <c>&lt;nite-module&gt;</c>.
    /// Islands are entries automatically.
    /// </summary>
    public List<string> Entries { get; init; } = [];

    /// <summary>
    /// The island renderer: <c>react</c> (default) or a module under <see cref="ClientRoot"/> that
    /// exports <c>mount(element, component, props)</c> and optionally returns an unmount function.
    /// </summary>
    public string Renderer { get; init; } = "react";

    /// <summary>Package used for the automatic JSX runtime, such as <c>react</c> or <c>preact</c>.</summary>
    public string JsxImportSource { get; init; } = "react";

    /// <summary>esbuild target for production output.</summary>
    public string Target { get; init; } = "es2022";

    public string Registry { get; init; } = Packages.NpmRegistry.DefaultUrl;

    public Dictionary<string, string> Dependencies { get; init; } = [];

    /// <summary>Installed for editor tooling (such as <c>@types/react</c>) but never bundled.</summary>
    public Dictionary<string, string> DevDependencies { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> AllDependencies =>
        DevDependencies.Concat(Dependencies)
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.Last().Value);

    public static NiteConfig Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, FileName);
        if (!File.Exists(path))
        {
            return new NiteConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<NiteConfig>(File.ReadAllText(path), JsonOptions) ?? new NiteConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid: {ex.Message}", ex);
        }
    }
}
