using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Nite.Assets;

/// <summary>
/// Marks the hashed files of a production build as immutable, so browsers and CDNs keep them for a
/// year without revalidating. Every bundle, chunk and asset that <c>nite build</c> writes has a
/// content hash in its name (<c>main-LCLEEUI3.js</c>), so a URL's contents never change; the files
/// without one (HTML entries and <c>manifest.json</c>) keep the server's own caching.
/// </summary>
internal static partial class ImmutableAssets
{
    public const string CacheControl = "public, max-age=31536000, immutable";

    // esbuild's [hash]: eight characters of base32 (A-Z, 2-7), after a dash, before the extension.
    [GeneratedRegex(@"^/_nite/.*-[A-Z2-7]{8}(\.[A-Za-z0-9]+)+$")]
    private static partial Regex HashedOutput();

    public static bool IsHashed(PathString path) =>
        path.HasValue
        && HashedOutput().IsMatch(path.Value!)
        && !path.Value!.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sets the header just before the response starts, after whatever serves the file (MapStaticAssets
    /// or UseStaticFiles) has set its own, so this one wins. Only successful and not-modified
    /// responses: a 404 for a mistyped hash must not be cached for a year.
    /// </summary>
    public static IApplicationBuilder UseImmutableAssets(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (IsHashed(context.Request.Path))
            {
                context.Response.OnStarting(static state =>
                {
                    var response = (HttpResponse)state;
                    if (response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status304NotModified)
                    {
                        response.Headers.CacheControl = CacheControl;
                    }
                    return Task.CompletedTask;
                }, context.Response);
            }
            return next(context);
        });
}
