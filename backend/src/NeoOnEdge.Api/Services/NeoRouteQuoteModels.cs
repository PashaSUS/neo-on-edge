namespace NeoOnEdge.Api.Services;

internal sealed record PoolInfo(
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
    decimal LiquidityScore = 0,
    string? TokenIdA = null,
    string? TokenIdB = null,
    decimal FeeBps = 0);

internal sealed record PathSearchResult(IReadOnlyList<PoolPath> Paths, string? Reason);

internal sealed record PoolPath(
    PoolInfo[] Pools,
    decimal GrossOut,
    decimal NetOut,
    decimal? MinimumNetOut = null);

internal sealed record SplitAllocation(IReadOnlyList<SplitLegAllocation> Legs, decimal NetOut);

internal sealed record SplitLegAllocation(PoolPath Path, long InputRaw, decimal GrossOut, decimal NetOut);

internal sealed record PathTokenValidationResult(
    bool IsValid,
    string? Reason,
    string? CurrentTokenId,
    string? PoolKey,
    string? TokenIdA,
    string? TokenIdB);

internal sealed class PathState
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
