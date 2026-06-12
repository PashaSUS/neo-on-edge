using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Services;

public sealed class NeoRouteQuoteService(AppDbContext db, IOptions<AppSettings> settings, ILogger<NeoRouteQuoteService> log)
{
    // Runtime-configurable caps are provided via `settings.Value` in AppSettings
    private const int MaxSplitLegs = 4;
    private const int MaxSplitCandidatePaths = 8;
    private const int SplitPercentStep = 1;
    private const decimal MaxSplitDirectImprovementMultiplier = 1.75m;
    private const int AlcorTradeLookbackHours = 12;
    private const int MinAlcorTradesPerEdge = 2;
    private const decimal AlcorObservedRateHaircut = 0.90m;
    private const int PreferredSyntheticEdgesPerToken = 2;
    private static readonly string[] PreferredSyntheticSources = ["a2", "a3", "ad"];

    private IReadOnlyList<PoolInfo>? _poolCache;
    private DateTimeOffset _poolCacheExpiresAt = DateTimeOffset.MinValue;

    public Task<NeoRouteResult?> GetRouteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        CancellationToken ct = default)
        => GetRouteAsync(
            input,
            output,
            amount,
            receiver,
            inputPrecision,
            outputPrecision,
            slippage,
            Math.Max(1, settings.Value.NeoRouteMaxHops),
            settings.Value.ContractAccount,
            ct);

    public async Task<NeoRouteResult?> GetRouteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        int maxHops,
        string contractAccount,
        CancellationToken ct = default)
        => (await GetRouteQuoteAsync(input, output, amount, receiver, inputPrecision, outputPrecision, slippage, maxHops, contractAccount, ct)).Route;

    public async Task<NeoRouteQuote> GetRouteQuoteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        int maxHops,
        string contractAccount,
        CancellationToken ct = default)
    {
        string codeIn = ParseCode(input);
        string codeOut = ParseCode(output);

        try
        {
            int neoFeeBps = Math.Clamp(settings.Value.NeoRouteFeeBps, 0, 100);
            int boundedMaxHops = Math.Clamp(maxHops, 1, Math.Max(1, settings.Value.NeoRouteMaxHops));
            decimal amountDecimals = amount / Pow10(inputPrecision);
            var search = await FindCandidatePathsAsync(codeIn, codeOut, amountDecimals, neoFeeBps, boundedMaxHops, 1, input, output, ct);
            var bestPath = search.Paths.FirstOrDefault();

            if (bestPath is null)
                return new NeoRouteQuote(null, search.Reason ?? "No executable NeoOnEdge route found.");

            return new NeoRouteQuote(BuildRouteResult(bestPath, output, receiver, outputPrecision, slippage, contractAccount, neoFeeBps, codeIn, codeOut), null);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Neo route quote failed for {Input}->{Output}", input, output);
            return new NeoRouteQuote(null, "NeoOnEdge route quote failed.");
        }
    }

    public async Task<NeoSplitRouteResult?> GetSplitRouteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        int maxHops,
        string contractAccount,
        CancellationToken ct = default)
        => (await GetSplitRouteQuoteAsync(input, output, amount, receiver, inputPrecision, outputPrecision, slippage, maxHops, contractAccount, ct)).Route;

    public async Task<NeoSplitRouteQuote> GetSplitRouteQuoteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        int maxHops,
        string contractAccount,
        CancellationToken ct = default)
    {
        if (amount <= 1)
            return new NeoSplitRouteQuote(null, "Input amount is too small to split.");

        string codeIn = ParseCode(input);
        string codeOut = ParseCode(output);

        try
        {
            int neoFeeBps = Math.Clamp(settings.Value.NeoRouteFeeBps, 0, 100);
            int boundedMaxHops = Math.Clamp(maxHops, 1, Math.Max(1, settings.Value.NeoRouteMaxHops));
            decimal amountDecimals = amount / Pow10(inputPrecision);
            var search = await FindCandidatePathsAsync(codeIn, codeOut, amountDecimals, neoFeeBps, boundedMaxHops, MaxSplitCandidatePaths, input, output, ct);
            var candidates = search.Paths
                .GroupBy(PathKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(MaxSplitCandidatePaths)
                .ToArray();

            if (candidates.Length < 2)
                return new NeoSplitRouteQuote(null, candidates.Length == 0
                    ? search.Reason ?? "No executable NeoOnEdge route found."
                    : $"Split needs at least 2 executable NeoOnEdge paths; found {candidates.Length}.");

            var bestDirectNetOut = candidates[0].NetOut;

            SplitAllocation? best = null;

            for (int i = 0; i < candidates.Length - 1; i++)
            {
                for (int j = i + 1; j < candidates.Length; j++)
                {
                    if (!PathsAreDisjoint(candidates[i], candidates[j]))
                        continue;

                    for (int firstPercent = SplitPercentStep; firstPercent <= 100 - SplitPercentStep; firstPercent += SplitPercentStep)
                    {
                        var firstRaw = amount * firstPercent / 100;
                        var secondRaw = amount - firstRaw;
                        var allocation = EvaluateSplitAllocation(
                            [candidates[i], candidates[j]],
                            [firstRaw, secondRaw],
                            inputPrecision,
                            neoFeeBps,
                            codeIn);

                        if (allocation is not null && (best is null || allocation.NetOut > best.NetOut))
                            best = allocation;
                    }
                }
            }

            if (MaxSplitLegs >= 3 && candidates.Length >= 3)
            {
                for (int i = 0; i < candidates.Length - 2; i++)
                {
                    for (int j = i + 1; j < candidates.Length - 1; j++)
                    {
                        for (int k = j + 1; k < candidates.Length; k++)
                        {
                            if (!PathsAreDisjoint(candidates[i], candidates[j], candidates[k]))
                                continue;

                            for (int firstPercent = SplitPercentStep; firstPercent <= 100 - (2 * SplitPercentStep); firstPercent += SplitPercentStep)
                            {
                                for (int secondPercent = SplitPercentStep; secondPercent <= 100 - firstPercent - SplitPercentStep; secondPercent += SplitPercentStep)
                                {
                                    var firstRaw = amount * firstPercent / 100;
                                    var secondRaw = amount * secondPercent / 100;
                                    var thirdRaw = amount - firstRaw - secondRaw;
                                    var allocation = EvaluateSplitAllocation(
                                        [candidates[i], candidates[j], candidates[k]],
                                        [firstRaw, secondRaw, thirdRaw],
                                        inputPrecision,
                                        neoFeeBps,
                                        codeIn);

                                    if (allocation is not null && (best is null || allocation.NetOut > best.NetOut))
                                        best = allocation;
                                }
                            }
                        }
                    }
                }
            }

            if (best is null)
                return new NeoSplitRouteQuote(null, "Candidate paths overlap or fail split-size liquidity caps.");

            if (bestDirectNetOut > 0 && best.NetOut > bestDirectNetOut * MaxSplitDirectImprovementMultiplier)
            {
                log.LogWarning(
                    "Rejected suspicious Neo split quote: input={Input} output={Output} splitOut={SplitOut} directOut={DirectOut} multiplier={Multiplier}",
                    input,
                    output,
                    best.NetOut,
                    bestDirectNetOut,
                    best.NetOut / bestDirectNetOut);
                return new NeoSplitRouteQuote(null, "Split quote was rejected by the safety guardrail versus direct NeoOnEdge.");
            }

            return new NeoSplitRouteQuote(BuildSplitRouteResult(best, inputPrecision, outputPrecision, slippage, output, receiver, contractAccount, neoFeeBps, codeIn, codeOut, amount), null);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Neo split route quote failed for {Input}->{Output}", input, output);
            return new NeoSplitRouteQuote(null, "Neo split quote failed.");
        }
    }

    private NeoRouteResult BuildRouteResult(
        PoolPath bestPath,
        string output,
        string receiver,
        int outputPrecision,
        double slippage,
        string contractAccount,
        int neoFeeBps,
        string codeIn,
        string codeOut)
    {
        decimal minReceived = bestPath.NetOut * (1 - (decimal)slippage / 100m);
        decimal estimatedProtocolFee = bestPath.GrossOut - bestPath.NetOut;
        int liquidityProviderFeeBps = bestPath.Pools.Sum(p => (int)DexFeeBps(p.Src));
        string contractOut = ParseContract(output);
        long minRaw = ToRawAmount(minReceived, outputPrecision);
        string memo = BuildMemo(bestPath.Pools, minRaw, codeOut, contractOut);

        var first = bestPath.Pools.First();
        return new NeoRouteResult(
            Src: first.Src,
            PairId: first.PairId,
            Output: FormatTokenAmount(bestPath.NetOut, outputPrecision),
            MinReceived: FormatTokenAmount(minReceived, outputPrecision),
            Memo: memo,
            Receiver: receiver,
            ContractAccount: contractAccount,
            ProtocolFeeBps: neoFeeBps,
            LiquidityProviderFeeBps: liquidityProviderFeeBps,
            TotalFeeBps: liquidityProviderFeeBps + neoFeeBps,
            EstimatedProtocolFee: FormatTokenAmount(estimatedProtocolFee, outputPrecision),
            Steps: BuildRouteSteps(bestPath.Pools, codeIn));
    }

    private async Task<PathSearchResult> FindCandidatePathsAsync(
        string codeIn,
        string codeOut,
        decimal amountDecimals,
        int neoFeeBps,
        int boundedMaxHops,
        int limit,
        string input,
        string output,
        CancellationToken ct)
    {
        var latest = await GetLatestPoolsAsync(ct);
        if (latest.Count == 0)
            return new PathSearchResult(Array.Empty<PoolPath>(), "No recent NeoOnEdge pool snapshots were indexed.");

        var adj = BuildAdjacency(latest);
        if (!adj.ContainsKey(codeIn))
            return new PathSearchResult(Array.Empty<PoolPath>(), $"No indexed NeoOnEdge entry pool for {codeIn}.");

        if (!adj.ContainsKey(codeOut))
            return new PathSearchResult(Array.Empty<PoolPath>(), $"No indexed NeoOnEdge exit pool for {codeOut}.");

        var candidates = new List<PoolPath>();
        int visitedStates = 0;
        int reachedOutputPaths = 0;
        bool capped = false;

        // Ensure any direct single-pool routes are considered even if adjacency pruning
        // would otherwise exclude them. This biases the result toward direct swaps
        // when they produce the best output for the requested amount.
        var directPools = latest
            .Where(p => (p.CodeA.Equals(codeIn, StringComparison.OrdinalIgnoreCase) && p.CodeB.Equals(codeOut, StringComparison.OrdinalIgnoreCase))
                     || (p.CodeB.Equals(codeIn, StringComparison.OrdinalIgnoreCase) && p.CodeA.Equals(codeOut, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var pool in directPools)
        {
            var gross = SimulatePath(new[] { pool }, amountDecimals, codeIn);
            if (gross > 0)
            {
                var net = ApplyProtocolFee(gross, neoFeeBps);
                var path = new PoolPath(new[] { pool }, gross, net);
                // Avoid adding duplicate path keys
                if (!candidates.Any(c => PathKey(c) == PathKey(path)))
                {
                    candidates.Add(path);
                    reachedOutputPaths++;
                    try { log.LogDebug("Evaluated direct path {Path} gross={Gross} net={Net}", string.Join('-', path.Pools.Select(p => p.PairId + "@" + p.Src)), gross, net); } catch { }
                }
            }
        }

        var q = new Queue<PathState>();
        q.Enqueue(new PathState(codeIn, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { codeIn }, new List<PoolInfo>()));

        while (q.Count > 0 && visitedStates < Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
        {
            var state = q.Dequeue();
            visitedStates++;

            if (state.Pools.Count > 0 && state.Pools.Count <= boundedMaxHops && state.Current.Equals(codeOut, StringComparison.OrdinalIgnoreCase))
            {
                reachedOutputPaths++;
                var gross = SimulatePath(state.Pools, amountDecimals, codeIn);
                if (gross > 0)
                {
                    var net = ApplyProtocolFee(gross, neoFeeBps);
                    candidates.Add(new PoolPath(state.Pools.ToArray(), gross, net));
                    try { log.LogDebug("Evaluated path {Path} gross={Gross} net={Net}", string.Join('-', state.Pools.Select(p => p.PairId + "@" + p.Src)), gross, net); } catch { }
                }

                continue;
            }

            if (state.Pools.Count >= boundedMaxHops) continue;
            if (!adj.TryGetValue(state.Current, out var neighbours)) continue;

            foreach (var pool in neighbours)
            {
                if (q.Count >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
                {
                    capped = true;
                    break;
                }

                var next = pool.CodeA.Equals(state.Current, StringComparison.OrdinalIgnoreCase) ? pool.CodeB : pool.CodeA;
                if (state.Visited.Contains(next)) continue;

                var nextVisited = new HashSet<string>(state.Visited, StringComparer.OrdinalIgnoreCase) { next };
                var nextPools = new List<PoolInfo>(state.Pools) { pool };
                q.Enqueue(new PathState(next, nextVisited, nextPools));
            }

            if (capped)
                break;
        }

        if (capped || visitedStates >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
            log.LogWarning("Neo route search capped: input={Input} output={Output} visitedStates={VisitedStates} queuedStates={QueuedStates} maxHops={MaxHops}", input, output, visitedStates, q.Count, boundedMaxHops);

        var paths = candidates
            .OrderByDescending(path => path.NetOut)
            .ThenBy(path => path.Pools.Length)
            .Take(Math.Max(1, limit))
            .ToArray();

        if (paths.Length > 0)
            return new PathSearchResult(paths, null);

        var reason = reachedOutputPaths > 0
            ? "A NeoOnEdge path exists, but this amount failed liquidity or safety caps."
            : capped || visitedStates >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates)
                ? "NeoOnEdge route search was capped before finding a path."
                : $"No NeoOnEdge path found within {boundedMaxHops} hop{(boundedMaxHops == 1 ? "" : "s")}.";

        return new PathSearchResult(paths, reason);
    }

    private Dictionary<string, List<PoolInfo>> BuildAdjacency(IReadOnlyList<PoolInfo> latest)
    {
        var adj = new Dictionary<string, List<PoolInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in latest)
        {
            if (!adj.TryGetValue(p.CodeA, out var listA)) { listA = new List<PoolInfo>(); adj[p.CodeA] = listA; }
            listA.Add(p);
            if (!adj.TryGetValue(p.CodeB, out var listB)) { listB = new List<PoolInfo>(); adj[p.CodeB] = listB; }
            listB.Add(p);
        }

        foreach (var tokenCode in adj.Keys.ToArray())
        {
            adj[tokenCode] = SelectNeighbourPools(adj[tokenCode]);
        }

        return adj;
    }

    private List<PoolInfo> SelectNeighbourPools(IReadOnlyList<PoolInfo> pools)
    {
        int maxPerToken = Math.Max(1, settings.Value.NeoRouteMaxPoolsPerToken);
        if (pools.Count <= maxPerToken)
            return pools
                .OrderByDescending(PoolLiquidity)
                .ToList();

        var ordered = pools
            .OrderByDescending(PoolLiquidity)
            .ToList();

        var selected = new List<PoolInfo>(maxPerToken);

        // Keep a small source-diverse slice so synthetic Alcor edges are not
        // completely starved by very deep high-liquidity AMM neighbour lists.
        foreach (var src in PreferredSyntheticSources)
        {
            foreach (var edge in ordered
                .Where(p => p.Src.Equals(src, StringComparison.OrdinalIgnoreCase))
                .Take(PreferredSyntheticEdgesPerToken))
            {
                if (selected.Count >= maxPerToken)
                    break;

                if (!selected.Contains(edge))
                    selected.Add(edge);
            }

            if (selected.Count >= maxPerToken)
                break;
        }

        foreach (var pool in ordered)
        {
            if (selected.Count >= maxPerToken)
                break;

            if (!selected.Contains(pool))
                selected.Add(pool);
        }

        return selected;
    }

    private SplitAllocation? EvaluateSplitAllocation(
        IReadOnlyList<PoolPath> paths,
        IReadOnlyList<long> rawAmounts,
        int inputPrecision,
        int neoFeeBps,
        string codeIn)
    {
        if (paths.Count != rawAmounts.Count)
            return null;

        var legs = new List<SplitLegAllocation>(paths.Count);
        var scale = Pow10(inputPrecision);
        for (int i = 0; i < paths.Count; i++)
        {
            var rawAmount = rawAmounts[i];
            if (rawAmount <= 0)
                return null;

            var gross = SimulatePath(paths[i].Pools, rawAmount / scale, codeIn);
            if (gross <= 0)
                return null;

            var net = ApplyProtocolFee(gross, neoFeeBps);
            if (net <= 0)
                return null;

            legs.Add(new SplitLegAllocation(paths[i], rawAmount, gross, net));
        }

        return legs.Count >= 2
            ? new SplitAllocation(legs, legs.Sum(leg => leg.NetOut))
            : null;
    }

    private static NeoSplitRouteResult BuildSplitRouteResult(
        SplitAllocation allocation,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        string output,
        string receiver,
        string contractAccount,
        int neoFeeBps,
        string codeIn,
        string codeOut,
        long totalInputRaw)
    {
        var slippageMultiplier = 1 - (decimal)slippage / 100m;
        var contractOut = ParseContract(output);
        var legs = allocation.Legs
            .Select((leg, index) => BuildSplitRouteLeg(leg, index, inputPrecision, outputPrecision, slippageMultiplier, receiver, contractAccount, neoFeeBps, codeIn, codeOut, contractOut))
            .ToArray();
        var minReceived = allocation.NetOut * slippageMultiplier;

        return new NeoSplitRouteResult(
            Output: FormatTokenAmount(allocation.NetOut, outputPrecision),
            MinReceived: FormatTokenAmount(minReceived, outputPrecision),
            Receiver: receiver,
            Legs: legs,
            TotalFeeBps: EstimateWeightedFeeBps(allocation.Legs, totalInputRaw, neoFeeBps),
            Execution: "multi-action");
    }

    private static NeoSplitRouteLeg BuildSplitRouteLeg(
        SplitLegAllocation leg,
        int index,
        int inputPrecision,
        int outputPrecision,
        decimal slippageMultiplier,
        string receiver,
        string contractAccount,
        int neoFeeBps,
        string codeIn,
        string codeOut,
        string contractOut)
    {
        var minReceived = leg.NetOut * slippageMultiplier;
        var minRaw = ToRawAmount(minReceived, outputPrecision);
        var liquidityProviderFeeBps = leg.Path.Pools.Sum(pool => (int)DexFeeBps(pool.Src));

        return new NeoSplitRouteLeg(
            Source: "neo",
            Label: BuildSplitLegLabel(leg.Path, index),
            Contract: contractAccount,
            Receiver: receiver,
            Memo: BuildMemo(leg.Path.Pools, minRaw, codeOut, contractOut),
            InputRaw: leg.InputRaw,
            InputAmount: FormatTokenAmount(leg.InputRaw / Pow10(inputPrecision), inputPrecision),
            Output: FormatTokenAmount(leg.NetOut, outputPrecision),
            MinReceived: FormatTokenAmount(minReceived, outputPrecision),
            Hops: Math.Max(1, leg.Path.Pools.Length),
            Steps: BuildRouteSteps(leg.Path.Pools, codeIn),
            ProtocolFeeBps: neoFeeBps,
            LiquidityProviderFeeBps: liquidityProviderFeeBps,
            TotalFeeBps: liquidityProviderFeeBps + neoFeeBps,
            EstimatedProtocolFee: FormatTokenAmount(leg.GrossOut - leg.NetOut, outputPrecision));
    }

    private async Task<IReadOnlyList<PoolInfo>> GetLatestPoolsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_poolCache is not null && now < _poolCacheExpiresAt)
            return _poolCache;

        long since = now.AddDays(-7).ToUnixTimeMilliseconds();

        var baseQuery = db.SwapOrders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            // Exclude Alcor snapshot sources here (they are handled from SwapV3Orders)
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
            select new PoolInfo(
                row.Src,
                row.PairId,
                row.CodeReserveA.ToUpper(),
                row.CodeReserveB.ToUpper(),
                row.AmountReserveA,
                row.AmountReserveB))
            .ToListAsync(ct);

        var alcorEdges = await GetRecentAlcorEdgesAsync(now, ct);
        var alcorMarketEdges = await GetRecentAlcorMarketEdgesAsync(now, ct);

        var latestAmmPools = snapshots
            .Where(p => IsPoolSane(p))
            .GroupBy(p => new { p.Src, p.PairId })
            .Select(g => g.OrderByDescending(PoolLiquidity).First())
            .ToArray();

        _poolCache = latestAmmPools.Concat(alcorEdges).Concat(alcorMarketEdges).ToArray();
        _poolCacheExpiresAt = now.AddSeconds(15);

        log.LogDebug("Loaded {PoolCount} Neo route pools from {SnapshotCount} latest snapshots", _poolCache.Count, snapshots.Count);
        return _poolCache;
    }

    private async Task<IReadOnlyList<PoolInfo>> GetRecentAlcorEdgesAsync(DateTimeOffset now, CancellationToken ct)
    {
        long since = now.AddHours(-AlcorTradeLookbackHours).ToUnixTimeMilliseconds();

        var rows = await db.SwapV3Orders.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .Where(x => x.Src == "a2" || x.Src == "a3")
            .Where(x => x.AmountIn > 0 && x.AmountOut > 0)
            .Where(x => x.CodeIn != "" && x.CodeOut != "" && x.CodeIn != x.CodeOut)
            .GroupBy(x => new
            {
                x.PairId,
                Src = x.Src,
                CodeIn = x.CodeIn.ToUpper(),
                CodeOut = x.CodeOut.ToUpper()
            })
            .Select(g => new
            {
                g.Key.PairId,
                g.Key.Src,
                g.Key.CodeIn,
                g.Key.CodeOut,
                TradeCount = g.Count(),
                VolumeIn = g.Sum(x => x.AmountIn),
                VolumeOut = g.Sum(x => x.AmountOut),
                LastTrade = g.Max(x => x.UpdatedAtTime)
            })
            .Where(x => x.TradeCount >= MinAlcorTradesPerEdge && x.VolumeIn > 0 && x.VolumeOut > 0)
            .ToListAsync(ct);

        return rows
            .Select(row =>
            {
                var rate = row.VolumeOut / row.VolumeIn * AlcorObservedRateHaircut;
                var maxInput = Math.Min(row.VolumeIn * settings.Value.AlcorMaxEdgeVolumeShare, row.VolumeIn / row.TradeCount * settings.Value.AlcorMaxAverageTradeMultiple);
                return new PoolInfo(
                    Src: row.Src,
                    PairId: row.PairId,
                    CodeA: row.CodeIn,
                    CodeB: row.CodeOut,
                    ReserveA: 0,
                    ReserveB: 0,
                    RateAtoB: rate,
                    RateBtoA: null,
                    MaxInputA: maxInput,
                    MaxInputB: 0,
                    LiquidityScore: row.VolumeIn);
            })
            .Where(IsPoolSane)
            .ToArray();
    }

    private async Task<IReadOnlyList<PoolInfo>> GetRecentAlcorMarketEdgesAsync(DateTimeOffset now, CancellationToken ct)
    {
        long since = now.AddHours(-AlcorTradeLookbackHours).ToUnixTimeMilliseconds();

        var rows = await db.MarketMatches.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since)
            .Where(x => x.Src == "ad")
            .Where(x => x.AmountAsk > 0 && x.AmountBid > 0)
            .Where(x => x.CodeAsk != "" && x.CodeBid != "" && x.CodeAsk != x.CodeBid)
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
                TradeCount = g.Count(),
                VolumeAsk = g.Sum(x => x.AmountAsk),
                VolumeBid = g.Sum(x => x.AmountBid),
                LastTrade = g.Max(x => x.UpdatedAtTime)
            })
            .Where(x => x.TradeCount >= MinAlcorTradesPerEdge && x.VolumeAsk > 0 && x.VolumeBid > 0)
            .ToListAsync(ct);

        return rows
            .Select(row =>
            {
                var rate = row.VolumeBid / row.VolumeAsk * AlcorObservedRateHaircut;
                var maxInput = Math.Min(row.VolumeAsk * settings.Value.AlcorMaxEdgeVolumeShare, row.VolumeAsk / row.TradeCount * settings.Value.AlcorMaxAverageTradeMultiple);
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
                    LiquidityScore: row.VolumeAsk);
            })
            .Where(IsPoolSane)
            .ToArray();
    }

    private static string ParseCode(string alcorId)
    {
        int dash = alcorId.IndexOf('-');
        return dash > 0 ? alcorId[..dash].ToUpperInvariant() : alcorId.ToUpperInvariant();
    }

    private static string ParseContract(string alcorId)
    {
        int dash = alcorId.IndexOf('-');
        return dash > 0 ? alcorId[(dash + 1)..] : "eosio.token";
    }

    private static decimal Pow10(int precision)
    {
        precision = Math.Clamp(precision, 0, 18);
        decimal result = 1m;
        for (int i = 0; i < precision; i++)
            result *= 10m;
        return result;
    }

    private static string FormatTokenAmount(decimal amount, int precision)
        => amount.ToString($"F{Math.Clamp(precision, 0, 18)}", System.Globalization.CultureInfo.InvariantCulture);

    private static long ToRawAmount(decimal amount, int precision)
    {
        if (amount <= 0)
            return 0;

        return (long)Math.Floor(amount * Pow10(precision));
    }

    private static decimal ApplyProtocolFee(decimal amount, int protocolFeeBps)
        => amount * (10_000m - protocolFeeBps) / 10_000m;

    private decimal SimulatePath(IReadOnlyList<PoolInfo> pools, decimal amountIn, string codeIn)
    {
        decimal current = amountIn;
        string currentCode = codeIn;

        foreach (var pool in pools)
        {
            decimal reserveIn, reserveOut;
            if (pool.CodeA.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
            {
                reserveIn = pool.ReserveA;
                reserveOut = pool.ReserveB;
                currentCode = pool.CodeB;
                if (pool.RateAtoB is { } rateAtoB)
                {
                    if (current > pool.MaxInputA) return 0m;
                    current *= rateAtoB;
                    continue;
                }
            }
            else if (pool.CodeB.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
            {
                reserveIn = pool.ReserveB;
                reserveOut = pool.ReserveA;
                currentCode = pool.CodeA;
                if (pool.RateBtoA is { } rateBtoA)
                {
                    if (current > pool.MaxInputB) return 0m;
                    current *= rateBtoA;
                    continue;
                }
            }
            else
            {
                return 0m;
            }

            if (current <= 0 || reserveIn <= 0 || reserveOut <= 0) return 0m;
            var dexFeeBps = DexFeeBps(pool.Src);
            var afterDex = current * (10_000m - dexFeeBps) / 10_000m;
            var grossOut = reserveOut * afterDex / (reserveIn + afterDex);
            if (grossOut <= 0 || grossOut > reserveOut * settings.Value.NeoRouteMaxPoolOutputReserveShare) return 0m;
            current = grossOut;
        }

        return current;
    }

    private static decimal DexFeeBps(string src) => src switch
    {
        "d" => 30m,
        "t" => 30m,
        "n" => 30m,
        "a" => 30m,
        "a2" => 30m,
        "a3" => 30m,
        "ad" => 30m,
        _ => 50m,
    };

    private static decimal PoolLiquidity(PoolInfo pool)
        => pool.LiquidityScore > 0 ? pool.LiquidityScore : pool.ReserveA + pool.ReserveB;

    private static string BuildMemo(IReadOnlyList<PoolInfo> pools, long minRaw, string codeOut, string contractOut)
    {
        var routeParts = pools.Select(pool => $"{pool.PairId}@{CanonicalRouteSource(pool.Src)}");
        return $"swap,{minRaw},{codeOut}@{contractOut}|{string.Join('-', routeParts)}";
    }

    private static int EstimateWeightedFeeBps(IReadOnlyList<SplitLegAllocation> legs, long totalInputRaw, int neoFeeBps)
    {
        if (totalInputRaw <= 0)
            return 0;

        decimal weighted = 0;
        foreach (var leg in legs)
        {
            var legFeeBps = leg.Path.Pools.Sum(pool => (int)DexFeeBps(pool.Src)) + neoFeeBps;
            weighted += (decimal)leg.InputRaw / totalInputRaw * legFeeBps;
        }

        return (int)Math.Round(weighted, MidpointRounding.AwayFromZero);
    }

    private static bool PathsAreDisjoint(params PoolPath[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in paths.SelectMany(path => path.Pools))
        {
            if (!seen.Add(PoolKey(pool)))
                return false;
        }

        return true;
    }

    private static string PathKey(PoolPath path)
        => string.Join('-', path.Pools.Select(PoolKey));

    private static string PoolKey(PoolInfo pool)
        => $"{CanonicalRouteSource(pool.Src)}:{pool.PairId}";

    private static string CanonicalRouteSource(string src)
        => src.Equals("a3", StringComparison.OrdinalIgnoreCase) ? "a2" : src;

    private static string BuildSplitLegLabel(PoolPath path, int index)
    {
        var labels = path.Pools
            .Select(pool => SourceLabel(pool.Src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length > 0
            ? string.Join(" + ", labels)
            : $"Neo leg {index + 1}";
    }

    private static string SourceLabel(string src) => src switch
    {
        "a2" => "Alcor v2",
        "a3" => "Alcor v3",
        "ad" => "Alcor DEX",
        "d" => "Defibox",
        "t" => "Taco",
        "a" => "ADEX",
        "n" => "NeftyBlocks",
        "wf" => "WaxFusion",
        _ => src.ToUpperInvariant()
    };

    private static IReadOnlyList<NeoRouteStep> BuildRouteSteps(IReadOnlyList<PoolInfo> pools, string codeIn)
    {
        var steps = new List<NeoRouteStep>(pools.Count);
        var currentCode = codeIn;

        foreach (var pool in pools)
        {
            string nextCode;
            if (pool.CodeA.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
            {
                nextCode = pool.CodeB;
            }
            else if (pool.CodeB.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
            {
                nextCode = pool.CodeA;
            }
            else
            {
                nextCode = pool.CodeA;
            }

            steps.Add(new NeoRouteStep(pool.Src, pool.PairId, currentCode, nextCode));
            currentCode = nextCode;
        }

        return steps;
    }

    private static bool IsPoolSane(PoolInfo p)
    {
        try
        {
            // basic sanity bounds to avoid malformed or mis-parsed reserves
            const decimal MaxReserve = 1_000_000_000_000m; // 1e12
            const decimal MinReserve = 0.000000000001m; // 1e-12

            if (p.RateAtoB is not null || p.RateBtoA is not null)
            {
                if (!(p.Src.Equals("a2", StringComparison.OrdinalIgnoreCase) ||
                      p.Src.Equals("a3", StringComparison.OrdinalIgnoreCase) ||
                      p.Src.Equals("ad", StringComparison.OrdinalIgnoreCase))) return false;
                if (string.IsNullOrWhiteSpace(p.PairId) || string.IsNullOrWhiteSpace(p.CodeA) || string.IsNullOrWhiteSpace(p.CodeB)) return false;
                if (p.RateAtoB is { } rateAtoB && (rateAtoB <= 0 || rateAtoB < 0.000001m || rateAtoB > 1_000_000m || p.MaxInputA <= 0)) return false;
                if (p.RateBtoA is { } rateBtoA && (rateBtoA <= 0 || rateBtoA < 0.000001m || rateBtoA > 1_000_000m || p.MaxInputB <= 0)) return false;
                return true;
            }

            if (p.ReserveA <= 0 || p.ReserveB <= 0) return false;
            if (p.ReserveA < MinReserve || p.ReserveB < MinReserve) return false;
            if (p.ReserveA > MaxReserve || p.ReserveB > MaxReserve) return false;

            // implied price should be within sane bounds
            var price = p.ReserveB / p.ReserveA;
            if (price <= 0 || price < 0.000001m || price > 1_000_000m) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record PoolInfo(
        string Src,
        string PairId,
        string CodeA,
        string CodeB,
        decimal ReserveA,
        decimal ReserveB,
        decimal? RateAtoB = null,
        decimal? RateBtoA = null,
        decimal MaxInputA = 0,
        decimal MaxInputB = 0,
        decimal LiquidityScore = 0);

    private sealed record PathSearchResult(IReadOnlyList<PoolPath> Paths, string? Reason);

    private sealed record PoolPath(PoolInfo[] Pools, decimal GrossOut, decimal NetOut);

    private sealed record SplitAllocation(IReadOnlyList<SplitLegAllocation> Legs, decimal NetOut);

    private sealed record SplitLegAllocation(PoolPath Path, long InputRaw, decimal GrossOut, decimal NetOut);

    private sealed class PathState
    {
        public string Current { get; }
        public HashSet<string> Visited { get; }
        public List<PoolInfo> Pools { get; }

        public PathState(string current, HashSet<string> visited, List<PoolInfo> pools)
        {
            Current = current;
            Visited = visited;
            Pools = pools;
        }
    }
}

public sealed record NeoRouteQuote(NeoRouteResult? Route, string? Reason);

public sealed record NeoSplitRouteQuote(NeoSplitRouteResult? Route, string? Reason);

public sealed record NeoRouteResult(
    string Src,
    string PairId,
    string Output,
    string MinReceived,
    string Memo,
    string Receiver,
    string ContractAccount,
    int ProtocolFeeBps,
    int LiquidityProviderFeeBps,
    int TotalFeeBps,
    string EstimatedProtocolFee,
    IReadOnlyList<NeoRouteStep> Steps);

public sealed record NeoSplitRouteResult(
    string Output,
    string MinReceived,
    string Receiver,
    IReadOnlyList<NeoSplitRouteLeg> Legs,
    int TotalFeeBps,
    string Execution);

public sealed record NeoSplitRouteLeg(
    string Source,
    string Label,
    string Contract,
    string Receiver,
    string Memo,
    long InputRaw,
    string InputAmount,
    string Output,
    string MinReceived,
    int Hops,
    IReadOnlyList<NeoRouteStep> Steps,
    int ProtocolFeeBps,
    int LiquidityProviderFeeBps,
    int TotalFeeBps,
    string EstimatedProtocolFee);

public sealed record NeoRouteStep(
    string Src,
    string PairId,
    string FromSymbol,
    string ToSymbol);