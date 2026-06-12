using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Hubs;

/// <summary>
/// SignalR hub for real-time DEX data streaming.
///
/// Client group naming:
///   trades_{src}_{pairId}       — trade stream for a pair
///   klines_{src}_{pairId}_{dur} — candlestick updates for a duration
///   orderbook_{src}_{pairId}    — order-book depth updates
///   stats_{src}_{pairId}        — 24-h stats updates
///   tokens                      — token catalog refresh notifications
/// </summary>
public sealed class NeoEdgeHub : Hub
{
    public async Task SubscribeTokens()
        => await Groups.AddToGroupAsync(Context.ConnectionId, "tokens");

    public async Task UnsubscribeTokens()
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, "tokens");

    public async Task SubscribeTrades(string src, string pairId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"trades_{src}_{pairId}");

    public async Task UnsubscribeTrades(string src, string pairId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"trades_{src}_{pairId}");

    public async Task SubscribeKlines(string src, string pairId, string duration)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"klines_{src}_{pairId}_{duration}");

    public async Task UnsubscribeKlines(string src, string pairId, string duration)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"klines_{src}_{pairId}_{duration}");

    public async Task SubscribeOrderBook(string src, string pairId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"orderbook_{src}_{pairId}");

    public async Task UnsubscribeOrderBook(string src, string pairId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"orderbook_{src}_{pairId}");

    public async Task SubscribeStats(string src, string pairId)
        => await Groups.AddToGroupAsync(Context.ConnectionId, $"stats_{src}_{pairId}");

    public async Task UnsubscribeStats(string src, string pairId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stats_{src}_{pairId}");
}
