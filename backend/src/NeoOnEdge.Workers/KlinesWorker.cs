using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;
using NeoOnEdge.Core.Models;
using StackExchange.Redis;

namespace NeoOnEdge.Workers;

/// <summary>
/// Periodically recomputes OHLCV kline bars for all active pairs.
/// </summary>
public sealed class KlinesWorker : BackgroundService
{
    private static readonly string[] Durations = ["1m", "5m", "15m", "1h", "4h", "1d", "1w"];
    private static readonly Dictionary<string, long> DurationMs = new()
    {
        ["1m"]  = 60_000,
        ["5m"]  = 300_000,
        ["15m"] = 900_000,
        ["1h"]  = 3_600_000,
        ["4h"]  = 14_400_000,
        ["1d"]  = 86_400_000,
        ["1w"]  = 604_800_000,
    };

    private readonly ILogger<KlinesWorker> _log;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISubscriber _sub;
    private readonly ConcurrentDictionary<string, PairGate> _pairLocks = new();
    private readonly ConcurrentDictionary<string, PairKey> _dirtyPairs = new();

    private sealed class PairGate
    {
        public SemaphoreSlim Gate { get; init; } = new SemaphoreSlim(1, 1);
        public long LastUsedMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public KlinesWorker(
        ILogger<KlinesWorker> log,
        IServiceScopeFactory scopes,
        IConnectionMultiplexer redis)
    {
        _log    = log;
        _scopes = scopes;
        _sub    = redis.GetSubscriber();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Subscribe to Redis trade events to trigger incremental kline updates
        await _sub.SubscribeAsync(RedisChannel.Pattern("trades:*"), (channel, msg) =>
        {
            MarkDirtyPair(channel.ToString());
        });

        _log.LogInformation("Klines worker started; incremental updates enabled, full recompute deferred");
        var nextFullRecompute = DateTimeOffset.UtcNow.AddHours(1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await FlushDirtyPairs(ct); }
            catch (Exception ex) { _log.LogError(ex, "Klines dirty-pair flush error"); }

            if (DateTimeOffset.UtcNow < nextFullRecompute)
                continue;

            try { await RecomputeAll(ct); }
            catch (Exception ex) { _log.LogError(ex, "Klines full recompute error"); }
            nextFullRecompute = DateTimeOffset.UtcNow.AddHours(1);
        }
    }

    private void MarkDirtyPair(string channel)
    {
        // channel = "trades:{src}:{pairId}"
        var parts = channel.Split(':');
        if (parts.Length < 3) return;
        var src    = parts[1];
        var pairId = parts[2];
        var key = $"{src}:{pairId}";

        _dirtyPairs[key] = new PairKey(src, pairId);
    }

    private async Task FlushDirtyPairs(CancellationToken ct)
    {
        foreach (var item in _dirtyPairs.ToArray())
        {
            if (ct.IsCancellationRequested) break;
            if (!_dirtyPairs.TryRemove(item.Key, out var pair)) continue;

            try
            {
                await UpdateKlinesForPairLocked(pair.Src, pair.PairId, ct, incremental: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Incremental klines update failed for {Src}:{PairId}", pair.Src, pair.PairId);
            }
        }
    }

    private async Task RecomputeAll(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        long windowStart = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        var swapPairs = await db.SwapOrders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= windowStart)
            .Select(x => new PairKey(x.Src, x.PairId))
            .Distinct()
            .ToListAsync(ct);

        var v3Pairs = await db.SwapV3Orders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= windowStart)
            .Select(x => new PairKey(x.Src, x.PairId))
            .Distinct()
            .ToListAsync(ct);

        var pairs = swapPairs.Concat(v3Pairs).Distinct().ToList();

        foreach (var pair in pairs)
        {
            if (ct.IsCancellationRequested) break;
            await UpdateKlinesForPairLocked(pair.Src, pair.PairId, ct, incremental: false);
        }

        // Prune stale pair locks to avoid unbounded growth in long-running services
        try { PrunePairLocks(); } catch { }
    }

    private void PrunePairLocks()
    {
        const long TtlMs = 24 * 60 * 60 * 1000; // 24h
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _pairLocks)
        {
            try
            {
                var key = kv.Key;
                var gate = kv.Value;
                if (now - gate.LastUsedMs > TtlMs)
                {
                    if (_pairLocks.TryRemove(key, out var removed))
                    {
                        try { removed.Gate.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    private async Task UpdateKlinesForPairLocked(string src, string pairId, CancellationToken ct, bool incremental)
    {
        var key = $"{src}:{pairId}";
        var gate = _pairLocks.GetOrAdd(key, _ => new PairGate());

        await gate.Gate.WaitAsync(ct);
        try
        {
            await UpdateKlinesForPair(src, pairId, ct, incremental);
        }
        finally
        {
            try { gate.LastUsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } catch { }
            gate.Gate.Release();
        }
    }

    private async Task UpdateKlinesForPair(string src, string pairId, CancellationToken ct, bool incremental)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        long windowStart = (incremental
            ? DateTimeOffset.UtcNow.AddDays(-8)
            : DateTimeOffset.UtcNow.AddDays(-30)).ToUnixTimeMilliseconds();

        var trades = src == "a2"
            ? await db.SwapV3Orders.AsNoTracking()
                .Where(x => x.Src == src && x.PairId == pairId && x.UpdatedAtTime >= windowStart)
                .Select(x => new TradePoint(
                    x.UpdatedAtTime,
                    x.GlobalSequence,
                    x.Maker,
                    x.AmountIn,
                    x.AmountOut))
                .ToListAsync(ct)
            : await db.SwapOrders.AsNoTracking()
                .Where(x => x.Src == src && x.PairId == pairId && x.UpdatedAtTime >= windowStart)
                .Select(x => new TradePoint(
                    x.UpdatedAtTime,
                    x.GlobalSequence,
                    x.Maker,
                    x.AmountIn,
                    x.AmountOut))
                .ToListAsync(ct);

        if (trades.Count == 0)
            return;

        foreach (var dur in Durations)
        {
            long bucketMs = DurationMs[dur];

            var existing = await db.KlineBars
                .Where(x => x.Src == src &&
                            x.PairId == pairId &&
                            x.Duration == dur &&
                            x.BucketTs >= windowStart)
                .ToListAsync(ct);
            var existingByBucket = existing.ToDictionary(x => x.BucketTs);

            var bars = trades
                .GroupBy(x => (x.UpdatedAtTime / bucketMs) * bucketMs)
                .Select(group =>
                {
                    var ordered = group
                        .OrderBy(x => x.UpdatedAtTime)
                        .ThenBy(x => x.GlobalSequence)
                        .ToList();
                    var prices = ordered.Select(Price).ToList();

                    return new KlineBar
                    {
                        Src = src,
                        PairId = pairId,
                        Duration = dur,
                        BucketTs = group.Key,
                        Open = prices.First(),
                        High = prices.Max(),
                        Low = prices.Min(),
                        Close = prices.Last(),
                        VolumeA = ordered.Sum(x => x.AmountIn),
                        VolumeB = ordered.Sum(x => x.AmountOut),
                        TradeCount = ordered.Count,
                        UniqueAccounts = ordered.Select(x => x.Maker).Distinct().Count()
                    };
                })
                .ToList();

            foreach (var bar in bars)
            {
                if (existingByBucket.TryGetValue(bar.BucketTs, out var existingBar))
                {
                    existingBar.Open = bar.Open;
                    existingBar.High = bar.High;
                    existingBar.Low = bar.Low;
                    existingBar.Close = bar.Close;
                    existingBar.VolumeA = bar.VolumeA;
                    existingBar.VolumeB = bar.VolumeB;
                    existingBar.TradeCount = bar.TradeCount;
                    existingBar.UniqueAccounts = bar.UniqueAccounts;
                }
                else
                {
                    await db.KlineBars.AddAsync(bar, ct);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private static decimal Price(TradePoint trade)
        => trade.AmountIn > 0 ? trade.AmountOut / trade.AmountIn : 0;

    private sealed record PairKey(string Src, string PairId);

    private sealed record TradePoint(
        long UpdatedAtTime,
        long GlobalSequence,
        string Maker,
        decimal AmountIn,
        decimal AmountOut);
}
