using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Nite.Dev;

/// <summary>The WebSocket channel the dev client listens on.</summary>
internal sealed class HmrHub
{
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _clients = new();

    public int ClientCount => _clients.Count;

    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        _clients[socket] = new SemaphoreSlim(1, 1);
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                // The client never sends anything meaningful; reading is how we notice it left.
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
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
