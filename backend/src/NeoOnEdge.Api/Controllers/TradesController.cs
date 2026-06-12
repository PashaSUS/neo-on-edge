using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TradesController(AppDbContext db) : ControllerBase
{
    /// GET /api/trades?src=d&pairId=1&limit=100&before=<ts>
    [HttpGet]
    public async Task<IActionResult> GetTrades(
        [FromQuery] string src, [FromQuery] string pairId,
        [FromQuery] int limit = 100, [FromQuery] long? before = null)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(pairId))
            return BadRequest("src and pairId are required");
        limit = Math.Clamp(limit, 1, 500);

        if (src == "ad")
        {
            if (!long.TryParse(pairId, out var marketId))
                return BadRequest("Alcor order-book pairId must be a numeric market id");

            var query = db.MarketMatches.AsNoTracking()
                .Where(x => x.Src == src && x.MarketId == marketId);

            if (before.HasValue)
                query = query.Where(x => x.UpdatedAtTime < before.Value);

            var rows = await query
                .OrderByDescending(x => x.UpdatedAtTime)
                .Take(limit)
                .Select(x => new TradeDto(
                    x.TrxId,
                    x.Src,
                    x.MarketId.ToString(),
                    $"{x.Asker}/{x.Bidder}",
                    x.AmountAsk,
                    x.AmountBid,
                    x.CodeAsk,
                    x.CodeBid,
                    x.UpdatedAtTime))
                .ToListAsync();

            return Ok(rows);
        }

        if (src == "a2")
        {
            var query = db.SwapV3Orders.AsNoTracking()
                .Where(x => x.Src == src && x.PairId == pairId);

            if (before.HasValue)
                query = query.Where(x => x.UpdatedAtTime < before.Value);

            var rows = await query
                .OrderByDescending(x => x.UpdatedAtTime)
                .Take(limit)
                .Select(x => new TradeDto(
                    x.TrxId,
                    x.Src,
                    x.PairId,
                    x.Maker,
                    x.AmountIn,
                    x.AmountOut,
                    x.CodeIn,
                    x.CodeOut,
                    x.UpdatedAtTime))
                .ToListAsync();

            return Ok(rows);
        }

        var ammQuery = db.SwapOrders.AsNoTracking()
            .Where(x => x.Src == src && x.PairId == pairId);

        if (before.HasValue)
            ammQuery = ammQuery.Where(x => x.UpdatedAtTime < before.Value);

        var ammRows = await ammQuery
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit)
            .Select(x => new TradeDto(
                x.TrxId,
                x.Src,
                x.PairId,
                x.Maker,
                x.AmountIn,
                x.AmountOut,
                x.CodeIn,
                x.CodeOut,
                x.UpdatedAtTime))
            .ToListAsync();

        return Ok(ammRows);
    }

    private sealed record TradeDto(
        string TrxId,
        string Src,
        string PairId,
        string Maker,
        decimal AmountIn,
        decimal AmountOut,
        string CodeIn,
        string CodeOut,
        long UpdatedAtTime);
}
