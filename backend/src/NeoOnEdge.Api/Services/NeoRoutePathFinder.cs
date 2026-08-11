using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Services;

public sealed class NeoRoutePathFinder(
    AppDbContext db,
    AlcorRouterService alcorRouter,
    NeoRoutePoolSnapshotProvider poolSnapshotProvider,
    NeoRouteQuoteSimulator simulator,
    IOptions<AppSettings> settings,
    ILogger<NeoRoutePathFinder> log)
{
    private static readonly string[] PreferredSyntheticSources = ["ad"];
    private const int PreferredSyntheticEdgesPerToken = 2;

    internal async Task<PathSearchResult> FindCandidatePathsAsync(
        string codeIn,
        string codeOut,
        long rawAmount,
        int inputPrecision,
        decimal amountDecimals,
        int neoFeeBps,
        int boundedMaxHops,
        int limit,
        string input,
        string output,
        double slippage,
        string contractAccount,
        CancellationToken ct,
        bool readAlcorCache)
    {
        var excludedRows = await db.RouteExcludedTokens.AsNoTracking()
            .Select(x => new { x.Contract, x.Symbol })
            .ToListAsync(ct);
        var excludedTokenKeys = excludedRows
            .Select(x => NeoRouteQuoteSupport.TokenKey(x.Contract, x.Symbol))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (excludedTokenKeys.Contains(NeoRouteQuoteSupport.TokenKey(NeoRouteQuoteSupport.ParseContract(input), codeIn)) ||
            excludedTokenKeys.Contains(NeoRouteQuoteSupport.TokenKey(NeoRouteQuoteSupport.ParseContract(output), codeOut)))
            return new PathSearchResult(Array.Empty<PoolPath>(), "This token is disabled for routing because it charges the exchange account transfer fees.");

        var liveAlcorPath = await GetLiveAlcorPathAsync(
            input,
            output,
            rawAmount,
            inputPrecision,
            neoFeeBps,
            boundedMaxHops,
            slippage,
            contractAccount,
            excludedTokenKeys,
            ct,
            readAlcorCache);

        var latest = (await poolSnapshotProvider.GetLatestPoolsAsync(ct))
            .Where(pool => !IsExcludedPoolToken(pool.CodeA, pool.TokenIdA, excludedTokenKeys))
            .Where(pool => !IsExcludedPoolToken(pool.CodeB, pool.TokenIdB, excludedTokenKeys))
            .ToArray();
        if (latest.Length == 0 && liveAlcorPath is null)
            return new PathSearchResult(Array.Empty<PoolPath>(), "No recent NeoOnEdge pool snapshots were indexed.");

        var adjacency = BuildAdjacency(latest);
        if (!adjacency.ContainsKey(codeIn) && liveAlcorPath is null)
            return new PathSearchResult(Array.Empty<PoolPath>(), $"No indexed NeoOnEdge entry pool for {codeIn}.");

        if (!adjacency.ContainsKey(codeOut) && liveAlcorPath is null)
            return new PathSearchResult(Array.Empty<PoolPath>(), $"No indexed NeoOnEdge exit pool for {codeOut}.");

        var candidates = new List<PoolPath>();
        if (liveAlcorPath is not null)
            candidates.Add(liveAlcorPath);

        int visitedStates = 0;
        int reachedOutputPaths = 0;
        int tokenMetadataRejects = 0;
        int tokenContractRejects = 0;
        var tokenRejectSamples = new List<string>(6);
        bool capped = false;

        void RecordTokenValidationReject(PoolPath path, PathTokenValidationResult validation, bool direct)
        {
            if (validation.Reason?.Contains("metadata", StringComparison.OrdinalIgnoreCase) == true)
                tokenMetadataRejects++;
            else
                tokenContractRejects++;

            if (tokenRejectSamples.Count >= 6)
                return;

            var kind = direct ? "direct" : "path";
            var pool = string.IsNullOrWhiteSpace(validation.PoolKey) ? "end" : validation.PoolKey;
            tokenRejectSamples.Add(
                $"{kind} {NeoRouteQuoteSupport.PathKey(path)} at {pool}: {validation.Reason} current={validation.CurrentTokenId ?? "?"} poolTokens={validation.TokenIdA ?? "?"}/{validation.TokenIdB ?? "?"}");
        }

        var directPools = latest
            .Where(pool => (pool.CodeA.Equals(codeIn, StringComparison.OrdinalIgnoreCase) && pool.CodeB.Equals(codeOut, StringComparison.OrdinalIgnoreCase))
                        || (pool.CodeB.Equals(codeIn, StringComparison.OrdinalIgnoreCase) && pool.CodeA.Equals(codeOut, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var pool in directPools)
        {
            var gross = simulator.SimulatePath([pool], amountDecimals, codeIn);
            if (gross <= 0)
                continue;

            var net = NeoRouteQuoteSupport.ApplyProtocolFee(gross, neoFeeBps);
            var path = new PoolPath([pool], gross, net);
            var tokenValidation = NeoRouteQuoteSupport.ValidatePathTokenIds(path.Pools, input, output);
            if (!tokenValidation.IsValid)
            {
                RecordTokenValidationReject(path, tokenValidation, direct: true);
                continue;
            }

            if (candidates.Any(candidate => NeoRouteQuoteSupport.PathKey(candidate) == NeoRouteQuoteSupport.PathKey(path)))
                continue;

            candidates.Add(path);
            reachedOutputPaths++;
            try
            {
                log.LogDebug("Evaluated direct path {Path} gross={Gross} net={Net}", string.Join('-', path.Pools.Select(p => p.PairId + "@" + p.Src)), gross, net);
            }
            catch
            {
            }
        }

        var queue = new Queue<PathState>();
        queue.Enqueue(new PathState(codeIn, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { codeIn }, []));

        while (queue.Count > 0 && visitedStates < Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
        {
            var state = queue.Dequeue();
            visitedStates++;

            if (state.Pools.Count > 0 && state.Pools.Count <= boundedMaxHops && state.Current.Equals(codeOut, StringComparison.OrdinalIgnoreCase))
            {
                reachedOutputPaths++;
                var gross = simulator.SimulatePath(state.Pools, amountDecimals, codeIn);
                if (gross > 0)
                {
                    var net = NeoRouteQuoteSupport.ApplyProtocolFee(gross, neoFeeBps);
                    var path = new PoolPath(state.Pools.ToArray(), gross, net);
                    var tokenValidation = NeoRouteQuoteSupport.ValidatePathTokenIds(path.Pools, input, output);
                    if (!tokenValidation.IsValid)
                    {
                        RecordTokenValidationReject(path, tokenValidation, direct: false);
                    }
                    else
                    {
                        candidates.Add(path);
                        try
                        {
                            log.LogDebug("Evaluated path {Path} gross={Gross} net={Net}", string.Join('-', state.Pools.Select(p => p.PairId + "@" + p.Src)), gross, net);
                        }
                        catch
                        {
                        }
                    }
                }

                continue;
            }

            if (state.Pools.Count >= boundedMaxHops)
                continue;
            if (!adjacency.TryGetValue(state.Current, out var neighbours))
                continue;

            foreach (var pool in neighbours)
            {
                if (queue.Count >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
                {
                    capped = true;
                    break;
                }

                var next = pool.CodeA.Equals(state.Current, StringComparison.OrdinalIgnoreCase) ? pool.CodeB : pool.CodeA;
                if (state.Visited.Contains(next))
                    continue;

                var nextVisited = new HashSet<string>(state.Visited, StringComparer.OrdinalIgnoreCase) { next };
                var nextPools = new List<PoolInfo>(state.Pools) { pool };
                queue.Enqueue(new PathState(next, nextVisited, nextPools));
            }

            if (capped)
                break;
        }

        if (capped || visitedStates >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates))
        {
            log.LogWarning(
                "Neo route search capped: input={Input} output={Output} visitedStates={VisitedStates} queuedStates={QueuedStates} maxHops={MaxHops}",
                input,
                output,
                visitedStates,
                queue.Count,
                boundedMaxHops);
        }

        var paths = candidates
            .OrderByDescending(path => path.NetOut)
            .ThenBy(path => path.Pools.Length)
            .Take(Math.Max(1, limit))
            .ToArray();

        var tokenValidationRejects = tokenMetadataRejects + tokenContractRejects;
        if (tokenValidationRejects > 0)
        {
            if (paths.Length == 0)
            {
                log.LogInformation(
                    "Neo route token-contract validation rejected all simulated candidate paths: input={Input} output={Output} rejected={Rejected}, missingMetadata={MissingMetadata}, contractMismatch={ContractMismatch}, samples={Samples}",
                    input,
                    output,
                    tokenValidationRejects,
                    tokenMetadataRejects,
                    tokenContractRejects,
                    string.Join(" | ", tokenRejectSamples));
            }
            else
            {
                log.LogDebug(
                    "Neo route token-contract validation rejected candidate paths: input={Input} output={Output} rejected={Rejected}, missingMetadata={MissingMetadata}, contractMismatch={ContractMismatch}, accepted={Accepted}, samples={Samples}",
                    input,
                    output,
                    tokenValidationRejects,
                    tokenMetadataRejects,
                    tokenContractRejects,
                    paths.Length,
                    string.Join(" | ", tokenRejectSamples));
            }
        }

        if (paths.Length > 0)
            return new PathSearchResult(paths, null);

        var reason = tokenValidationRejects > 0
            ? tokenMetadataRejects > tokenContractRejects
                ? "NeoOnEdge found symbol-level paths, but pool token contract metadata is missing for those pools."
                : "NeoOnEdge found symbol-level paths, but their token contracts do not match the requested token id."
            : reachedOutputPaths > 0
            ? "A NeoOnEdge path exists, but this amount failed liquidity or safety caps."
            : capped || visitedStates >= Math.Max(1, settings.Value.NeoRouteMaxSearchStates)
                ? "NeoOnEdge route search was capped before finding a path."
                : $"No NeoOnEdge path found within {boundedMaxHops} hop{(boundedMaxHops == 1 ? "" : "s")}.";

        return new PathSearchResult(paths, reason);
    }

    private async Task<PoolPath?> GetLiveAlcorPathAsync(
        string input,
        string output,
        long rawAmount,
        int inputPrecision,
        int neoFeeBps,
        int maxHops,
        double slippage,
        string receiver,
        IReadOnlySet<string> excludedTokenKeys,
        CancellationToken ct,
        bool readAlcorCache)
    {
        var route = readAlcorCache
            ? await alcorRouter.GetRouteAsync(input, output, rawAmount, inputPrecision, receiver, slippage, maxHops, ct)
            : await alcorRouter.GetFreshRouteAsync(input, output, rawAmount, inputPrecision, receiver, slippage, maxHops, ct);

        if (route is null ||
            route.Route.Length == 0 ||
            route.Route.Length > maxHops ||
            route.Swaps.Count != 1 ||
            route.Swaps[0].Percent != 100 ||
            !route.Swaps[0].Route.SequenceEqual(route.Route))
            return null;

        var pools = new List<PoolInfo>(route.Route.Length);
        var currentTokenId = input;

        foreach (var poolId in route.Route)
        {
            if (!route.Pools.TryGetValue(poolId.ToString(), out var pool))
                return null;

            AlcorTokenId nextToken;
            if (pool.TokenA.Id.Equals(currentTokenId, StringComparison.OrdinalIgnoreCase))
            {
                nextToken = pool.TokenB;
            }
            else if (pool.TokenB.Id.Equals(currentTokenId, StringComparison.OrdinalIgnoreCase))
            {
                nextToken = pool.TokenA;
            }
            else
            {
                return null;
            }

            if (NeoRouteQuoteSupport.IsExcludedAlcorToken(pool.TokenA, excludedTokenKeys) ||
                NeoRouteQuoteSupport.IsExcludedAlcorToken(pool.TokenB, excludedTokenKeys))
                return null;

            pools.Add(new PoolInfo(
                Src: "a2",
                PairId: poolId.ToString(),
                CodeA: pool.TokenA.Symbol.ToUpperInvariant(),
                CodeB: pool.TokenB.Symbol.ToUpperInvariant(),
                ReserveA: 0,
                ReserveB: 0,
                FeeBps: NeoRouteQuoteSupport.NormalizeAlcorFeeBps(pool.Fee),
                TokenIdA: pool.TokenA.Id,
                TokenIdB: pool.TokenB.Id));
            currentTokenId = nextToken.Id;
        }

        if (!currentTokenId.Equals(output, StringComparison.OrdinalIgnoreCase))
            return null;

        var gross = NeoRouteQuoteSupport.ParseRouteAmount(route.Output);
        var minimumGross = NeoRouteQuoteSupport.ParseRouteAmount(route.MinReceived);
        if (gross <= 0 || minimumGross <= 0)
            return null;

        return new PoolPath(
            pools.ToArray(),
            gross,
            NeoRouteQuoteSupport.ApplyProtocolFee(gross, neoFeeBps),
            NeoRouteQuoteSupport.ApplyProtocolFee(minimumGross, neoFeeBps));
    }

    private static bool IsExcludedPoolToken(string symbol, string? tokenId, IReadOnlySet<string> excludedTokenKeys)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return false;

        return excludedTokenKeys.Contains(NeoRouteQuoteSupport.TokenKey(NeoRouteQuoteSupport.ParseContract(tokenId), symbol));
    }

    private Dictionary<string, List<PoolInfo>> BuildAdjacency(IReadOnlyList<PoolInfo> latest)
    {
        var adjacency = new Dictionary<string, List<PoolInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in latest)
        {
            if (!adjacency.TryGetValue(pool.CodeA, out var listA))
            {
                listA = [];
                adjacency[pool.CodeA] = listA;
            }
            listA.Add(pool);

            if (!adjacency.TryGetValue(pool.CodeB, out var listB))
            {
                listB = [];
                adjacency[pool.CodeB] = listB;
            }
            listB.Add(pool);
        }

        foreach (var tokenCode in adjacency.Keys.ToArray())
        {
            adjacency[tokenCode] = SelectNeighbourPools(adjacency[tokenCode]);
        }

        return adjacency;
    }

    private List<PoolInfo> SelectNeighbourPools(IReadOnlyList<PoolInfo> pools)
    {
        int maxPerToken = Math.Max(1, settings.Value.NeoRouteMaxPoolsPerToken);
        if (pools.Count <= maxPerToken)
        {
            return pools
                .OrderByDescending(NeoRouteQuoteSupport.PoolLiquidity)
                .ToList();
        }

        var ordered = pools
            .OrderByDescending(NeoRouteQuoteSupport.PoolLiquidity)
            .ToList();

        var selected = new List<PoolInfo>(maxPerToken);

        foreach (var src in PreferredSyntheticSources)
        {
            foreach (var edge in ordered
                .Where(pool => pool.Src.Equals(src, StringComparison.OrdinalIgnoreCase))
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
}
