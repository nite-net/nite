using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Nite.Assets;
using Nite.Configuration;
using Nite.Esbuild;
using Nite.Html;
using Nite.Packages;
using Nite.Runtime;

namespace Nite.Dev;

/// <summary>
/// The development server behind <c>/_nite</c>. Source files are compiled on request (one esbuild
/// transform each, cached until the file changes), bare imports resolve through an import map to
/// pre-bundled dependencies, and a file watcher drives hot updates over a WebSocket: React
/// components refresh in place, CSS swaps in place, and anything else reloads the page.
/// </summary>
public sealed class NiteDevServer : INiteAssets, IAsyncDisposable
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

    private NiteProject _project;
    private EsbuildService? _esbuild;
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

    /// <summary>Starts installing, scanning and pre-bundling in the background. Safe to call more than once.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ready = RunExclusiveAsync(InitializeAsync);
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

        await OptimizeAsync();
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
        else if (extension == ".css" && asModule)
        {
            _served.TryAdd(requested, 0);
            var css = await File.ReadAllTextAsync(requested);
            var id = _project.RelativeToClientRoot(requested);
            var code = $$"""
                const id = {{JsonSerializer.Serialize(id)}};
                const css = {{JsonSerializer.Serialize(css)}};
                let style = document.querySelector(`style[data-nite-css="${CSS.escape(id)}"]`);
                if (!style) {
                  style = document.createElement("style");
                  style.dataset.niteCss = id;
                  document.head.appendChild(style);
                }
                style.textContent = css;
                export default css;
                """;
            await WriteAsync(context, code, "text/javascript");
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
            return cached;
        }

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
            if (ResolveImport(file, specifier) is { } target)
            {
                imports.Add(target);
                continue;
            }
            if (!ImportScanner.IsBare(specifier))
            {
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

            if (changed.Any(p => Path.GetFileName(p).StartsWith("tsconfig", StringComparison.Ordinal) && p.EndsWith(".json", StringComparison.Ordinal)))
            {
                // Path aliases may have moved; every compiled module's imports could resolve differently.
                LoadProjectInputs();
                _modules.Clear();
                await _hub.BroadcastAsync(new { type = "reload" });
                return;
            }

            var plan = await PlanUpdateAsync(changed);
            if (plan.Reload)
            {
                _logger.LogDebug("Reloading after changes to {Files}", string.Join(", ", changed.Select(_project.RelativeToClientRoot)));
                await _hub.BroadcastAsync(new { type = "reload" });
                return;
            }

            foreach (var error in plan.Errors)
            {
                await _hub.BroadcastAsync(new { type = "error", error.Message, error.File, error.Line, error.Column, error.LineText });
            }
            foreach (var css in plan.Stylesheets)
            {
                var relative = _project.RelativeToClientRoot(css);
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
                await _hub.BroadcastAsync(new { type = "update", updates, cleared = plan.Cleared });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling file changes failed; reloading");
            await _hub.BroadcastAsync(new { type = "reload" });
        }
        finally
        {
            _flushLock.Release();
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

            if (extension == ".css")
            {
                plan.Stylesheets.Add(path);
                continue;
            }
            if (extension == ".html")
            {
                plan.Reload = true;
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

    private async Task<bool> PropagateAsync(string file, UpdatePlan plan)
    {
        if (!plan.Touched.Add(file))
        {
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
