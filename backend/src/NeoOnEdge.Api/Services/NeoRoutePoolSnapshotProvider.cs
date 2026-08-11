using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Services;

public sealed class NeoRoutePoolSnapshotProvider(
    AppDbContext db,
    IOptions<AppSettings> settings,
    ILogger<NeoRoutePoolSnapshotProvider> log)
{
    private const int AlcorTradeLookbackHours = 12;
    private const int MinAlcorTradesPerEdge = 2;

    private IReadOnlyList<PoolInfo>? poolCache;
    private DateTimeOffset poolCacheExpiresAt = DateTimeOffset.MinValue;

    internal async Task<IReadOnlyList<PoolInfo>> GetLatestPoolsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (poolCache is not null && now < poolCacheExpiresAt)
            return poolCache;

        long since = now.AddDays(-7).ToUnixTimeMilliseconds();

        var baseQuery = db.SwapOrders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .Where(x => x.Src != "a2" && x.Src != "ad" && x.Src != "a3")
            .Where(x => x.AmountReserveA > 0 && x.AmountReserveB > 0);

        var latestPerPool = baseQuery
            .GroupBy(x => new { x.Src, x.PairId })
            .Select(g => new
            {
                g.Key.Src,
                g.Key.PairId,
                UpdatedAtTime = g.Max(x => x.UpdatedAtTime)
            });

        var snapshots = await (
            from row in baseQuery
            join latest in latestPerPool
                on new { row.Src, row.PairId, row.UpdatedAtTime }
                equals new { latest.Src, latest.PairId, latest.UpdatedAtTime }
            select new
            {
                row.Src,
                row.PairId,
                CodeReserveA = row.CodeReserveA.ToUpper(),
                CodeReserveB = row.CodeReserveB.ToUpper(),
                row.AmountReserveA,
                row.AmountReserveB
            })
            .ToListAsync(ct);

        var latestPerListing = db.ListingEvents.AsNoTracking()
            .Where(x => x.ContractA != "" && x.ContractB != "" && x.CodeA != "" && x.CodeB != "")
            .GroupBy(x => new { x.Src, x.PairId })
            .Select(g => new
            {
                g.Key.Src,
                g.Key.PairId,
                UpdatedAtTime = g.Max(x => x.UpdatedAtTime)
            });

        var listingContracts = (await (
            from row in db.ListingEvents.AsNoTracking()
            join latest in latestPerListing
                on new { row.Src, row.PairId, row.UpdatedAtTime }
                equals new { latest.Src, latest.PairId, latest.UpdatedAtTime }
            select new
            {
                row.Src,
                row.PairId,
                CodeA = row.CodeA.ToUpper(),
                CodeB = row.CodeB.ToUpper(),
                row.ContractA,
                row.ContractB
            })
            .ToListAsync(ct))
            .ToDictionary(
                x => NeoRouteQuoteSupport.PoolDictionaryKey(x.Src, x.PairId),
                StringComparer.OrdinalIgnoreCase);

        var alcorMarketEdges = await GetRecentAlcorMarketEdgesAsync(now, ct);

        var latestAmmPools = snapshots
            .Select(snapshot =>
            {
                listingContracts.TryGetValue(NeoRouteQuoteSupport.PoolDictionaryKey(snapshot.Src, snapshot.PairId), out var listing);
                var tokenIds = NeoRouteQuoteSupport.ResolvePoolTokenIds(
                    snapshot.CodeReserveA,
                    snapshot.CodeReserveB,
                    listing?.CodeA,
                    listing?.CodeB,
                    listing?.ContractA,
                    listing?.ContractB);

                return new PoolInfo(
                    snapshot.Src,
                    snapshot.PairId,
                    snapshot.CodeReserveA,
                    snapshot.CodeReserveB,
                    snapshot.AmountReserveA,
                    snapshot.AmountReserveB,
                    TokenIdA: tokenIds.TokenIdA,
                    TokenIdB: tokenIds.TokenIdB);
            })
            .Where(NeoRouteQuoteSupport.IsPoolSane)
            .GroupBy(pool => new { pool.Src, pool.PairId })
            .Select(group => group.OrderByDescending(NeoRouteQuoteSupport.PoolLiquidity).First())
            .ToArray();

        var ammPoolsWithTokenIds = latestAmmPools.Count(pool =>
            !string.IsNullOrWhiteSpace(pool.TokenIdA) &&
            !string.IsNullOrWhiteSpace(pool.TokenIdB));

        poolCache = latestAmmPools.Concat(alcorMarketEdges).ToArray();
        poolCacheExpiresAt = now.AddSeconds(15);

        log.LogInformation(
            "Loaded Neo route pool snapshot: totalPools={PoolCount}, ammPools={AmmPoolCount}, ammPoolsWithTokenIds={AmmPoolsWithTokenIds}, ammPoolsMissingTokenIds={AmmPoolsMissingTokenIds}, rawSnapshots={SnapshotCount}, alcorMarketEdges={AlcorMarketEdges}, cacheSeconds={CacheSeconds}",
            poolCache.Count,
            latestAmmPools.Length,
            ammPoolsWithTokenIds,
            latestAmmPools.Length - ammPoolsWithTokenIds,
            snapshots.Count,
            alcorMarketEdges.Count,
            15);
        return poolCache;
    }

    private async Task<IReadOnlyList<PoolInfo>> GetRecentAlcorMarketEdgesAsync(DateTimeOffset now, CancellationToken ct)
    {
        long since = now.AddHours(-AlcorTradeLookbackHours).ToUnixTimeMilliseconds();

        var groupedRows = await db.MarketMatches.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .Where(x => x.Src == "ad")
            .Where(x => x.AmountAsk > 0 && x.AmountBid > 0)
            .Where(x => x.CodeAsk != "" && x.CodeBid != "" && x.CodeAsk != x.CodeBid)
            .Where(x => x.MarketContractBaseToken != "" && x.MarketContractQuoteToken != "")
            .Where(x => x.MarketCodeBaseToken != "" && x.MarketCodeQuoteToken != "")
            .GroupBy(x => new
            {
                x.MarketId,
                CodeAsk = x.CodeAsk.ToUpper(),
                CodeBid = x.CodeBid.ToUpper()
            })
            .Select(g => new
            {
                MarketId = g.Key.MarketId,
                CodeAsk = g.Key.CodeAsk,
                CodeBid = g.Key.CodeBid,
                MarketContractBaseToken = g.Max(x => x.MarketContractBaseToken),
                MarketContractQuoteToken = g.Max(x => x.MarketContractQuoteToken),
                MarketCodeBaseToken = g.Max(x => x.MarketCodeBaseToken.ToUpper()),
                MarketCodeQuoteToken = g.Max(x => x.MarketCodeQuoteToken.ToUpper()),
                TradeCount = g.Count(),
                VolumeAsk = g.Sum(x => x.AmountAsk),
                VolumeBid = g.Sum(x => x.AmountBid),
                LastTrade = g.Max(x => x.UpdatedAtTime)
            })
            .ToListAsync(ct);

        var rows = groupedRows
            .Where(x => x.TradeCount >= MinAlcorTradesPerEdge && x.VolumeAsk > 0 && x.VolumeBid > 0)
            .ToArray();

        var edges = rows
            .Select(row =>
            {
                var rate = row.VolumeBid / row.VolumeAsk * 0.90m;
                var maxInput = Math.Min(row.VolumeAsk * settings.Value.AlcorMaxEdgeVolumeShare, row.VolumeAsk / row.TradeCount * settings.Value.AlcorMaxAverageTradeMultiple);
                var tokenIds = NeoRouteQuoteSupport.ResolvePoolTokenIds(
                    row.CodeAsk,
                    row.CodeBid,
                    row.MarketCodeBaseToken,
                    row.MarketCodeQuoteToken,
                    row.MarketContractBaseToken,
                    row.MarketContractQuoteToken);
                return new PoolInfo(
                    Src: "ad",
                    PairId: row.MarketId.ToString(),
                    CodeA: row.CodeAsk,
                    CodeB: row.CodeBid,
                    ReserveA: 0,
                    ReserveB: 0,
                    RateAtoB: rate,
                    RateBtoA: null,
                    MaxInputA: maxInput,
                    MaxInputB: 0,
                    LiquidityScore: row.VolumeAsk,
                    TokenIdA: tokenIds.TokenIdA,
                    TokenIdB: tokenIds.TokenIdB);
            })
            .Where(pool => !string.IsNullOrWhiteSpace(pool.TokenIdA) && !string.IsNullOrWhiteSpace(pool.TokenIdB))
            .Where(NeoRouteQuoteSupport.IsPoolSane)
            .ToArray();

        log.LogInformation(
            "Loaded Alcor market edge datapoints: groupedEdges={GroupedEdgeCount}, acceptedEdges={AcceptedEdgeCount}, insufficientTradeEdges={InsufficientTradeEdges}, lookbackHours={LookbackHours}, minTradesPerEdge={MinTradesPerEdge}",
            groupedRows.Count,
            edges.Length,
            groupedRows.Count - rows.Length,
            AlcorTradeLookbackHours,
            MinAlcorTradesPerEdge);

        return edges;
    }
}
