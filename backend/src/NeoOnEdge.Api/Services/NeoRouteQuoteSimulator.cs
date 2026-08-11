using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;

namespace NeoOnEdge.Api.Services;

public sealed class NeoRouteQuoteSimulator(IOptions<AppSettings> settings)
{
    internal decimal SimulatePath(IReadOnlyList<PoolInfo> pools, decimal amountIn, string codeIn)
    {
        decimal current = amountIn;
        string currentCode = codeIn;

        foreach (var pool in pools)
        {
            decimal reserveIn;
            decimal reserveOut;
            if (pool.CodeA.Equals(currentCode, StringComparison.OrdinalIgnoreCase))
            {
                reserveIn = pool.ReserveA;
                reserveOut = pool.ReserveB;
                currentCode = pool.CodeB;
                if (pool.RateAtoB is { } rateAtoB)
                {
                    if (current > pool.MaxInputA)
                        return 0m;
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
                    if (current > pool.MaxInputB)
                        return 0m;
                    current *= rateBtoA;
                    continue;
                }
            }
            else
            {
                return 0m;
            }

            if (current <= 0 || reserveIn <= 0 || reserveOut <= 0)
                return 0m;

            var dexFeeBps = NeoRouteQuoteSupport.DexFeeBps(pool);
            var afterDex = current * (10_000m - dexFeeBps) / 10_000m;
            var grossOut = reserveOut * afterDex / (reserveIn + afterDex);
            if (grossOut <= 0 || grossOut > reserveOut * settings.Value.NeoRouteMaxPoolOutputReserveShare)
                return 0m;

            current = grossOut;
        }

        return current;
    }

    internal SplitAllocation? EvaluateSplitAllocation(
        IReadOnlyList<PoolPath> paths,
        IReadOnlyList<long> rawAmounts,
        int inputPrecision,
        int neoFeeBps,
        string codeIn)
    {
        if (paths.Count != rawAmounts.Count)
            return null;

        var legs = new List<SplitLegAllocation>(paths.Count);
        var scale = NeoRouteQuoteSupport.Pow10(inputPrecision);
        for (int i = 0; i < paths.Count; i++)
        {
            var rawAmount = rawAmounts[i];
            if (rawAmount <= 0)
                return null;

            var gross = SimulatePath(paths[i].Pools, rawAmount / scale, codeIn);
            if (gross <= 0)
                return null;

            var net = NeoRouteQuoteSupport.ApplyProtocolFee(gross, neoFeeBps);
            if (net <= 0)
                return null;

            legs.Add(new SplitLegAllocation(paths[i], rawAmount, gross, net));
        }

        return legs.Count >= 2
            ? new SplitAllocation(legs, legs.Sum(leg => leg.NetOut))
            : null;
    }
}
