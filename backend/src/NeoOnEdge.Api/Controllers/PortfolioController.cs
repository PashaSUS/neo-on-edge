using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PortfolioController(AppDbContext db) : ControllerBase
{
    /// GET /api/portfolio/{account} — swap history for an account
    [HttpGet("{account}")]
    public async Task<IActionResult> GetPortfolio(
        string account,
        [FromQuery] int limit = 100,
        [FromQuery] long? before = null)
    {
        if (string.IsNullOrWhiteSpace(account))
            return BadRequest("account is required");
        limit = Math.Clamp(limit, 1, 500);

        var ammQuery = db.SwapOrders.AsNoTracking().Where(x => x.Maker == account);
        var v3Query  = db.SwapV3Orders.AsNoTracking().Where(x => x.Maker == account);

        if (before.HasValue)
        {
            ammQuery = ammQuery.Where(x => x.UpdatedAtTime < before.Value);
            v3Query  = v3Query.Where(x => x.UpdatedAtTime < before.Value);
        }

        var amm = await ammQuery
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit)
            .Select(x => new PortfolioSwapDto(
                "amm",
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

        var v3 = await v3Query
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit)
            .Select(x => new PortfolioSwapDto(
                "v3",
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

        return Ok(amm.Concat(v3)
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit));
    }

    /// GET /api/portfolio/{account}/limit-orders
    [HttpGet("{account}/limit-orders")]
    public async Task<IActionResult> GetLimitOrders(
        string account,
        [FromQuery] int limit = 100,
        [FromQuery] long? before = null)
    {
        if (string.IsNullOrWhiteSpace(account))
            return BadRequest("account is required");
        limit = Math.Clamp(limit, 1, 500);

        var fillQuery  = db.LimitOrderFills.AsNoTracking().Where(x => x.Owner == account);
        var closeQuery = db.LimitOrderCloses.AsNoTracking().Where(x => x.Owner == account);

        if (before.HasValue)
        {
            fillQuery  = fillQuery.Where(x => x.UpdatedAtTime < before.Value);
            closeQuery = closeQuery.Where(x => x.UpdatedAtTime < before.Value);
        }

        var fills = await fillQuery
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit)
            .Select(x => new LimitOrderDto(
                "fill",
                x.TrxId,
                x.Src,
                x.Mode,
                x.ActionOrdinal,
                x.Owner,
                x.AskContract,
                x.AskCode,
                x.BidContract,
                x.BidCode,
                x.MarketId,
                x.AmountAsk,
                x.AmountBid,
                x.GlobalSequence,
                x.UpdatedAtTime))
            .ToListAsync();

        var closes = await closeQuery
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit)
            .Select(x => new LimitOrderDto(
                "close",
                x.TrxId,
                x.Src,
                x.Mode,
                x.ActionOrdinal,
                x.Owner,
                x.AskContract,
                x.AskCode,
                x.BidContract,
                x.BidCode,
                x.MarketId,
                0,
                0,
                x.GlobalSequence,
                x.UpdatedAtTime))
            .ToListAsync();

        return Ok(fills.Concat(closes)
            .OrderByDescending(x => x.UpdatedAtTime)
            .Take(limit));
    }

    private sealed record PortfolioSwapDto(
        string Type,
        string TrxId,
        string Src,
        string PairId,
        string Account,
        decimal AmountIn,
        decimal AmountOut,
        string CodeIn,
        string CodeOut,
        long UpdatedAtTime);

    private sealed record LimitOrderDto(
        string Type,
        string TrxId,
        string Src,
        string Mode,
        int ActionOrdinal,
        string Owner,
        string AskContract,
        string AskCode,
        string BidContract,
        string BidCode,
        long MarketId,
        decimal AmountAsk,
        decimal AmountBid,
        long GlobalSequence,
        long UpdatedAtTime);
}
