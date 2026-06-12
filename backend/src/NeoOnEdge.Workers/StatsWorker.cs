using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;
using StackExchange.Redis;
using System.Text.Json;

namespace NeoOnEdge.Workers;

/// <summary>
/// Computes and broadcasts 24-hour statistics for every pair.
/// Stats are published to Redis and served by the REST API from cache.
/// </summary>
public sealed class StatsWorker : BackgroundService
{
    private readonly ILogger<StatsWorker> _log;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDatabase _cache;
    private readonly ISubscriber _pub;

    public StatsWorker(
        ILogger<StatsWorker> log,
        IServiceScopeFactory scopes,
        IConnectionMultiplexer redis)
    {
        _log    = log;
        _scopes = scopes;
        _cache  = redis.GetDatabase();
        _pub    = redis.GetSubscriber();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ComputeStats(ct); }
            catch (Exception ex) { _log.LogError(ex, "StatsWorker error"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task ComputeStats(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        long since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        var amm = await db.SwapOrders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .GroupBy(x => new { x.Src, x.PairId, x.CodeIn, x.CodeOut })
            .Select(g => new PairStat(
                g.Key.Src,
                g.Key.PairId,
                g.Key.CodeIn,
                g.Key.CodeOut,
                g.Sum(x => x.AmountIn),
                g.Sum(x => x.AmountOut),
                g.Count(),
                g.Max(x => x.UpdatedAtTime)))
            .ToListAsync(ct);

        var v3 = await db.SwapV3Orders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .GroupBy(x => new { x.Src, x.PairId, x.CodeIn, x.CodeOut })
            .Select(g => new PairStat(
                g.Key.Src,
                g.Key.PairId,
                g.Key.CodeIn,
                g.Key.CodeOut,
                g.Sum(x => x.AmountIn),
                g.Sum(x => x.AmountOut),
                g.Count(),
                g.Max(x => x.UpdatedAtTime)))
            .ToListAsync(ct);

        foreach (var row in amm.Concat(v3))
        {
            var json = JsonSerializer.Serialize(row);
            var key  = $"stats:{row.Src}:{row.PairId}";
            await _cache.StringSetAsync(key, json, TimeSpan.FromMinutes(5));
            _pub.Publish(RedisChannel.Literal($"stats_update:{row.Src}:{row.PairId}"), json);
        }
    }

    private record PairStat(
        string Src, string PairId, string CodeIn, string CodeOut,
        decimal VolumeA, decimal VolumeB, long TradeCount, long LastTrade);
}
