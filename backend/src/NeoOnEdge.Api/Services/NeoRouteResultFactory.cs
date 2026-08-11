namespace NeoOnEdge.Api.Services;

public sealed class NeoRouteResultFactory
{
    internal NeoRouteResult BuildRouteResult(
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
        if (bestPath.MinimumNetOut is { } quotedMinimum)
            minReceived = Math.Min(minReceived, quotedMinimum);

        decimal estimatedProtocolFee = bestPath.GrossOut - bestPath.NetOut;
        int liquidityProviderFeeBps = bestPath.Pools.Sum(pool => (int)NeoRouteQuoteSupport.DexFeeBps(pool));
        string contractOut = NeoRouteQuoteSupport.ParseContract(output);
        long minRaw = NeoRouteQuoteSupport.ToRawAmount(minReceived, outputPrecision);
        string memo = NeoRouteQuoteSupport.BuildMemo(bestPath.Pools, minRaw, codeOut, contractOut);

        var first = bestPath.Pools.First();
        return new NeoRouteResult(
            Src: first.Src,
            PairId: first.PairId,
            Output: NeoRouteQuoteSupport.FormatTokenAmount(bestPath.NetOut, outputPrecision),
            MinReceived: NeoRouteQuoteSupport.FormatTokenAmount(minReceived, outputPrecision),
            Memo: memo,
            Receiver: receiver,
            ContractAccount: contractAccount,
            ProtocolFeeBps: neoFeeBps,
            LiquidityProviderFeeBps: liquidityProviderFeeBps,
            TotalFeeBps: liquidityProviderFeeBps + neoFeeBps,
            EstimatedProtocolFee: NeoRouteQuoteSupport.FormatTokenAmount(estimatedProtocolFee, outputPrecision),
            Steps: NeoRouteQuoteSupport.BuildRouteSteps(bestPath.Pools, codeIn));
    }

    internal NeoSplitRouteResult BuildSplitRouteResult(
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
        var contractOut = NeoRouteQuoteSupport.ParseContract(output);
        var legs = allocation.Legs
            .Select((leg, index) => BuildSplitRouteLeg(leg, index, inputPrecision, outputPrecision, slippageMultiplier, receiver, contractAccount, neoFeeBps, codeIn, codeOut, contractOut))
            .ToArray();
        var minReceived = allocation.NetOut * slippageMultiplier;

        return new NeoSplitRouteResult(
            Output: NeoRouteQuoteSupport.FormatTokenAmount(allocation.NetOut, outputPrecision),
            MinReceived: NeoRouteQuoteSupport.FormatTokenAmount(minReceived, outputPrecision),
            Receiver: receiver,
            Legs: legs,
            TotalFeeBps: NeoRouteQuoteSupport.EstimateWeightedFeeBps(allocation.Legs, totalInputRaw, neoFeeBps),
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
        var minRaw = NeoRouteQuoteSupport.ToRawAmount(minReceived, outputPrecision);
        var liquidityProviderFeeBps = leg.Path.Pools.Sum(pool => (int)NeoRouteQuoteSupport.DexFeeBps(pool));

        return new NeoSplitRouteLeg(
            Source: "neo",
            Label: NeoRouteQuoteSupport.BuildSplitLegLabel(leg.Path, index),
            Contract: contractAccount,
            Receiver: receiver,
            Memo: NeoRouteQuoteSupport.BuildMemo(leg.Path.Pools, minRaw, codeOut, contractOut),
            InputRaw: leg.InputRaw,
            InputAmount: NeoRouteQuoteSupport.FormatTokenAmount(leg.InputRaw / NeoRouteQuoteSupport.Pow10(inputPrecision), inputPrecision),
            Output: NeoRouteQuoteSupport.FormatTokenAmount(leg.NetOut, outputPrecision),
            MinReceived: NeoRouteQuoteSupport.FormatTokenAmount(minReceived, outputPrecision),
            Hops: Math.Max(1, leg.Path.Pools.Length),
            Steps: NeoRouteQuoteSupport.BuildRouteSteps(leg.Path.Pools, codeIn),
            ProtocolFeeBps: neoFeeBps,
            LiquidityProviderFeeBps: liquidityProviderFeeBps,
            TotalFeeBps: liquidityProviderFeeBps + neoFeeBps,
            EstimatedProtocolFee: NeoRouteQuoteSupport.FormatTokenAmount(leg.GrossOut - leg.NetOut, outputPrecision));
    }
}
