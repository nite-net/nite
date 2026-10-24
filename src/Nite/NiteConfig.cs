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
    /// Extra entries relative to <see cref="ClientRoot"/>: modules that pages load with
    /// <c>&lt;nite-module&gt;</c>, or HTML pages served with <c>MapNiteSpa</c>. Islands and
    /// <c>index.html</c> (when it exists) are entries automatically.
    /// </summary>
    public List<string> Entries { get; init; } = [];

    /// <summary>
    /// The island renderer: <c>react</c> (default) or a module under <see cref="ClientRoot"/> that
    /// exports <c>mount(element, component, props)</c> and optionally returns an unmount function.
    /// </summary>
    public string Renderer { get; init; } = "react";

    /// <summary>Package used for the automatic JSX runtime, such as <c>react</c> or <c>preact</c>.</summary>
    public string JsxImportSource { get; init; } = "react";

    /// <summary>
    /// Environment variables with these prefixes (from .env files or the environment) are inlined
    /// into client code as <c>import.meta.env.KEY</c> and <c>process.env.KEY</c>. Others never
    /// reach the browser. Imported Vite projects use <c>VITE_</c>, Create React App ones <c>REACT_APP_</c>.
    /// </summary>
    public List<string> EnvPrefix { get; init; } = ["NITE_PUBLIC_"];

    /// <summary>Allow JSX in <c>.js</c> files, as Create React App does. Off by default, as in Vite.</summary>
    public bool JsxInJs { get; init; }

    /// <summary>
    /// React Fast Refresh in development: component edits update in place and keep state. Needs
    /// <c>react-refresh</c>, which Nite installs as a dev dependency when React is a dependency.
    /// </summary>
    public bool FastRefresh { get; init; } = true;

    /// <summary>
    /// esbuild target for production output. When it names only a JavaScript version, Nite adds
    /// the browsers Vite targets by default, so CSS gets lowered for them too.
    /// </summary>
    public string Target { get; init; } = "es2022";

    /// <summary>
    /// Registry for this project's unscoped packages. Usually better set in .npmrc or the user
    /// config (see <see cref="Configuration.NiteSettings"/>); this wins over both.
    /// </summary>
    public string? Registry { get; init; }

    /// <summary>Stylesheet options, named as in Vite's <c>css</c> config.</summary>
    public NiteCssConfig Css { get; init; } = new();

    public Dictionary<string, string> Dependencies { get; init; } = [];

    /// <summary>Installed for editor tooling (such as <c>@types/react</c>) but never bundled.</summary>
    public Dictionary<string, string> DevDependencies { get; init; } = [];

    /// <summary>Everything to install: dependencies, dev dependencies and packages Nite needs itself.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> AllDependencies
    {
        get
        {
            var all = DevDependencies.Concat(Dependencies)
                .GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Last().Value);
            if (UsesFastRefresh)
            {
                all.TryAdd("react-refresh", ReactRefreshRange);
            }
            return all;
        }
    }

    internal const string ReactRefreshRange = "^0.19.0";

    [JsonIgnore]
    internal bool UsesFastRefresh => FastRefresh && JsxImportSource == "react" && Dependencies.ContainsKey("react");

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

/// <summary>The <c>css</c> section of <c>nite.json</c>.</summary>
public sealed class NiteCssConfig
{
    /// <summary>
    /// Run the project's PostCSS config (postcss.config.*) on stylesheets. On by default whenever
    /// a config exists; packages' own stylesheets in node_modules are never run through it.
    /// </summary>
    public bool PostCss { get; init; } = true;

    /// <summary>Options for the Sass (<c>scss</c>, also used for <c>sass</c>) and Less compilers.</summary>
    public NitePreprocessorOptions PreprocessorOptions { get; init; } = new();
}

public sealed class NitePreprocessorOptions
{
    public SassOptions Scss { get; init; } = new();
    public LessOptions Less { get; init; } = new();
}

public sealed class SassOptions
{
    /// <summary>
    /// Code added to the start of every Sass file, such as <c>@use "mantine" as mantine;</c>.
    /// The client root is a load path, so files there can be used by name.
    /// </summary>
    public string? AdditionalData { get; init; }

    /// <summary>Extra folders (relative to the project) to resolve <c>@use</c> and <c>@import</c> from.</summary>
    public List<string> LoadPaths { get; init; } = [];

    /// <summary>Deprecation warnings to keep quiet, such as <c>import</c> or <c>global-builtin</c>.</summary>
    public List<string> SilenceDeprecations { get; init; } = [];
}

public sealed class LessOptions
{
    /// <summary>Code added to the start of every Less file.</summary>
    public string? AdditionalData { get; init; }

    /// <summary>Extra folders (relative to the project) to resolve <c>@import</c> from.</summary>
    public List<string> Paths { get; init; } = [];

    public bool JavascriptEnabled { get; init; }
    public Dictionary<string, string> GlobalVars { get; init; } = [];
    public Dictionary<string, string> ModifyVars { get; init; } = [];
}
