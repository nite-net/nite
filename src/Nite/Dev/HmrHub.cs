using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Nite.Dev;

/// <summary>The WebSocket channel the dev client listens on.</summary>
internal sealed class HmrHub
{
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _clients = new();

    public int ClientCount => _clients.Count;

    /// <summary>How long a client gets to answer the server's close frame on shutdown.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var gate = new SemaphoreSlim(1, 1);
        _clients[socket] = gate;
        using var receive = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        // Kestrel waits for open requests on shutdown, and this one never ends by itself: without
        // this the host sits out the whole HostOptions.ShutdownTimeout while a tab is open. Say
        // "going away" (1001) instead; the client reconnects and reloads once the app is back.
        var stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var onStopping = stopping.Register(() => _ = CloseForShutdownAsync(socket, gate, receive));
        try
        {
            var buffer = new byte[1024];
            // CloseSent: we said goodbye on shutdown and are waiting for the client's reply.
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                // The client never sends anything meaningful; reading is how we notice it left.
                var result = await socket.ReceiveAsync(buffer, receive.Token);
                if (result.MessageType == WebSocketMessageType.Close && socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(socket, out _);
        }
    }

    private static async Task CloseForShutdownAsync(WebSocket socket, SemaphoreSlim gate, CancellationTokenSource receive)
    {
        try
        {
            // A client that never answers must not hold up shutdown either.
            receive.CancelAfter(CloseTimeout);
            using var timeout = new CancellationTokenSource(CloseTimeout);
            await gate.WaitAsync(timeout.Token);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Server shutting down", timeout.Token);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The socket is already gone, or the receive loop gave up on it; either way it is closed.
        }
    }

    public async Task BroadcastAsync(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web);
        foreach (var (socket, gate) in _clients)
        {
            await gate.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
                _clients.TryRemove(socket, out _);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
