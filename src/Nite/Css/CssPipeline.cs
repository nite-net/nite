using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nite.Esbuild;
using Nite.Observability;

namespace Nite.Css;

/// <summary>
/// What happens to a stylesheet before esbuild bundles it, in the order Vite does it: Sass or Less
/// by extension, then Tailwind (when the file uses it), then the project's PostCSS config. esbuild
/// does the rest (inlining <c>@import</c>, scoping CSS modules, url() assets, lowering, minifying),
/// both in the dev server and in production builds, so the two agree.
/// </summary>
public sealed class CssPipeline : IAsyncDisposable
{
    internal static readonly string[] StyleExtensions = [".css", ".scss", ".sass", ".less"];

    /// <summary>The esbuild filter for files this pipeline loads.</summary>
    internal const string StyleFilter = @"\.(css|scss|sass|less)$";

    private readonly NiteProject _project;
    private readonly ILogger _logger;
    private readonly ToolBundler _bundler;
    private readonly PostCssRunner _postcss;
    private readonly TailwindCompiler _tailwind;
    private readonly LessCompiler _less;
    private readonly SassCompiler _sass;
    private readonly ConcurrentDictionary<string, string> _virtualModules = new(StringComparer.Ordinal);

    public CssPipeline(NiteProject project, EsbuildService esbuild, HttpClient http, ILoggerFactory loggerFactory)
    {
        _project = project;
        _logger = loggerFactory.CreateLogger<CssPipeline>();
        _bundler = new ToolBundler(esbuild, project.Directory, project.WorkDirectory);
        _postcss = new PostCssRunner(_bundler, project.Directory, loggerFactory.CreateLogger<PostCssRunner>());
        _tailwind = new TailwindCompiler(_bundler, project, loggerFactory.CreateLogger<TailwindCompiler>());
        _less = new LessCompiler(_bundler, project, loggerFactory.CreateLogger<LessCompiler>());
        _sass = new SassCompiler(http, project, loggerFactory.CreateLogger<SassCompiler>());
    }

    internal TailwindCompiler Tailwind => _tailwind;

    public static bool IsStylesheet(string path) =>
        StyleExtensions.Contains(Path.GetExtension(path.Split('?', '#')[0]).ToLowerInvariant());

    public static bool IsCssModule(string path)
    {
        var name = Path.GetFileName(path.Split('?', '#')[0]);
        return IsStylesheet(name) && Path.GetFileNameWithoutExtension(name).EndsWith(".module", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs one file through the steps that apply to it.</summary>
    /// <param name="mode"><c>development</c> or <c>production</c>, as PostCSS configs see it.</param>
    /// <param name="parent">
    /// The span the tools' spans belong to. esbuild calls plugins from its reader loop, whose
    /// ambient span is whatever request first started esbuild, so plugins pass the build's span.
    /// </param>
    internal async Task<CssStepResult> ProcessAsync(string file, string mode, ActivityContext parent = default)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var dependencies = new List<string>();
        var options = _project.Config.Css;

        string css;
        switch (extension)
        {
            case ".scss" or ".sass":
                var sass = await RunToolAsync("sass", file, parent, () => _sass.CompileAsync(file, options.PreprocessorOptions.Scss));
                css = sass.Css;
                dependencies.AddRange(sass.Dependencies);
                break;
            case ".less":
                var less = await RunToolAsync("less", file, parent, () => _less.CompileAsync(file, options.PreprocessorOptions.Less));
                css = less.Css;
                dependencies.AddRange(less.Dependencies);
                break;
            default:
                css = await File.ReadAllTextAsync(file);
                break;
        }

        // Packages ship finished CSS; running the app's PostCSS over it is slow and pointless.
        if (IsInNodeModules(file))
        {
            return new CssStepResult(css, dependencies);
        }

        if (TailwindCompiler.Uses(css) && TailwindApplies(css))
        {
            var tailwind = await RunToolAsync("tailwind", file, parent, () => _tailwind.ProcessAsync(file, css));
            css = tailwind.Css;
            dependencies.AddRange(tailwind.Dependencies);
        }

        if (options.PostCss && PostCssRunner.FindConfig(_project) is { } config)
        {
            var postcss = await RunToolAsync("postcss", file, parent, () => _postcss.ProcessAsync(config, css, file, mode));
            css = postcss.Css;
            dependencies.Add(config);
            dependencies.AddRange(postcss.Dependencies);
        }
        return new CssStepResult(css, dependencies);
    }

    private async Task<T> RunToolAsync<T>(string tool, string file, ActivityContext parent, Func<Task<T>> run)
    {
        using var activity = NiteTelemetry.Source.StartActivity("nite.css." + tool, ActivityKind.Internal, parent);
        activity?.SetTag("nite.file", _project.RelativeToClientRoot(file));
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await run();
        }
        catch (Exception ex)
        {
            activity.Fail(ex);
            throw;
        }
        finally
        {
            NiteTelemetry.StylesheetToolDuration.Record(NiteTelemetry.SecondsSince(started), new KeyValuePair<string, object?>("nite.css.tool", tool));
        }
    }

    /// <summary>
    /// Loads the JavaScript tools a project's stylesheets will need and compiles its Tailwind
    /// stylesheets, so the first page load does not pay for it. Failures are left for the real
    /// request to report.
    /// </summary>
    internal async Task WarmUpAsync(IEnumerable<string> stylesheets)
    {
        foreach (var file in stylesheets)
        {
            try
            {
                if (Path.GetExtension(file) == ".css" && TailwindCompiler.Uses(await File.ReadAllTextAsync(file)))
                {
                    await ProcessAsync(file, Mode.Development);
                }
            }
            catch (Exception ex) when (ex is EsbuildPluginException or IOException or Jint.Runtime.JavaScriptException)
            {
                _logger.LogDebug("Warming up {File} failed: {Message}", file, ex.Message);
            }
        }
        if (_project.Config.Css.PostCss && PostCssRunner.FindConfig(_project) is { } config)
        {
            try
            {
                await _postcss.ProcessAsync(config, "", Path.Combine(_project.ClientRoot, "warm-up.css"), Mode.Development);
            }
            catch (Exception ex) when (ex is EsbuildPluginException or IOException or Jint.Runtime.JavaScriptException)
            {
                _logger.LogDebug("Loading PostCSS failed: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Tailwind directives such as <c>@apply</c> also appear in non-Tailwind CSS (and Sass), so
    /// they only count when Tailwind is installed or the file imports it outright.
    /// </summary>
    private bool TailwindApplies(string css) =>
        File.Exists(Path.Combine(_project.Directory, "node_modules", "tailwindcss", "package.json"))
        || css.Contains("tailwindcss", StringComparison.Ordinal);

    private static bool IsInNodeModules(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// The esbuild plugin that puts stylesheets through <see cref="ProcessAsync"/>.
    /// </summary>
    /// <param name="mode">Passed to PostCSS configs.</param>
    /// <param name="onProcessed">Told about every file processed and what went into it (for hot updates).</param>
    /// <param name="rewriteUrl">
    /// In development, maps a url() asset file to the URL the dev server serves it at, so assets
    /// are never copied. In production it is null and esbuild's file loader hashes and copies them.
    /// </param>
    /// <param name="uniqueModuleNames">
    /// Give each CSS module a name derived from its path, so modules built separately (as the dev
    /// server does) never produce the same class name for two different files.
    /// </param>
    internal EsbuildPlugin CreatePlugin(string mode, Action<string, CssStepResult>? onProcessed = null,
        Func<string, string?>? rewriteUrl = null, bool uniqueModuleNames = false)
    {
        var plugin = new EsbuildPlugin { Name = "nite-css" };
        var parent = Activity.Current?.Context ?? default;

        // Every path in a dev stylesheet build comes through here (those builds are small); in a
        // production build, which also bundles all the JavaScript, only the few that need it.
        var filter = rewriteUrl is null ? "^(/|https?:|//|data:|#|~)" : ".*";
        plugin.OnResolve.Add(new EsbuildOnResolve(filter, "", args => Task.FromResult(ResolveReference(args, rewriteUrl))));
        if (uniqueModuleNames)
        {
            plugin.OnResolve.Add(new EsbuildOnResolve(@"\.module\.(css|scss|sass|less)$", "", args =>
            {
                var path = args.Path;
                if (!path.StartsWith('.') && !Path.IsPathRooted(path))
                {
                    return Task.FromResult<EsbuildResolveResult?>(null);
                }
                var real = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(args.ResolveDir, path));
                return Task.FromResult<EsbuildResolveResult?>(new EsbuildResolveResult { Path = VirtualModulePath(real) });
            }));
        }

        plugin.OnLoad.Add(new EsbuildOnLoad(StyleFilter, "file", async args =>
        {
            var file = _virtualModules.TryGetValue(args.Path, out var real) ? real : args.Path;
            var result = await ProcessAsync(file, mode, parent);
            onProcessed?.Invoke(file, result);
            return new EsbuildLoadResult
            {
                Contents = result.Css,
                Loader = IsCssModule(file) ? "local-css" : "css",
                ResolveDir = Path.GetDirectoryName(file),
                WatchFiles = result.Dependencies,
            };
        }));
        return plugin;
    }

    /// <summary>
    /// url() references and @import rules: remote, data and site-root URLs stay as written; in
    /// development, asset files become dev server URLs; <c>~package/file</c> means node_modules.
    /// </summary>
    private static EsbuildResolveResult? ResolveReference(EsbuildResolveArgs args, Func<string, string?>? rewriteUrl)
    {
        var path = args.Path;
        if (args.Kind is not ("url-token" or "import-rule"))
        {
            return null;
        }

        if (path.StartsWith("data:", StringComparison.Ordinal) || path.StartsWith('#') || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith("http:", StringComparison.Ordinal) || path.StartsWith("https:", StringComparison.Ordinal)
            || (path.StartsWith('/') && !File.Exists(path)))
        {
            return new EsbuildResolveResult { Path = path, External = true };
        }

        var bare = path.Split('?', '#')[0];
        var suffix = path[bare.Length..];
        string? file = bare.StartsWith('~') ? ResolvePackageFile(bare[1..], args.ResolveDir) : null;
        if (rewriteUrl is null)
        {
            // Production: let esbuild's file loader copy it; only ~ needs help finding it.
            return file is null ? null : new EsbuildResolveResult { Path = file };
        }
        if (args.Kind == "import-rule")
        {
            return file is null ? null : new EsbuildResolveResult { Path = file };
        }

        if (file is null && args.ResolveDir.Length > 0 && !bare.StartsWith('~'))
        {
            file = Path.GetFullPath(Path.Combine(args.ResolveDir, Uri.UnescapeDataString(bare)));
        }
        // An asset that is missing (or outside what the dev server serves) is left to the browser;
        // a 404 there is clearer than a stylesheet that fails to build.
        var url = file is not null && File.Exists(file) ? rewriteUrl(file) : null;
        return new EsbuildResolveResult { Path = url is null ? path : url + suffix, External = true };
    }

    /// <summary><c>Button.module.css</c> → <c>Button.k3x9z1.module.css</c> in the same folder; the hash comes from the full path.</summary>
    internal string VirtualModulePath(string real)
    {
        var name = Path.GetFileName(real);
        var index = name.LastIndexOf(".module.", StringComparison.OrdinalIgnoreCase);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_project.RelativeToClientRoot(real))))[..6];
        var virtualPath = Path.Combine(Path.GetDirectoryName(real)!, $"{name[..index]}.{hash}{name[index..]}");
        _virtualModules[virtualPath] = real;
        return virtualPath;
    }

    private static string? ResolvePackageFile(string specifier, string fromDirectory)
    {
        var (package, subpath) = CssResolver.Split(specifier);
        for (var directory = fromDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, "node_modules", package, subpath);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _postcss.Dispose();
        _tailwind.Dispose();
        _less.Dispose();
        await _sass.DisposeAsync();
    }
}
