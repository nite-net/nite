using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Nite.Assets;
using Nite.Configuration;
using Nite.Css;
using Nite.Esbuild;
using Nite.Html;
using Nite.Observability;
using Nite.Packages;
using Nite.Runtime;

namespace Nite.Dev;

/// <summary>
/// The development server behind <c>/_nite</c>. Source files are compiled on request (one esbuild
/// transform each, cached until the file changes), bare imports resolve through an import map to
/// pre-bundled dependencies, and a file watcher drives hot updates over a WebSocket: React
/// components refresh in place, CSS swaps in place, and anything else reloads the page.
/// </summary>
public sealed partial class NiteDevServer : INiteAssets, IAsyncDisposable
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HmrHub _hub = new();
    private readonly ConcurrentDictionary<string, CachedModule> _modules = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _bareImports = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingChanges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _warnedModules = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _changeTimer;
    private readonly Timer _reoptimizeTimer;
    private readonly Timer _configTimer;

    // The module graph, keyed by full path. Only modules a browser has loaded take part in updates.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _importsOf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _importers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _served = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _timestamps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _failing = new(StringComparer.Ordinal);

    // Stylesheets as built for the browser, keyed by file path (or "npm:<specifier>" for package CSS).
    private readonly ConcurrentDictionary<string, BuiltStylesheet> _stylesheets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _stylesheetErrors = new(StringComparer.Ordinal);

    private NiteProject _project;
    private EsbuildService? _esbuild;
    private CssPipeline? _css;
    private DependencyOptimizer? _optimizer;
    private DependencyBundle _dependencies = new("", new Dictionary<string, string>());
    private string _lockFingerprint = "";
    private Task _ready = Task.CompletedTask;
    private volatile bool _initialized;
    private volatile bool _refreshEnabled;
    private int _started;
    private FileSystemWatcher? _sourceWatcher;
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _envWatcher;
    private List<FileSystemWatcher>? _projectWatchers;
    private readonly Timer _envTimer;
    private volatile IReadOnlyList<string> _envDefines = [];
    private volatile TsconfigPaths _aliases = new();

    public NiteDevServer(NiteProject project, HttpClient http, ILoggerFactory loggerFactory)
    {
        _project = project;
        _http = http;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NiteDevServer>();
        _changeTimer = new Timer(_ => _ = FlushChangesAsync());
        _reoptimizeTimer = new Timer(_ => _ = ReoptimizeAsync());
        _configTimer = new Timer(_ => _ = ReloadConfigAsync());
        _envTimer = new Timer(_ => _ = ReloadEnvironmentAsync());
    }

    public bool IsDevelopment => true;

    /// <summary>For <see cref="NiteHealthCheck"/>: started, busy, failed or ready.</summary>
    internal HealthCheckResult CheckHealth()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            return HealthCheckResult.Unhealthy("The Nite dev server has not started. Call app.UseNite().");
        }

        var ready = _ready;
        if (ready.IsFaulted)
        {
            var error = ready.Exception!.GetBaseException();
            return HealthCheckResult.Unhealthy($"Nite failed: {error.Message}", error);
        }
        if (!ready.IsCompleted)
        {
            return HealthCheckResult.Degraded("Nite is installing packages or pre-bundling dependencies.");
        }
        return HealthCheckResult.Healthy("The Nite dev server is ready.", new Dictionary<string, object>
        {
            ["modules"] = _modules.Count,
            ["dependencies"] = _dependencies.Imports.Count,
        });
    }

    /// <summary>Starts installing, scanning and pre-bundling in the background. Safe to call more than once.</summary>
    /// <param name="hostStarted">
    /// Completes when the host has started. Startup waits for it, because telemetry listeners
    /// (OpenTelemetry's) subscribe as the host starts and would miss the startup trace otherwise.
    /// </param>
    public void Start(Task? hostStarted = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ready = RunExclusiveAsync(async () =>
        {
            if (hostStarted is not null)
            {
                // Bounded, in case the host never reports that it started.
                await Task.WhenAny(hostStarted, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            await NiteTelemetry.TraceAsync("nite.dev.startup", InitializeAsync, root: true);
        });
        StartWatching();
        NiteHotReload.Updated += OnServerHotReload;
    }

    private void OnServerHotReload(Type[]? types)
    {
        var names = types?.Select(t => t.FullName).Where(n => n is not null).Distinct().ToList() ?? [];
        _logger.LogDebug("C# hot reload applied; notifying {Count} browser(s)", _hub.ClientCount);
        _ = _hub.BroadcastAsync(new { type = "server-update", types = names });
    }

    private async Task InitializeAsync()
    {
        var binary = await EsbuildBinary.EnsureAsync(_http, NiteSettings.Load(_project.Directory).Registry);
        _esbuild = new EsbuildService(binary, _loggerFactory.CreateLogger<EsbuildService>());
        _optimizer = new DependencyOptimizer(_esbuild, _loggerFactory.CreateLogger<DependencyOptimizer>());
        _css = new CssPipeline(_project, _esbuild, _http, _loggerFactory);

        await InstallAsync();
        LoadProjectInputs();

        // Compile everything once up front so the first page load already has every dependency
        // pre-bundled, instead of discovering them one reload at a time.
        if (Directory.Exists(_project.ClientRoot))
        {
            var files = Directory.EnumerateFiles(_project.ClientRoot, "*", SearchOption.AllDirectories)
                .Where(f => NiteProject.ModuleExtensions.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (file, _) =>
            {
                try
                {
                    await CompileAsync(file);
                }
                catch (EsbuildException)
                {
                    // Reported when the browser asks for the file.
                }
            });
        }

        // Stylesheet tools run in an interpreter and take a moment to load; do it alongside bundling.
        var stylesheets = Directory.Exists(_project.ClientRoot)
            ? Directory.EnumerateFiles(_project.ClientRoot, "*", SearchOption.AllDirectories)
                .Where(f => CssPipeline.IsStylesheet(f) && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList()
            : [];
        var warmUp = _css.WarmUpAsync(stylesheets);
        await OptimizeAsync();
        await warmUp;
        _initialized = true;
        _logger.LogInformation("Nite dev server ready for {ClientRoot}{Refresh}", _project.ClientRoot, _refreshEnabled ? " (React Fast Refresh on)" : "");
    }

    private async Task InstallAsync()
    {
        var installer = new PackageInstaller(_http, _loggerFactory.CreateLogger<PackageInstaller>());
        var lockFile = await installer.InstallAsync(_project.Directory, _project.Config);
        _lockFingerprint = lockFile.Fingerprint();

        var refresh = _project.Config.UsesFastRefresh && IsInstalled(ReactRefresh.RuntimeSpecifier);
        if (refresh != _refreshEnabled)
        {
            _refreshEnabled = refresh;
            _modules.Clear(); // compiled code carries (or lacks) refresh registration
        }
    }

    private async Task OptimizeAsync()
    {
        var specifiers = _bareImports.Keys.Where(IsInstalled).ToList();
        if (_project.UsesReactRenderer)
        {
            specifiers.AddRange(RuntimeSources.ReactRendererImports.Where(IsInstalled));
        }
        var jsxRuntime = _project.Config.JsxImportSource + "/jsx-dev-runtime";
        if (IsInstalled(jsxRuntime))
        {
            specifiers.Add(jsxRuntime);
        }
        if (_refreshEnabled)
        {
            specifiers.Add(ReactRefresh.RuntimeSpecifier);
        }

        _dependencies = await _optimizer!.OptimizeAsync(_project, specifiers, _lockFingerprint, CancellationToken.None);
    }

    /// <summary>Handles a request under <c>/_nite</c> (the prefix is already in PathBase).</summary>
    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        NameRequest(context, path);
        switch (path)
        {
            case "/ws":
                await _hub.AcceptAsync(context);
                return;
            case "/client.js":
                await WriteAsync(context, RuntimeSources.DevClient, "text/javascript");
                return;
            case "/refresh.js":
                await WriteAsync(context, RuntimeSources.RefreshRuntime, "text/javascript");
                return;
            case "/runtime/islands.js":
                await WriteAsync(context, RuntimeSources.IslandLoader, "text/javascript");
                return;
            case "/runtime/renderer.js":
                await WriteAsync(context, RuntimeSources.RendererForDev(_project), "text/javascript");
                return;
            case "/runtime/config.js":
                await WriteAsync(context, RuntimeSources.ClientConfig, "text/javascript");
                return;
        }

        try
        {
            await _ready;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nite failed to start");
            await WriteErrorModuleAsync(context, new ErrorPayload($"Nite failed to start: {ex.Message}"));
            return;
        }

        if (path.StartsWith("/deps/", StringComparison.Ordinal))
        {
            await ServeDependencyAsync(context, path["/deps/".Length..]);
        }
        else if (path.StartsWith("/src/", StringComparison.Ordinal))
        {
            await ServeSourceAsync(context, Uri.UnescapeDataString(path["/src/".Length..]));
        }
        else if (path.StartsWith("/npm/", StringComparison.Ordinal))
        {
            await ServePackageFileAsync(context, Uri.UnescapeDataString(path["/npm/".Length..]));
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task ServeDependencyAsync(HttpContext context, string name)
    {
        var directory = Path.Combine(_project.WorkDirectory, "deps");
        var file = Path.GetFullPath(Path.Combine(directory, name));
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Entry URLs carry ?v=<hash> and chunk names carry a content hash, so both are immutable.
        context.Response.Headers.CacheControl = "max-age=31536000, immutable";
        await WriteAsync(context, await File.ReadAllBytesAsync(file), ContentType(file), cacheControl: null);
    }

    private async Task ServeSourceAsync(HttpContext context, string relativePath)
    {
        var requested = _project.ResolveClientPath(relativePath);
        if (requested is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!File.Exists(requested))
        {
            // Hand-written URLs may leave off the extension; send them to the one canonical URL
            // so the browser never instantiates the same module twice.
            if (ResolveModuleFile(requested) is { } resolved)
            {
                context.Response.Redirect($"{context.Request.PathBase}/src/{_project.RelativeToClientRoot(resolved)}{context.Request.QueryString}");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            return;
        }

        var extension = Path.GetExtension(requested).ToLowerInvariant();
        var asModule = context.Request.Query.ContainsKey("import");

        if (NiteProject.ModuleExtensions.Contains(extension) || (extension == ".json" && asModule))
        {
            _served.TryAdd(requested, 0);
            try
            {
                var module = await CompileAsync(requested);
                if (module.MissingPackages.Count > 0)
                {
                    await WriteErrorModuleAsync(context, new ErrorPayload(
                        $"Package{(module.MissingPackages.Count > 1 ? "s" : "")} {string.Join(", ", module.MissingPackages.Select(p => $"'{p}'"))} " +
                        $"{(module.MissingPackages.Count > 1 ? "are" : "is")} not installed. Add {(module.MissingPackages.Count > 1 ? "them" : "it")} to \"dependencies\" in nite.json.",
                        _project.RelativeToClientRoot(requested)));
                    return;
                }
                var code = ImportScanner.Rewrite(module.Code, specifier => ServedSpecifier(requested, specifier));
                await WriteAsync(context, code, "text/javascript");
            }
            catch (EsbuildException ex)
            {
                _logger.LogWarning("{Message}", ex.Message);
                await WriteErrorModuleAsync(context, ErrorPayload.From(ex, _project.RelativeToClientRoot(requested)));
            }
        }
        else if (CssPipeline.IsStylesheet(requested))
        {
            _served.TryAdd(requested, 0);
            await ServeStylesheetAsync(context, requested, requested, _project.RelativeToClientRoot(requested), asModule);
        }
        else if (asModule)
        {
            // import logo from "./logo.svg" gives the asset's URL, as in Vite.
            var url = $"{context.Request.PathBase}/src/{_project.RelativeToClientRoot(requested)}";
            await WriteAsync(context, $"export default {JsonSerializer.Serialize(url)};\n", "text/javascript");
        }
        else
        {
            await WriteAsync(context, await File.ReadAllBytesAsync(requested), ContentType(requested));
        }
    }

    /// <summary>
    /// Files from node_modules: package stylesheets imported from code (<c>@mantine/core/styles.css</c>,
    /// built like any other stylesheet) and the fonts and images their url()s point at.
    /// </summary>
    private async Task ServePackageFileAsync(HttpContext context, string specifier)
    {
        var nodeModules = Path.Combine(_project.Directory, "node_modules");
        if (CssPipeline.IsStylesheet(specifier) && (context.Request.Query.ContainsKey("import") || !File.Exists(Path.Combine(nodeModules, specifier))))
        {
            await ServeStylesheetAsync(context, "npm:" + specifier, specifier, "npm:" + specifier, context.Request.Query.ContainsKey("import"));
            return;
        }

        var file = Path.GetFullPath(Path.Combine(nodeModules, specifier));
        if (!file.StartsWith(nodeModules + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await WriteAsync(context, await File.ReadAllBytesAsync(file), ContentType(file));
    }

    /// <param name="key">The stylesheet's cache key: its path, or npm:specifier.</param>
    /// <param name="entry">What esbuild builds: the file, or the package specifier.</param>
    /// <param name="id">The name the browser knows it by (style tags and hot updates).</param>
    private async Task ServeStylesheetAsync(HttpContext context, string key, string entry, string id, bool asModule)
    {
        BuiltStylesheet sheet;
        try
        {
            sheet = await BuildStylesheetAsync(key, entry, context.Request.PathBase + NiteProject.BasePath);
        }
        catch (EsbuildException ex)
        {
            _logger.LogWarning("{Message}", ex.Message);
            var error = ErrorPayload.From(ex, ErrorFile(ex, id));
            _stylesheetErrors[key] = error.File ?? id;
            TrackFailedStylesheet(key, entry, context.Request.PathBase + NiteProject.BasePath, ex);
            if (asModule)
            {
                await WriteErrorModuleAsync(context, error);
            }
            else
            {
                // A <link>ed stylesheet cannot run code; the overlay comes over the socket instead.
                _ = _hub.BroadcastAsync(new { type = "error", error.Message, error.File, error.Line, error.Column, error.LineText });
                await WriteAsync(context, $"/* {error.Message.Replace("*/", "* /")} */\n", "text/css");
            }
            return;
        }

        if (!asModule)
        {
            await WriteAsync(context, sheet.Css, "text/css");
            return;
        }

        var code = new StringBuilder();
        code.Append($$"""
            const id = {{JsonSerializer.Serialize(id)}};
            const css = {{JsonSerializer.Serialize(sheet.Css)}};
            let style = document.querySelector(`style[data-nite-css="${CSS.escape(id)}"]`);
            if (!style) {
              style = document.createElement("style");
              style.dataset.niteCss = id;
              document.head.appendChild(style);
            }
            style.textContent = css;

            """);
        if (sheet.Classes is { } classes)
        {
            // CSS modules: the class map, plus a named export for each name that can be one.
            code.Append($"const classes = {JsonSerializer.Serialize(classes)};\nexport default classes;\n");
            foreach (var name in classes.Keys.Where(IsExportableName))
            {
                code.Append($"export const {name} = classes[{JsonSerializer.Serialize(name)}];\n");
            }
        }
        else
        {
            code.Append("export default css;\n");
        }
        await WriteAsync(context, code.ToString(), "text/javascript");
    }

    /// <summary>
    /// Builds a stylesheet for the browser with esbuild and the CSS pipeline: imports inlined,
    /// Sass/Less/Tailwind/PostCSS applied, url() assets pointed at the dev server. Cached until a
    /// file that went into it changes.
    /// </summary>
    private async Task<BuiltStylesheet> BuildStylesheetAsync(string key, string entry, string basePath)
    {
        if (_stylesheets.TryGetValue(key, out var cached) && cached.BasePath == basePath && cached.IsCurrent())
        {
            CountCompile("stylesheet", hit: true);
            return cached;
        }
        return await TraceCompileAsync("stylesheet", entry, () => BuildStylesheetUncachedAsync(key, entry, basePath));
    }

    private async Task<BuiltStylesheet> BuildStylesheetUncachedAsync(string key, string entry, string basePath)
    {
        var files = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var processed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var isModule = CssPipeline.IsCssModule(entry);
        var plugin = _css!.CreatePlugin(NiteEnvironment.Development,
            onProcessed: (file, result) =>
            {
                files[file] = 0;
                processed[file] = 0;
                foreach (var dependency in result.Dependencies)
                {
                    files[dependency] = 0;
                }
            },
            rewriteUrl: file => AssetUrl(file, basePath),
            uniqueModuleNames: true);

        var outDirectory = Path.Combine(_project.WorkDirectory, "css");
        var entryPath = entry;
        if (isModule)
        {
            // A CSS module's class map only exists on the JavaScript side, so build it through an importer.
            Directory.CreateDirectory(outDirectory);
            entryPath = Path.Combine(outDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry)))[..16] + ".js");
            await File.WriteAllTextAsync(entryPath, $"export {{ default }} from {JsonSerializer.Serialize(NitePaths.ToUrlPath(entry))};\n");
        }

        var result = await _esbuild!.BuildAsync(new EsbuildBuildOptions
        {
            WorkingDirectory = _project.Directory,
            Entries = [new EsbuildEntry(entryPath)],
            Flags = ["--bundle", "--format=esm", $"--outdir={outDirectory}", "--conditions=style,development"],
            Write = false,
            Plugins = [plugin],
        });

        var css = result.OutputFiles.FirstOrDefault(f => f.Path.EndsWith(".css", StringComparison.Ordinal)) is { } cssFile
            ? Encoding.UTF8.GetString(cssFile.Contents)
            : "";
        IReadOnlyDictionary<string, string>? classes = null;
        if (isModule && result.OutputFiles.FirstOrDefault(f => f.Path.EndsWith(".js", StringComparison.Ordinal)) is { } jsFile)
        {
            classes = ParseClassMap(Encoding.UTF8.GetString(jsFile.Contents));
        }

        var stamps = files.Keys.Where(File.Exists).ToDictionary(f => f, Stamp, StringComparer.Ordinal);
        var built = new BuiltStylesheet(key, basePath, css, classes, stamps) { Processed = processed.Keys.ToHashSet(StringComparer.Ordinal) };
        _stylesheets[key] = built;
        if (_css.Tailwind.StylesheetsWithUtilities.Count > 0)
        {
            WatchProjectForTailwind();
        }
        return built;
    }

    /// <summary>
    /// Keeps a stylesheet that failed to build in the cache, marked stale, so that saving the fix
    /// (in it, or in the partial the error was in) finds it and updates the page.
    /// </summary>
    private void TrackFailedStylesheet(string key, string entry, string basePath, EsbuildException ex, BuiltStylesheet? previous = null)
    {
        var files = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        foreach (var file in previous?.Files.Keys ?? [])
        {
            files[file] = (0, 0);
        }
        if (File.Exists(entry))
        {
            files[Path.GetFullPath(entry)] = (0, 0);
        }
        foreach (var error in ex.Errors)
        {
            if (error.File is { } file && Path.GetFullPath(Path.Combine(_project.Directory, file)) is var full && File.Exists(full))
            {
                files[full] = (0, 0);
            }
        }
        _stylesheets[key] = new BuiltStylesheet(key, basePath, previous?.Css ?? "", previous?.Classes, files);
    }

    /// <summary>Where the dev server serves an asset a stylesheet references.</summary>
    private string? AssetUrl(string file, string basePath)
    {
        if (_project.IsInsideClientRoot(file))
        {
            return $"{basePath}/src/{_project.RelativeToClientRoot(file)}";
        }
        var nodeModules = Path.Combine(_project.Directory, "node_modules") + Path.DirectorySeparatorChar;
        return file.StartsWith(nodeModules, StringComparison.Ordinal)
            ? $"{basePath}/npm/{NitePaths.ToUrlPath(Path.GetRelativePath(nodeModules, file))}"
            : null;
    }

    /// <summary>The class map from esbuild's output for a CSS module (an object literal of string values).</summary>
    internal static IReadOnlyDictionary<string, string> ParseClassMap(string code)
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ClassMapEntry().Matches(code))
        {
            var name = match.Groups["quoted"].Success ? JsonSerializer.Deserialize<string>($"\"{match.Groups["quoted"].Value}\"")! : match.Groups["name"].Value;
            classes[name] = JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"")!;
        }
        return classes;
    }

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "default", "break", "case", "catch", "class", "const", "continue", "debugger", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "static", "implements", "interface", "package",
        "private", "protected", "public", "await", "classes", "css", "id", "style",
    };

    private static bool IsExportableName(string name) => IdentifierName().IsMatch(name) && !ReservedWords.Contains(name);

    private string ErrorFile(EsbuildException ex, string fallback) =>
        ex.Errors.FirstOrDefault()?.File is { } file && Path.GetFullPath(Path.Combine(_project.Directory, file)) is var full && _project.IsInsideClientRoot(full)
            ? _project.RelativeToClientRoot(full)
            : fallback;

    private static (long, long) Stamp(string file)
    {
        var info = new FileInfo(file);
        return (info.LastWriteTimeUtc.Ticks, info.Length);
    }

    [GeneratedRegex("""^\s+(?:"(?<quoted>(?:[^"\\]|\\.)*)"|(?<name>[A-Za-z_$][\w$]*)):\s*"(?<value>(?:[^"\\]|\\.)*)",?\s*$""", RegexOptions.Multiline)]
    private static partial Regex ClassMapEntry();

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*$")]
    private static partial Regex IdentifierName();

    /// <summary>
    /// Compiles a module and records its imports in the module graph. The cached code still has
    /// the specifiers esbuild printed; <see cref="ServedSpecifier"/> rewrites them per request so
    /// hot-update timestamps are always current.
    /// </summary>
    private async Task<CachedModule> CompileAsync(string file)
    {
        var info = new FileInfo(file);
        var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
        if (_modules.TryGetValue(file, out var cached) && cached.Stamp == stamp)
        {
            CountCompile("module", hit: true);
            return cached;
        }
        return await TraceCompileAsync("module", file, () => CompileUncachedAsync(file, stamp));
    }

    private async Task<CachedModule> CompileUncachedAsync(string file, (long Ticks, long Length) stamp)
    {
        var source = await File.ReadAllTextAsync(file);
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var loader = extension switch
        {
            ".tsx" => "tsx",
            ".ts" or ".mts" => "ts",
            ".jsx" => "jsx",
            ".json" => "json",
            ".js" when _project.Config.JsxInJs => "jsx",
            _ => "js",
        };

        var result = await _esbuild!.TransformAsync(source,
        [
            $"--loader={loader}", "--format=esm", "--target=es2022", "--sourcemap=inline",
            $"--sourcefile={Path.GetFileName(file)}", "--jsx=automatic", "--jsx-dev",
            $"--jsx-import-source={_project.Config.JsxImportSource}",
            "--define:process.env.NODE_ENV=\"development\"",
            .. _envDefines,
        ]);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var imports = new List<string>();
        var discovered = false;
        foreach (var specifier in ImportScanner.FindSpecifiers(result.Code))
        {
            if (specifier == RuntimeSources.ClientConfigSpecifier)
            {
                // Nite's own module, mapped in the import map; not a package.
                continue;
            }
            if (ResolveImport(file, specifier) is { } target)
            {
                imports.Add(target);
                continue;
            }
            if (!ImportScanner.IsBare(specifier))
            {
                continue;
            }

            if (CssPipeline.IsStylesheet(specifier))
            {
                // Package CSS (@mantine/core/styles.css) is served as a stylesheet, not pre-bundled.
                if (!IsInstalled(specifier))
                {
                    missing.Add(ImportScanner.PackageName(specifier));
                }
                continue;
            }

            // Remembered even when missing, so adding the package to nite.json bundles it right away.
            var isNew = _bareImports.TryAdd(specifier, 0);
            if (!IsInstalled(specifier))
            {
                missing.Add(ImportScanner.PackageName(specifier));
            }
            else if (isNew && !_dependencies.Imports.ContainsKey(specifier))
            {
                discovered = true;
            }
        }

        if (discovered && _initialized)
        {
            _logger.LogInformation("New dependency imported by {File}; re-bundling dependencies", _project.RelativeToClientRoot(file));
            _reoptimizeTimer.Change(100, Timeout.Infinite);
        }

        var code = result.Code;
        var refresh = RefreshInfo.None;
        if (_refreshEnabled && loader is "tsx" or "jsx")
        {
            refresh = ReactRefresh.Analyze(code);
            if (refresh.Components.Count > 0)
            {
                // A relative URL, so it resolves to /_nite/refresh.js under any PathBase.
                var relative = _project.RelativeToClientRoot(file);
                var runtimeUrl = string.Concat(Enumerable.Repeat("../", relative.Count(c => c == '/') + 1)) + "refresh.js";
                code = ReactRefresh.Append(code, ReactRefresh.Footer(refresh, relative, runtimeUrl));
            }
        }

        UpdateGraph(file, imports);
        var module = new CachedModule(stamp, code, missing.ToList(), refresh.IsBoundary);
        _modules[file] = module;
        return module;
    }

    /// <summary>
    /// Names the request's span after its route, as routing does for endpoints. Requests under
    /// /_nite never reach routing, so traces would otherwise list each of them as just "GET".
    /// </summary>
    private static void NameRequest(HttpContext context, string path)
    {
        if (context.Features.Get<IHttpActivityFeature>()?.Activity is not { IsAllDataRequested: true } activity)
        {
            return;
        }

        var route = NiteProject.BasePath + path switch
        {
            _ when path.StartsWith("/src/", StringComparison.Ordinal) => "/src/{**path}",
            _ when path.StartsWith("/deps/", StringComparison.Ordinal) => "/deps/{file}",
            _ when path.StartsWith("/npm/", StringComparison.Ordinal) => "/npm/{**specifier}",
            "/ws" or "/client.js" or "/refresh.js" or "/runtime/islands.js" or "/runtime/renderer.js" or "/runtime/config.js" => path,
            _ => "/{**path}",
        };
        activity.DisplayName = $"{context.Request.Method} {route}";
        activity.SetTag("http.route", route);
    }

    private static void CountCompile(string kind, bool hit) =>
        NiteTelemetry.Compiles.Add(1, new KeyValuePair<string, object?>("nite.kind", kind), new KeyValuePair<string, object?>("nite.cache", hit ? "hit" : "miss"));

    /// <summary>A cache miss: one span (a child of the request that needed it) and one duration measurement.</summary>
    private async Task<T> TraceCompileAsync<T>(string kind, string file, Func<Task<T>> compile)
    {
        CountCompile(kind, hit: false);
        using var activity = NiteTelemetry.Source.StartActivity(kind == "module" ? "nite.dev.compile" : "nite.dev.stylesheet");
        activity?.SetTag("nite.file", _project.RelativeToClientRoot(file));
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await compile();
        }
        catch (Exception ex)
        {
            activity.Fail(ex);
            throw;
        }
        finally
        {
            NiteTelemetry.CompileDuration.Record(NiteTelemetry.SecondsSince(started), new KeyValuePair<string, object?>("nite.kind", kind));
        }
    }

    private void UpdateGraph(string file, IReadOnlyList<string> imports)
    {
        if (_importsOf.TryGetValue(file, out var previous))
        {
            foreach (var target in previous.Except(imports))
            {
                if (_importers.TryGetValue(target, out var set))
                {
                    set.TryRemove(file, out _);
                }
            }
        }
        foreach (var target in imports)
        {
            _importers.GetOrAdd(target, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[file] = 0;
        }
        _importsOf[file] = imports;
    }

    private IReadOnlyList<string> LoadedImporters(string file) =>
        _importers.TryGetValue(file, out var set) ? set.Keys.Where(_served.ContainsKey).ToList() : [];

    /// <summary>
    /// The specifier as served: relative imports point at the exact file (<c>./Button</c> →
    /// <c>./Button.tsx</c>), non-JS imports get <c>?import</c> so the server answers with a module,
    /// and hot-updated files get <c>t=</c> so importers re-run fetch the new version.
    /// </summary>
    private string? ServedSpecifier(string importer, string specifier)
    {
        if (ResolveImport(importer, specifier) is not { } target)
        {
            if (ImportScanner.IsBare(specifier) && CssPipeline.IsStylesheet(specifier))
            {
                // /_nite/src/a/b.tsx → ../../npm/<specifier>, relative so any PathBase works.
                var depth = _project.RelativeToClientRoot(importer).Count(c => c == '/') + 1;
                var key = "npm:" + specifier;
                var stamp = _timestamps.TryGetValue(key, out var t) ? $"&t={t}" : "";
                return string.Concat(Enumerable.Repeat("../", depth)) + "npm/" + specifier + "?import" + stamp;
            }
            return null;
        }

        var directory = Path.GetDirectoryName(importer)!;
        var relative = NitePaths.ToUrlPath(Path.GetRelativePath(directory, target));
        if (!relative.StartsWith("../", StringComparison.Ordinal))
        {
            relative = "./" + relative;
        }

        var query = new List<string>();
        if (!NiteProject.ModuleExtensions.Contains(Path.GetExtension(target).ToLowerInvariant()))
        {
            query.Add("import");
        }
        if (_timestamps.TryGetValue(target, out var timestamp))
        {
            query.Add($"t={timestamp}");
        }
        return query.Count == 0 ? relative : relative + "?" + string.Join('&', query);
    }

    /// <summary>A relative import, or a tsconfig path alias (<c>@/components/x</c>), resolved to a client file.</summary>
    private string? ResolveImport(string importer, string specifier)
    {
        var pathPart = specifier.Split('?', '#')[0];
        var target = specifier.StartsWith('.')
            ? ResolveModuleFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importer)!, pathPart)))
            : ImportScanner.IsBare(specifier) ? _aliases.Resolve(pathPart) : null;
        return target is not null && _project.IsInsideClientRoot(target) ? target : null;
    }

    /// <summary>Reads what compiled code depends on besides the source itself: path aliases and public env vars.</summary>
    private void LoadProjectInputs()
    {
        _aliases = TsconfigPaths.Load(_project.ClientRoot);
        _envDefines = NiteEnvironment.DefineFlags(_project, NiteEnvironment.Development);
    }

    private async Task ReloadEnvironmentAsync()
    {
        _logger.LogInformation("Environment files changed; recompiling");
        LoadProjectInputs();
        _modules.Clear();
        await _hub.BroadcastAsync(new { type = "reload" });
    }

    /// <summary>Node-style file resolution: exact file, added extension, TS for .js, then index files.</summary>
    internal static string? ResolveModuleFile(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }
        foreach (var extension in NiteProject.ModuleExtensions)
        {
            if (File.Exists(path + extension))
            {
                return path + extension;
            }
        }

        // TypeScript's NodeNext convention: import "./x.js" means x.ts.
        var swap = Path.GetExtension(path) switch
        {
            ".js" => new[] { ".ts", ".tsx" },
            ".jsx" => [".tsx"],
            ".mjs" => [".mts"],
            _ => [],
        };
        foreach (var extension in swap)
        {
            var candidate = Path.ChangeExtension(path, extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (Directory.Exists(path))
        {
            foreach (var extension in NiteProject.ModuleExtensions)
            {
                var index = Path.Combine(path, "index" + extension);
                if (File.Exists(index))
                {
                    return index;
                }
            }
        }
        return null;
    }

    private bool IsInstalled(string specifier) =>
        File.Exists(Path.Combine(_project.Directory, "node_modules", ImportScanner.PackageName(specifier), "package.json"));

    private void StartWatching()
    {
        if (Directory.Exists(_project.ClientRoot))
        {
            _sourceWatcher = new FileSystemWatcher(_project.ClientRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            // Creates and renames count as changes: editors that save through a temp file and a
            // rename would otherwise never get a hot update.
            _sourceWatcher.Changed += (_, e) => OnSourceChanged(e.FullPath);
            _sourceWatcher.Created += (_, e) => OnSourceChanged(e.FullPath);
            _sourceWatcher.Deleted += (_, e) => OnSourceChanged(e.FullPath);
            _sourceWatcher.Renamed += (_, e) =>
            {
                OnSourceChanged(e.OldFullPath);
                OnSourceChanged(e.FullPath);
            };
            _sourceWatcher.EnableRaisingEvents = true;
        }
        else
        {
            _logger.LogWarning("Client root {ClientRoot} does not exist; file watching is off until restart", _project.ClientRoot);
        }

        _configWatcher = new FileSystemWatcher(_project.Directory, NiteConfig.FileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onConfig = (_, _) => _configTimer.Change(200, Timeout.Infinite);
        _configWatcher.Changed += onConfig;
        _configWatcher.Created += onConfig;
        _configWatcher.Renamed += (_, _) => _configTimer.Change(200, Timeout.Infinite);
        _configWatcher.EnableRaisingEvents = true;

        _envWatcher = new FileSystemWatcher(_project.Directory, ".env*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onEnv = (_, e) =>
        {
            if (NiteEnvironment.IsEnvFile(Path.GetFileName(e.FullPath)))
            {
                _envTimer.Change(150, Timeout.Infinite);
            }
        };
        _envWatcher.Changed += onEnv;
        _envWatcher.Created += onEnv;
        _envWatcher.Deleted += onEnv;
        _envWatcher.Renamed += (sender, e) => onEnv(sender, e);
        _envWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Tailwind finds classes anywhere in the project (Razor views, too), not just the client root,
    /// so once a stylesheet uses it, watch the rest of the project. One watcher per top-level
    /// folder keeps node_modules, bin and obj (thousands of folders) out of the watch list.
    /// </summary>
    private void WatchProjectForTailwind()
    {
        if (_projectWatchers is not null)
        {
            return;
        }
        lock (_stylesheets)
        {
            if (_projectWatchers is not null)
            {
                return;
            }
            var watchers = new List<FileSystemWatcher>();
            void OnFile(string path)
            {
                if (_css is not null && !_project.IsInsideClientRoot(path)
                    && TailwindScanner.IsScannable(Path.GetRelativePath(_project.Directory, path)) && _css.Tailwind.CoversFile(path))
                {
                    OnSourceChanged(path);
                }
            }
            FileSystemWatcher Watch(string directory, bool recursive)
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => OnFile(e.FullPath);
                watcher.Created += (_, e) => OnFile(e.FullPath);
                watcher.Renamed += (_, e) => OnFile(e.FullPath);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }

            bool ShouldWatch(string directory) =>
                directory != _project.ClientRoot && !_project.ClientRoot.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && TailwindScanner.IsScannable(Path.GetRelativePath(_project.Directory, directory) + Path.DirectorySeparatorChar + "x")
                && Path.GetFileName(directory) != "wwwroot";

            var root = Watch(_project.Directory, recursive: false);
            root.NotifyFilter |= NotifyFilters.DirectoryName;
            root.Created += (_, e) =>
            {
                // A new top-level folder (Pages/, say): watch it, and scan what is already in it.
                if (!Directory.Exists(e.FullPath) || !ShouldWatch(e.FullPath))
                {
                    return;
                }
                lock (watchers)
                {
                    watchers.Add(Watch(e.FullPath, recursive: true));
                }
                foreach (var file in Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories))
                {
                    OnFile(file);
                }
            };
            watchers.Add(root);
            foreach (var directory in Directory.EnumerateDirectories(_project.Directory).Where(ShouldWatch))
            {
                watchers.Add(Watch(directory, recursive: true));
            }
            _projectWatchers = watchers;
        }
    }

    private void OnSourceChanged(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.EndsWith('~') || name.EndsWith(".swp", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return;
        }

        _pendingChanges[path] = 0;
        // Editors often write a file in several steps; wait for them to settle.
        _changeTimer.Change(60, Timeout.Infinite);
    }

    private async Task FlushChangesAsync()
    {
        await _flushLock.WaitAsync();
        Activity? activity = null;
        try
        {
            var changed = _pendingChanges.Keys.ToList();
            foreach (var path in changed)
            {
                _pendingChanges.TryRemove(path, out _);
            }
            if (changed.Count == 0 || !_initialized)
            {
                return;
            }

            // A root span: file changes come from the watcher, and the timer that got us here
            // carries whatever request was current when it was created.
            Activity.Current = null;
            activity = NiteTelemetry.Source.StartActivity("nite.dev.hot_update");
            activity?.SetTag("nite.files.changed", changed.Count);

            if (changed.Any(p => Path.GetFileName(p).StartsWith("tsconfig", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal)))
            {
                // Path aliases may have moved; every compiled module's imports could resolve differently.
                LoadProjectInputs();
                _modules.Clear();
                CountUpdate(activity, "reload");
                await _hub.BroadcastAsync(new { type = "reload" });
                return;
            }

            var plan = await PlanUpdateAsync(changed);
            if (plan.Reload)
            {
                _logger.LogDebug("Reloading after changes to {Files}", string.Join(", ", changed.Select(_project.RelativeToClientRoot)));
                CountUpdate(activity, "reload");
                await _hub.BroadcastAsync(new { type = "reload" });
                return;
            }

            foreach (var error in plan.Errors)
            {
                CountUpdate(activity, "error");
                await _hub.BroadcastAsync(new { type = "error", error.Message, error.File, error.Line, error.Column, error.LineText });
            }
            foreach (var key in plan.Stylesheets)
            {
                CountUpdate(activity, "css");
                if (key.StartsWith("npm:", StringComparison.Ordinal))
                {
                    await _hub.BroadcastAsync(new { type = "css", path = key, url = $"npm/{key[4..]}?import" });
                    continue;
                }
                var relative = _project.RelativeToClientRoot(key);
                await _hub.BroadcastAsync(new { type = "css", path = relative, url = $"src/{relative}?import" });
            }
            if (plan.Boundaries.Count == 0 && plan.Cleared.Count > 0)
            {
                // A fix that changed nothing the page uses still has to clear its overlay.
                await _hub.BroadcastAsync(new { type = "update", updates = Array.Empty<object>(), cleared = plan.Cleared });
            }
            if (plan.Boundaries.Count > 0)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var file in plan.Touched)
                {
                    _timestamps[file] = timestamp;
                }
                var updates = plan.Boundaries.Select(file => new { path = _project.RelativeToClientRoot(file), url = $"src/{_project.RelativeToClientRoot(file)}", timestamp });
                _logger.LogDebug("Hot updating {Modules}", string.Join(", ", plan.Boundaries.Select(_project.RelativeToClientRoot)));
                CountUpdate(activity, "update");
                activity?.SetTag("nite.update.modules", plan.Boundaries.Count);
                await _hub.BroadcastAsync(new { type = "update", updates, cleared = plan.Cleared });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling file changes failed; reloading");
            activity.Fail(ex);
            CountUpdate(activity, "reload");
            await _hub.BroadcastAsync(new { type = "reload" });
        }
        finally
        {
            activity?.Dispose();
            _flushLock.Release();
        }
    }

    /// <summary>Counts one browser update; the span keeps the most decisive kind (a reload beats the rest).</summary>
    private static void CountUpdate(Activity? activity, string kind)
    {
        NiteTelemetry.HotUpdates.Add(1, new KeyValuePair<string, object?>("nite.update.kind", kind));
        if (activity is not null && activity.GetTagItem("nite.update.kind") is not "reload")
        {
            activity.SetTag("nite.update.kind", kind);
        }
    }

    /// <summary>
    /// Decides how the browser should take a set of file changes. For each changed module, walk up
    /// the importers the browser has actually loaded until reaching modules whose exports are all
    /// React components (refresh boundaries). If some path reaches a page's entry module first,
    /// nothing can accept the update in place and the page reloads, as in Vite.
    /// </summary>
    private async Task<UpdatePlan> PlanUpdateAsync(IReadOnlyList<string> changed)
    {
        var plan = new UpdatePlan();
        await PlanStylesheetUpdatesAsync(changed, plan);
        foreach (var path in changed.Distinct())
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var isModule = NiteProject.ModuleExtensions.Contains(extension) || extension == ".json";
            var inUse = _served.ContainsKey(path) || LoadedImporters(path).Count > 0;

            if (!File.Exists(path))
            {
                // Deleted (or a directory event). Anything that imported it is now broken.
                _modules.TryRemove(path, out _);
                if (inUse)
                {
                    plan.Reload = true;
                }
                continue;
            }

            if (CssPipeline.IsStylesheet(path))
            {
                continue; // handled with the stylesheets that use it
            }
            if (extension == ".html")
            {
                if (_project.IsInsideClientRoot(path))
                {
                    plan.Reload = true;
                }
                continue;
            }

            _modules.TryRemove(path, out _);
            if (!inUse)
            {
                continue; // nothing on any open page uses it
            }
            if (!isModule || !_refreshEnabled)
            {
                plan.Reload = true;
                continue;
            }

            try
            {
                await CompileAsync(path);
            }
            catch (EsbuildException ex)
            {
                // Show the error without reloading; the next good save hot-updates again.
                _failing[path] = 0;
                plan.Errors.Add(ErrorPayload.From(ex, _project.RelativeToClientRoot(path)));
                continue;
            }
            if (_failing.TryRemove(path, out _))
            {
                plan.Cleared.Add(_project.RelativeToClientRoot(path));
            }

            if (!await PropagateAsync(path, plan))
            {
                plan.Reload = true;
            }
        }
        return plan;
    }

    /// <summary>
    /// Stylesheets affected by a set of changes: those built from a changed file (an import, a
    /// Sass partial, the PostCSS config), and those using Tailwind when a source file gained
    /// classes. Each is rebuilt now so errors show at once; plain ones swap in place, CSS modules
    /// update the components that import them.
    /// </summary>
    private async Task PlanStylesheetUpdatesAsync(IReadOnlyList<string> changed, UpdatePlan plan)
    {
        if (_css is null || _stylesheets.IsEmpty)
        {
            return;
        }

        var changedSet = changed.ToHashSet(StringComparer.Ordinal);
        var affected = _stylesheets.Values.Where(s => s.Files.Keys.Any(changedSet.Contains)).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var tailwindSources = changed.Where(f => File.Exists(f) && !CssPipeline.IsStylesheet(f) && _css.Tailwind.CoversFile(f)).ToList();
        if (tailwindSources.Count > 0 && _css.Tailwind.Scanner.ScanFiles(tailwindSources))
        {
            var withUtilities = _css.Tailwind.StylesheetsWithUtilities.ToHashSet(StringComparer.Ordinal);
            // Only stylesheets that themselves emit utilities; one that just @references them does not change.
            affected.UnionWith(_stylesheets.Values.Where(s => s.Processed.Any(withUtilities.Contains)).Select(s => s.Key));
        }

        foreach (var key in affected)
        {
            if (!_stylesheets.TryRemove(key, out var previous))
            {
                continue;
            }
            var entry = key.StartsWith("npm:", StringComparison.Ordinal) ? key[4..] : key;
            var id = key.StartsWith("npm:", StringComparison.Ordinal) ? key : _project.RelativeToClientRoot(key);
            if (!File.Exists(entry) && !key.StartsWith("npm:", StringComparison.Ordinal))
            {
                plan.Reload |= _served.ContainsKey(key) || LoadedImporters(key).Count > 0;
                continue;
            }

            try
            {
                await BuildStylesheetAsync(key, entry, previous.BasePath);
            }
            catch (EsbuildException ex)
            {
                var error = ErrorPayload.From(ex, ErrorFile(ex, id));
                _stylesheetErrors[key] = error.File ?? id;
                plan.Errors.Add(error);
                TrackFailedStylesheet(key, entry, previous.BasePath, ex, previous);
                continue;
            }
            if (_stylesheetErrors.TryRemove(key, out var clearedFile))
            {
                plan.Cleared.Add(clearedFile);
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _timestamps[key] = timestamp;
            if (CssPipeline.IsCssModule(entry))
            {
                if (!_refreshEnabled || !await PropagateAsync(key, plan))
                {
                    plan.Reload = true;
                }
            }
            else
            {
                plan.Stylesheets.Add(key);
            }
        }
    }

    private async Task<bool> PropagateAsync(string file, UpdatePlan plan)
    {
        if (!plan.Touched.Add(file))
        {
            return true;
        }

        if (CssPipeline.IsStylesheet(file))
        {
            // A CSS module's class map changed; the components importing it re-run.
            foreach (var importer in LoadedImporters(file))
            {
                if (!await PropagateAsync(importer, plan))
                {
                    return false;
                }
            }
            return true;
        }

        CachedModule module;
        try
        {
            module = await CompileAsync(file);
        }
        catch (EsbuildException)
        {
            return false;
        }

        if (module.IsBoundary)
        {
            plan.Boundaries.Add(file);
            return true;
        }

        var importers = LoadedImporters(file);
        if (importers.Count == 0)
        {
            // An entry the page loaded directly (main.tsx, a non-component island) or nothing at all.
            return !_served.ContainsKey(file);
        }

        foreach (var importer in importers)
        {
            if (!await PropagateAsync(importer, plan))
            {
                return false;
            }
        }
        return true;
    }

    private async Task ReoptimizeAsync()
    {
        // Timer callbacks carry the span that was current when the timer was made; start fresh.
        Activity.Current = null;
        _ready = RunExclusiveAsync(OptimizeAsync);
        try
        {
            await _ready;
            await _hub.BroadcastAsync(new { type = "reload" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-bundling dependencies failed");
        }
    }

    private async Task ReloadConfigAsync()
    {
        Activity.Current = null;
        NiteProject updated;
        try
        {
            updated = new NiteProject(_project.Directory);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError("{Message}", ex.Message);
            return;
        }

        if (updated.ClientRoot != _project.ClientRoot)
        {
            _logger.LogWarning("clientRoot changed in nite.json; restart the app to pick it up");
            return;
        }

        _logger.LogInformation("nite.json changed; updating packages");
        _project = updated;
        _modules.Clear();
        _stylesheets.Clear();
        if (_esbuild is not null)
        {
            var previousCss = _css;
            _css = new CssPipeline(_project, _esbuild, _http, _loggerFactory);
            if (previousCss is not null)
            {
                await previousCss.DisposeAsync();
            }
        }
        _ready = RunExclusiveAsync(async () =>
        {
            if (_esbuild is null)
            {
                // Startup failed (often a typo in nite.json); this is the retry.
                await InitializeAsync();
                return;
            }
            await InstallAsync();
            await OptimizeAsync();
        });

        try
        {
            await _ready;
            await _hub.BroadcastAsync(new { type = "reload" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updating packages failed");
        }
    }

    private async Task RunExclusiveAsync(Func<Task> work)
    {
        await _pipelineLock.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    public ValueTask<NiteHead> GetHeadAsync(HttpContext context) => GetHeadAsync(context, includeIslands: true);

    private async ValueTask<NiteHead> GetHeadAsync(HttpContext context, bool includeIslands)
    {
        try
        {
            await _ready;
        }
        catch (Exception)
        {
            // The failure is reported by the module requests; still render the client so the
            // overlay can show it.
        }

        var basePath = context.Request.PathBase + NiteProject.BasePath;
        var imports = _dependencies.Imports.ToDictionary(pair => pair.Key, pair => $"{basePath}/{pair.Value}");
        imports[RuntimeSources.ClientConfigSpecifier] = $"{basePath}/runtime/config.js";
        var scripts = new List<string>();
        if (_refreshEnabled)
        {
            // Must run before react-dom is first evaluated.
            scripts.Add($"{basePath}/refresh.js");
        }
        scripts.Add($"{basePath}/client.js");
        if (includeIslands)
        {
            scripts.Add($"{basePath}/runtime/islands.js");
        }
        return new NiteHead(imports, [], scripts);
    }

    public async ValueTask<string> GetHtmlAsync(string page, HttpContext context)
    {
        var file = _project.ResolveClientPath(page);
        if (file is null || !File.Exists(file))
        {
            throw new InvalidOperationException($"HTML entry '{page}' was not found in {_project.ClientRoot}.");
        }

        var relativePage = _project.RelativeToClientRoot(file);
        var basePath = context.Request.PathBase + NiteProject.BasePath;
        var html = HtmlDocument.Rewrite(await File.ReadAllTextAsync(file), reference =>
        {
            var local = HtmlDocument.ResolveLocal(relativePage, reference.Url);
            var target = local is null ? null : _project.ResolveClientPath(local);
            if (target is null)
            {
                return null;
            }

            if (reference.Kind == HtmlReferenceKind.ModuleScript)
            {
                var module = ResolveModuleFile(target);
                return module is null ? null
                    : $"<script type=\"module\" src=\"{basePath}/src/{_project.RelativeToClientRoot(module)}\"></script>";
            }
            return File.Exists(target)
                ? $"<link rel=\"stylesheet\" href=\"{basePath}/src/{_project.RelativeToClientRoot(target)}\">"
                : null;
        });

        var head = await GetHeadAsync(context, includeIslands: false);
        return HtmlDocument.InjectHead(html, head.ToHtml());
    }

    public ValueTask<NiteAsset> GetIslandAsync(string name, HttpContext context)
    {
        var islands = _project.FindIslands();
        if (!islands.TryGetValue(name, out var file))
        {
            throw new InvalidOperationException(
                $"No island named '{name}' in {_project.IslandsDirectory}. Available: {(islands.Count == 0 ? "none" : string.Join(", ", islands.Keys))}.");
        }
        return ValueTask.FromResult(SourceAsset(file, context));
    }

    public ValueTask<NiteAsset> GetModuleAsync(string source, HttpContext context)
    {
        var file = _project.ResolveClientPath(source) is { } path ? ResolveModuleFile(path) : null;
        if (file is null)
        {
            throw new InvalidOperationException($"Module '{source}' was not found in {_project.ClientRoot}.");
        }

        var relative = _project.RelativeToClientRoot(file);
        if (!_project.Config.Entries.Any(e => NitePaths.ToUrlPath(e).TrimStart('/') == relative) && _warnedModules.TryAdd(relative, 0))
        {
            _logger.LogWarning("'{Module}' is not listed in \"entries\" in nite.json, so production builds will not include it", relative);
        }
        return ValueTask.FromResult(SourceAsset(file, context));
    }

    private NiteAsset SourceAsset(string file, HttpContext context) =>
        new($"{context.Request.PathBase}{NiteProject.BasePath}/src/{_project.RelativeToClientRoot(file)}", [], []);

    private static async Task WriteErrorModuleAsync(HttpContext context, ErrorPayload error)
    {
        // A module that reports its own failure: the overlay shows up even on first page load,
        // before the WebSocket has connected.
        var payload = JsonSerializer.Serialize(error, JsonSerializerOptions.Web);
        var code = $$"""
            import { showErrorOverlay } from {{JsonSerializer.Serialize($"{context.Request.PathBase}/client.js")}};
            const error = {{payload}};
            showErrorOverlay(error);
            throw new Error(`[nite] ${error.file ? error.file + ": " : ""}${error.message}`);
            """;
        await WriteAsync(context, code, "text/javascript");
    }

    private static Task WriteAsync(HttpContext context, string body, string contentType) =>
        WriteAsync(context, Encoding.UTF8.GetBytes(body), contentType + "; charset=utf-8");

    private static async Task WriteAsync(HttpContext context, byte[] body, string contentType, string? cacheControl = "no-cache")
    {
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..16]}\"";
        if (cacheControl is not null)
        {
            context.Response.Headers.CacheControl = cacheControl;
        }
        context.Response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body);
    }

    private static string ContentType(string file)
    {
        if (Path.GetExtension(file) is ".js" or ".mjs")
        {
            return "text/javascript; charset=utf-8";
        }
        return ContentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";
    }

    public async ValueTask DisposeAsync()
    {
        NiteHotReload.Updated -= OnServerHotReload;
        _sourceWatcher?.Dispose();
        foreach (var watcher in _projectWatchers ?? [])
        {
            watcher.Dispose();
        }
        if (_css is not null)
        {
            await _css.DisposeAsync();
        }
        _envWatcher?.Dispose();
        await _envTimer.DisposeAsync();
        _configWatcher?.Dispose();
        await _changeTimer.DisposeAsync();
        await _reoptimizeTimer.DisposeAsync();
        await _configTimer.DisposeAsync();
        if (_esbuild is not null)
        {
            await _esbuild.DisposeAsync();
        }
    }

    private sealed record BuiltStylesheet(string Key, string BasePath, string Css, IReadOnlyDictionary<string, string>? Classes,
        IReadOnlyDictionary<string, (long, long)> Files)
    {
        /// <summary>The files that went through the CSS pipeline (not just their dependencies).</summary>
        public IReadOnlySet<string> Processed { get; init; } = new HashSet<string>();

        public bool IsCurrent() => Files.All(pair => File.Exists(pair.Key) && Stamp(pair.Key) == pair.Value);
    }

    private sealed record CachedModule((long Ticks, long Length) Stamp, string Code, IReadOnlyList<string> MissingPackages, bool IsBoundary);

    private sealed class UpdatePlan
    {
        public bool Reload { get; set; }
        public HashSet<string> Boundaries { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Touched { get; } = new(StringComparer.Ordinal);
        public List<string> Stylesheets { get; } = [];
        public List<ErrorPayload> Errors { get; } = [];
        public List<string> Cleared { get; } = [];
    }

    private sealed record ErrorPayload(string Message, string? File = null, int Line = 0, int Column = 0, string? LineText = null)
    {
        public static ErrorPayload From(EsbuildException ex, string file)
        {
            var error = ex.Errors.FirstOrDefault();
            return new ErrorPayload(error?.Text ?? ex.Message, file, error?.Line ?? 0, error?.Column ?? 0, error?.LineText);
        }
    }
}
