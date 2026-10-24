using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Nite.Assets;
using Nite.Esbuild;
using Nite.Packages;
using Nite.Runtime;

namespace Nite.Dev;

/// <summary>
/// The development server behind <c>/_nite</c>. Source files are compiled on request (one esbuild
/// transform each, cached until the file changes), bare imports resolve through an import map to
/// pre-bundled dependencies, and a file watcher pushes CSS swaps or reloads over a WebSocket.
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
    private readonly Timer _changeTimer;
    private readonly Timer _reoptimizeTimer;
    private readonly Timer _configTimer;

    private NiteProject _project;
    private EsbuildService? _esbuild;
    private DependencyOptimizer? _optimizer;
    private DependencyBundle _dependencies = new("", new Dictionary<string, string>());
    private string _lockFingerprint = "";
    private Task _ready = Task.CompletedTask;
    private volatile bool _initialized;
    private int _started;
    private bool _structuralChange;
    private FileSystemWatcher? _sourceWatcher;
    private FileSystemWatcher? _configWatcher;

    public NiteDevServer(NiteProject project, HttpClient http, ILoggerFactory loggerFactory)
    {
        _project = project;
        _http = http;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NiteDevServer>();
        _changeTimer = new Timer(_ => _ = FlushChangesAsync());
        _reoptimizeTimer = new Timer(_ => _ = ReoptimizeAsync());
        _configTimer = new Timer(_ => _ = ReloadConfigAsync());
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
    }

    private async Task InitializeAsync()
    {
        var binary = await EsbuildBinary.EnsureAsync(_http, _project.Config.Registry);
        _esbuild = new EsbuildService(binary, _loggerFactory.CreateLogger<EsbuildService>());
        _optimizer = new DependencyOptimizer(_esbuild, _loggerFactory.CreateLogger<DependencyOptimizer>());

        await InstallAsync();

        // Compile everything once up front so the first page load already has every dependency
        // pre-bundled, instead of discovering them one reload at a time.
        if (Directory.Exists(_project.ClientRoot))
        {
            var files = Directory.EnumerateFiles(_project.ClientRoot, "*", SearchOption.AllDirectories)
                .Where(f => NiteProject.ModuleExtensions.Contains(Path.GetExtension(f)) && !f.EndsWith(".d.ts", StringComparison.Ordinal));
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
        _logger.LogInformation("Nite dev server ready for {ClientRoot}", _project.ClientRoot);
    }

    private async Task InstallAsync()
    {
        var installer = new PackageInstaller(_http, _loggerFactory.CreateLogger<PackageInstaller>());
        var lockFile = await installer.InstallAsync(_project.Directory, _project.Config);
        _lockFingerprint = lockFile.Fingerprint();
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
                await WriteAsync(context, module.Code, "text/javascript");
            }
            catch (EsbuildException ex)
            {
                var error = ex.Errors.FirstOrDefault();
                _logger.LogWarning("{Message}", ex.Message);
                await WriteErrorModuleAsync(context, new ErrorPayload(
                    error?.Text ?? ex.Message,
                    _project.RelativeToClientRoot(requested),
                    error?.Line ?? 0,
                    error?.Column ?? 0,
                    error?.LineText));
            }
        }
        else if (extension == ".css" && asModule)
        {
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
            _ => "js",
        };

        var result = await _esbuild!.TransformAsync(source,
        [
            $"--loader={loader}", "--format=esm", "--target=es2022", "--sourcemap=inline",
            $"--sourcefile={Path.GetFileName(file)}", "--jsx=automatic", "--jsx-dev",
            $"--jsx-import-source={_project.Config.JsxImportSource}",
            "--define:process.env.NODE_ENV=\"development\"",
        ]);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var discovered = false;
        foreach (var specifier in ImportScanner.FindSpecifiers(result.Code).Where(ImportScanner.IsBare))
        {
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

        var code = ImportScanner.Rewrite(result.Code, specifier => CanonicalSpecifier(file, specifier));
        var module = new CachedModule(stamp, code, missing.ToList());
        _modules[file] = module;
        return module;
    }

    /// <summary>
    /// Rewrites a relative import to the exact file it resolves to (<c>./Button</c> →
    /// <c>./Button.tsx</c>), and marks non-JS imports with <c>?import</c> so the server answers
    /// with a module instead of the raw file.
    /// </summary>
    private string? CanonicalSpecifier(string importer, string specifier)
    {
        if (!specifier.StartsWith('.'))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(importer)!;
        var queryIndex = specifier.IndexOfAny(['?', '#']);
        var pathPart = queryIndex < 0 ? specifier : specifier[..queryIndex];
        var target = ResolveModuleFile(Path.GetFullPath(Path.Combine(directory, pathPart)));
        if (target is null || !_project.IsInsideClientRoot(target))
        {
            return null;
        }

        var relative = NitePaths.ToUrlPath(Path.GetRelativePath(directory, target));
        if (!relative.StartsWith("../", StringComparison.Ordinal))
        {
            relative = "./" + relative;
        }
        if (!NiteProject.ModuleExtensions.Contains(Path.GetExtension(target).ToLowerInvariant()))
        {
            relative += "?import";
        }
        return relative;
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
            _sourceWatcher.Changed += (_, e) => OnSourceChanged(e.FullPath, structural: false);
            _sourceWatcher.Created += (_, e) => OnSourceChanged(e.FullPath, structural: true);
            _sourceWatcher.Deleted += (_, e) => OnSourceChanged(e.FullPath, structural: true);
            _sourceWatcher.Renamed += (_, e) => OnSourceChanged(e.FullPath, structural: true);
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
    }

    private void OnSourceChanged(string path, bool structural)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.EndsWith('~') || name.EndsWith(".swp", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return;
        }

        if (structural)
        {
            _structuralChange = true;
        }
        _pendingChanges[path] = 0;
        // Editors often write a file in several steps; wait for them to settle.
        _changeTimer.Change(60, Timeout.Infinite);
    }

    private async Task FlushChangesAsync()
    {
        var changed = _pendingChanges.Keys.ToList();
        foreach (var path in changed)
        {
            _pendingChanges.TryRemove(path, out _);
        }
        if (changed.Count == 0)
        {
            return;
        }

        if (_structuralChange)
        {
            // A file appeared or vanished, so any cached import resolution may now be wrong.
            _structuralChange = false;
            _modules.Clear();
        }
        else
        {
            foreach (var path in changed)
            {
                _modules.TryRemove(path, out _);
            }
        }

        if (changed.All(p => p.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
        {
            foreach (var path in changed)
            {
                var relative = _project.RelativeToClientRoot(path);
                await _hub.BroadcastAsync(new { type = "css", path = relative, url = $"src/{relative}?import" });
            }
            return;
        }

        _logger.LogDebug("Reloading after changes to {Files}", string.Join(", ", changed.Select(_project.RelativeToClientRoot)));
        await _hub.BroadcastAsync(new { type = "reload" });
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

    public async ValueTask<NiteHead> GetHeadAsync(HttpContext context)
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
        return new NiteHead(imports, [], [$"{basePath}/client.js", $"{basePath}/runtime/islands.js"]);
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
        _sourceWatcher?.Dispose();
        _configWatcher?.Dispose();
        await _changeTimer.DisposeAsync();
        await _reoptimizeTimer.DisposeAsync();
        await _configTimer.DisposeAsync();
        if (_esbuild is not null)
        {
            await _esbuild.DisposeAsync();
        }
    }

    private sealed record CachedModule((long Ticks, long Length) Stamp, string Code, IReadOnlyList<string> MissingPackages);

    private sealed record ErrorPayload(string Message, string? File = null, int Line = 0, int Column = 0, string? LineText = null);
}
