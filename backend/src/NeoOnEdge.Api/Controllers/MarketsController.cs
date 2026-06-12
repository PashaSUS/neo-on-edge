using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class MarketsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IDatabase _cache;

    public MarketsController(AppDbContext db, IConnectionMultiplexer redis)
    {
        _db    = db;
        _cache = redis.GetDatabase();
    }

    /// GET /api/markets — list all pairs with 24h stats
    [HttpGet]
    public async Task<IActionResult> GetMarkets([FromQuery] string? src = null)
    {
        long since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        var ammQuery = _db.SwapOrders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since);
        var v3Query = _db.SwapV3Orders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since);

        if (!string.IsNullOrWhiteSpace(src))
        {
            ammQuery = ammQuery.Where(x => x.Src == src);
            v3Query  = v3Query.Where(x => x.Src == src);
        }

        var amm = await ammQuery
            .GroupBy(x => new { x.Src, x.PairId, x.CodeIn, x.CodeOut })
            .Select(g => new MarketSummary(
                g.Key.Src,
                g.Key.PairId,
                g.Key.CodeIn,
                g.Key.CodeOut,
                g.Sum(x => x.AmountIn),
                g.Sum(x => x.AmountOut),
                g.Count(),
                g.Max(x => x.UpdatedAtTime)))
            .ToListAsync();

        var v3 = await v3Query
            .GroupBy(x => new { x.Src, x.PairId, x.CodeIn, x.CodeOut })
            .Select(g => new MarketSummary(
                g.Key.Src,
                g.Key.PairId,
                g.Key.CodeIn,
                g.Key.CodeOut,
                g.Sum(x => x.AmountIn),
                g.Sum(x => x.AmountOut),
                g.Count(),
                g.Max(x => x.UpdatedAtTime)))
            .ToListAsync();

        return Ok(amm.Concat(v3).OrderByDescending(x => x.VolumeA));
    }

    /// GET /api/markets/{src}/{pairId}/stats
    [HttpGet("{src}/{pairId}/stats")]
    public async Task<IActionResult> GetStats(string src, string pairId)
    {
        var cached = await _cache.StringGetAsync($"stats:{src}:{pairId}");
        if (cached.HasValue)
            return Content(cached.ToString(), "application/json");

        long since = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        object? row;
        if (src == "ad")
        {
            if (!long.TryParse(pairId, out var marketId))
                return BadRequest("Alcor order-book pairId must be a numeric market id");

            row = await _db.MarketMatches.AsNoTracking()
                .Where(x => x.Src == src && x.MarketId == marketId && x.UpdatedAtTime >= since)
                .OrderByDescending(x => x.UpdatedAtTime)
                .FirstOrDefaultAsync();
        }
        else if (src == "a2")
        {
            row = await _db.SwapV3Orders.AsNoTracking()
                .Where(x => x.Src == src && x.PairId == pairId && x.UpdatedAtTime >= since)
                .OrderByDescending(x => x.UpdatedAtTime)
                .FirstOrDefaultAsync();
        }
        else
        {
            row = await _db.SwapOrders.AsNoTracking()
                .Where(x => x.Src == src && x.PairId == pairId && x.UpdatedAtTime >= since)
                .OrderByDescending(x => x.UpdatedAtTime)
                .FirstOrDefaultAsync();
        }

        return row is null ? NotFound() : Ok(row);
    }

    private sealed record MarketSummary(
        string Src,
        string PairId,
        string TokenA,
        string TokenB,
        decimal VolumeA,
        decimal VolumeB,
        int TradeCount,
        long LastTrade);
}
