using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Nite.Build;

namespace Nite.Assets;

/// <summary>Answers tag helpers from the manifest written by <c>nite build</c>.</summary>
public sealed class ProductionAssets(IWebHostEnvironment environment) : INiteAssets
{
    private readonly Lazy<NiteManifest> _manifest = new(() => Load(environment));

    public bool IsDevelopment => false;

    public ValueTask<NiteHead> GetHeadAsync(HttpContext context)
    {
        var manifest = _manifest.Value;
        if (!manifest.Entries.TryGetValue(NiteManifest.RuntimeKey, out var runtime))
        {
            return ValueTask.FromResult(new NiteHead(null, [], []));
        }

        var preloads = runtime.Imports.Select(path => Url(context, path)).ToList();
        return ValueTask.FromResult(new NiteHead(null, preloads, [Url(context, runtime.File)]));
    }

    public ValueTask<NiteAsset> GetIslandAsync(string name, HttpContext context)
    {
        var manifest = _manifest.Value;
        if (!manifest.Islands.TryGetValue(name, out var key) || !manifest.Entries.TryGetValue(key, out var entry))
        {
            throw new InvalidOperationException($"Island '{name}' is not in the Nite build. Rebuild with `nite build`.");
        }
        return ValueTask.FromResult(ToAsset(context, entry));
    }

    public ValueTask<NiteAsset> GetModuleAsync(string source, HttpContext context)
    {
        var key = NitePaths.ToUrlPath(source).TrimStart('/');
        var manifest = _manifest.Value;
        var entry = manifest.Entries.GetValueOrDefault(key)
            ?? manifest.Entries.FirstOrDefault(e => Path.ChangeExtension(e.Key, null) == Path.ChangeExtension(key, null)).Value
            ?? throw new InvalidOperationException($"Module '{source}' is not in the Nite build. Add it to \"entries\" in nite.json.");
        return ValueTask.FromResult(ToAsset(context, entry));
    }

    private static NiteAsset ToAsset(HttpContext context, NiteManifestEntry entry) =>
        new(Url(context, entry.File),
            entry.Css.Select(path => Url(context, path)).ToList(),
            entry.Imports.Select(path => Url(context, path)).ToList());

    private static string Url(HttpContext context, string path) =>
        $"{context.Request.PathBase}{NiteProject.BasePath}/{path}";

    private static NiteManifest Load(IWebHostEnvironment environment)
    {
        var file = environment.WebRootFileProvider.GetFileInfo($"{NiteProject.BasePath.TrimStart('/')}/{NiteManifest.FileName}");
        if (!file.Exists)
        {
            throw new InvalidOperationException(
                $"wwwroot{NiteProject.BasePath}/{NiteManifest.FileName} was not found. Run `nite build` (publishing does this for you) or run in Development.");
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return NiteManifest.Parse(reader.ReadToEnd());
    }
}
