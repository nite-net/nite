using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Nite.Assets;

/// <summary>Writes <see cref="NiteOptions.ClientConfig"/>'s values into a page, for <c>nite:config</c> to read.</summary>
internal static class NiteClientConfig
{
    public const string ElementId = "nite-config";

    // The default encoder escapes <, > and &, so the JSON cannot close the script element early.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The <c>&lt;script type="application/json"&gt;</c> element, or null when no client config is set.</summary>
    public static async ValueTask<string?> RenderAsync(HttpContext context)
    {
        var options = context.RequestServices.GetService<IOptions<NiteOptions>>()?.Value;
        if (options is null || (options.ClientConfig is null && options.ClientConfigAsync is null))
        {
            return null;
        }
        if (options.ClientConfig is not null && options.ClientConfigAsync is not null)
        {
            throw new InvalidOperationException("Set NiteOptions.ClientConfig or NiteOptions.ClientConfigAsync, not both.");
        }

        var value = options.ClientConfigAsync is { } load ? await load(context) : options.ClientConfig!(context);
        return Render(value);
    }

    public static string Render(object? value) =>
        $"<script type=\"application/json\" id=\"{ElementId}\">{JsonSerializer.Serialize(value ?? new { }, Json)}</script>";
}
