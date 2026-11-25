using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nite.Dev;

namespace Nite.Tests;

public class HmrHubTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_open_dev_socket_does_not_hold_up_shutdown(bool clientAnswersClose)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // Long enough that waiting it out fails the test by a wide margin.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        await using var app = builder.Build();
        var hub = new HmrHub();
        app.UseWebSockets();
        app.Map("/ws", hub.AcceptAsync);
        await app.StartAsync();

        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/ws" }.Uri, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hub.ClientCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(1, hub.ClientCount);

        var receive = Task.Run(async () =>
        {
            if (!clientAnswersClose)
            {
                return (WebSocketCloseStatus?)null;
            }
            var result = await client.ReceiveAsync(new byte[64], CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            return result.CloseStatus;
        });

        var stopwatch = Stopwatch.StartNew();
        await app.StopAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Shutdown took {stopwatch.Elapsed}");
        if (clientAnswersClose)
        {
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await receive);
            Assert.Equal(0, hub.ClientCount);
        }
    }
}
