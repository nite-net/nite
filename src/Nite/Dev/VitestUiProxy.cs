using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Nite.Testing;

namespace Nite.Dev;

/// <summary>
/// Serves the UI of a running <c>nite test --ui</c> inside the app, at <c>/_nite/vitest/</c>, and
/// the last coverage report at <c>/_nite/coverage/</c>. Requests pass through to Vitest unchanged,
/// cookies and token included, so Vitest's own check still guards its API: the dev server never
/// learns the token and never adds it. Without that, any page that reached the app (through DNS
/// rebinding, say) could drive Vitest, which runs code.
/// </summary>
internal sealed class VitestUiProxy(NiteProject project)
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
    });

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host",
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>A request for <c>/_nite/vitest/…</c>, <c>/__vitest_api__</c> or <c>/__open-in-editor</c>.</summary>
    public async Task ProxyAsync(HttpContext context, string path)
    {
        if (!IsLocal(context))
        {
            // Vitest's API runs code; on top of its token, keep it to this machine and this app's pages.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (VitestUi.Find(project) is not { } ui)
        {
            await NotRunningAsync(context, path);
            return;
        }

        var target = $"127.0.0.1:{ui.Port}{path}{context.Request.QueryString}";
        if (context.WebSockets.IsWebSocketRequest)
        {
            await RelaySocketAsync(context, new Uri("ws://" + target));
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "http://" + target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
        }
        foreach (var (name, values) in context.Request.Headers)
        {
            if (HopByHop.Contains(name))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            await NotRunningAsync(context, path);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!HopByHop.Contains(name))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>A file of the coverage report under <c>.nite/coverage</c>.</summary>
    public async Task ServeCoverageAsync(HttpContext context, string relative)
    {
        var directory = Path.GetFullPath(Path.Combine(project.WorkDirectory, "coverage"));
        if (relative.Length == 0 && !context.Request.Path.Value!.EndsWith('/'))
        {
            // index.html links its files relatively, so it must load from the folder's URL.
            context.Response.Redirect(context.Request.PathBase + context.Request.Path + "/");
            return;
        }
        var file = Path.GetFullPath(Path.Combine(directory, relative.Length == 0 || relative.EndsWith('/') ? relative + "index.html" : relative));
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            if (relative.Length == 0)
            {
                await WriteHtmlAsync(context, "No coverage report yet", "Run <code>nite test --coverage</code>, then reload this page.");
            }
            return;
        }

        context.Response.ContentType = ContentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(file, context.RequestAborted);
    }

    private static async Task RelaySocketAsync(HttpContext context, Uri target)
    {
        using var upstream = new ClientWebSocket();
        try
        {
            await upstream.ConnectAsync(target, context.RequestAborted);
        }
        catch (WebSocketException)
        {
            // Vitest refuses a socket without the right token with a plain 401.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var downstream = await context.WebSockets.AcceptWebSocketAsync();
        using var done = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var up = PumpAsync(downstream, upstream, done.Token);
        var down = PumpAsync(upstream, downstream, done.Token);
        // When one side closes, give the other time to answer the close before cutting it off:
        // cancelling a receive aborts the socket, which can drop data the browser has not read yet.
        var first = await Task.WhenAny(up, down);
        await Task.WhenAny(first == up ? down : up, Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted));
        await done.CancelAsync();
        await Task.WhenAll(Quietly(up), Quietly(down));
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
        {
            var result = await from.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await to.CloseOutputAsync(from.CloseStatus ?? WebSocketCloseStatus.NormalClosure, from.CloseStatusDescription, cancellationToken);
                return;
            }
            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
        }
    }

    /// <summary>From this machine, and (when the browser says where from) from a page of this app.</summary>
    internal static bool IsLocal(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
        {
            return false;
        }
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static Task NotRunningAsync(HttpContext context, string path)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return path.StartsWith(VitestUi.BasePath, StringComparison.Ordinal) && !context.WebSockets.IsWebSocketRequest
            ? WriteHtmlAsync(context, "Vitest is not running",
                "Run <code>nite test --ui</code> in the project folder, then open the link it prints. Its path and token work on this address too.")
            : Task.CompletedTask;
    }

    private static Task WriteHtmlAsync(HttpContext context, string title, string body)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{{title}}</title>
            <style>body{font:16px/1.5 system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem;color-scheme:light dark}code{font-size:.95em}</style>
            </head><body><h1>{{title}}</h1><p>{{body}}</p></body></html>
            """;
        return context.Response.WriteAsync(html, Encoding.UTF8);
    }
}
