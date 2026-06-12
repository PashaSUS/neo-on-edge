using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NeoOnEdge.Api.Hubs;
using NeoOnEdge.Core.Configuration;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Services;

/// <summary>
/// Bridges Redis pub/sub trade/stats events into SignalR groups.
/// </summary>
public sealed class RedisSignalRBridge : BackgroundService
{
    private readonly ISubscriber _sub;
    private readonly IHubContext<NeoEdgeHub> _hub;
    private readonly ILogger<RedisSignalRBridge> _log;

    public RedisSignalRBridge(
        IConnectionMultiplexer redis,
        IHubContext<NeoEdgeHub> hub,
        ILogger<RedisSignalRBridge> log)
    {
        _sub = redis.GetSubscriber();
        _hub = hub;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _sub.SubscribeAsync(RedisChannel.Pattern("trades:*"), async (channel, msg) =>
        {
            var parts = channel.ToString().Split(':');
            if (parts.Length < 3) return;
            var group = $"trades_{parts[1]}_{parts[2]}";
            await _hub.Clients.Group(group).SendAsync("trade", msg.ToString(), ct);
        });

        await _sub.SubscribeAsync(RedisChannel.Pattern("stats_update:*"), async (channel, msg) =>
        {
            var parts = channel.ToString().Split(':');
            if (parts.Length < 3) return;
            var group = $"stats_{parts[1]}_{parts[2]}";
            await _hub.Clients.Group(group).SendAsync("stats", msg.ToString(), ct);
        });

        await _sub.SubscribeAsync(RedisChannel.Pattern("kline_update:*"), async (channel, msg) =>
        {
            // channel = kline_update:{src}:{pairId}:{duration}
            var parts = channel.ToString().Split(':');
            if (parts.Length < 4) return;
            var group = $"klines_{parts[1]}_{parts[2]}_{parts[3]}";
            await _hub.Clients.Group(group).SendAsync("kline", msg.ToString(), ct);
        });

        await _sub.SubscribeAsync(RedisChannel.Literal("tokens_update"), async (_channel, msg) =>
        {
            await _hub.Clients.Group("tokens").SendAsync("tokens", msg.ToString(), ct);
        });

        // Keep alive
        await Task.Delay(Timeout.Infinite, ct);
    }
}
