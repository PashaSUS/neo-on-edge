using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;

namespace NeoOnEdge.Api.Services;

public sealed class NeoRouteQuoteService(
    NeoRoutePathFinder pathFinder,
    NeoRouteQuoteSimulator simulator,
    NeoRouteResultFactory resultFactory,
    IOptions<AppSettings> settings,
    ILogger<NeoRouteQuoteService> log)
{
    private const int MaxSplitLegs = 4;
    private const int MaxSplitCandidatePaths = 8;
    private const int SplitPercentStep = 1;
    private const decimal MaxSplitDirectImprovementMultiplier = 1.75m;
    private const decimal MaxDirectNeoAlcorImprovementMultiplier = 2m;

    public Task<NeoRouteResult?> GetRouteAsync(
        string input,
        string output,
        long amount,
        string receiver,
        int inputPrecision,
        int outputPrecision,
        double slippage,
        CancellationToken ct = default,
        bool readAlcorCache = true)
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
            ct,
            readAlcorCache);

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
        CancellationToken ct = default,
        bool readAlcorCache = true)
        => (await GetRouteQuoteAsync(
            input,
            output,
            amount,
            receiver,
            inputPrecision,
            outputPrecision,
            slippage,
            maxHops,
            contractAccount,
            ct,
            readAlcorCache)).Route;

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
        CancellationToken ct = default,
        bool readAlcorCache = true)
    {
        string codeIn = NeoRouteQuoteSupport.ParseCode(input);
        string codeOut = NeoRouteQuoteSupport.ParseCode(output);

        try
        {
            int neoFeeBps = Math.Clamp(settings.Value.NeoRouteFeeBps, 0, 100);
            int boundedMaxHops = Math.Clamp(maxHops, 1, Math.Max(1, settings.Value.NeoRouteMaxHops));
            decimal amountDecimals = amount / NeoRouteQuoteSupport.Pow10(inputPrecision);
            var search = await pathFinder.FindCandidatePathsAsync(
                codeIn,
                codeOut,
                amount,
                inputPrecision,
                amountDecimals,
                neoFeeBps,
                boundedMaxHops,
                1,
                input,
                output,
                slippage,
                contractAccount,
                ct,
                readAlcorCache);
            var bestPath = search.Paths.FirstOrDefault();

            if (bestPath is null)
                return new NeoRouteQuote(null, search.Reason ?? "No executable NeoOnEdge route found.");

            var liveAlcorReference = NeoRouteQuoteSupport.FindLiveAlcorReferencePath(search.Paths);
            if (NeoRouteQuoteSupport.IsSuspiciousComparedToLiveAlcor(bestPath, liveAlcorReference, MaxDirectNeoAlcorImprovementMultiplier))
            {
                log.LogWarning(
                    "Rejected suspicious Neo quote: input={Input} output={Output} neoOut={NeoOut} alcorOut={AlcorOut} multiplier={Multiplier}",
                    input,
                    output,
                    bestPath.NetOut,
                    liveAlcorReference!.NetOut,
                    bestPath.NetOut / liveAlcorReference.NetOut);
                return new NeoRouteQuote(null, "NeoOnEdge route was rejected by the safety guardrail versus live Alcor.");
            }

            return new NeoRouteQuote(
                resultFactory.BuildRouteResult(bestPath, output, receiver, outputPrecision, slippage, contractAccount, neoFeeBps, codeIn, codeOut),
                null);
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
        CancellationToken ct = default,
        bool readAlcorCache = true)
        => (await GetSplitRouteQuoteAsync(
            input,
            output,
            amount,
            receiver,
            inputPrecision,
            outputPrecision,
            slippage,
            maxHops,
            contractAccount,
            ct,
            readAlcorCache)).Route;

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
        CancellationToken ct = default,
        bool readAlcorCache = true)
    {
        if (amount <= 1)
            return new NeoSplitRouteQuote(null, "Input amount is too small to split.");

        string codeIn = NeoRouteQuoteSupport.ParseCode(input);
        string codeOut = NeoRouteQuoteSupport.ParseCode(output);

        try
        {
            int neoFeeBps = Math.Clamp(settings.Value.NeoRouteFeeBps, 0, 100);
            int boundedMaxHops = Math.Clamp(maxHops, 1, Math.Max(1, settings.Value.NeoRouteMaxHops));
            decimal amountDecimals = amount / NeoRouteQuoteSupport.Pow10(inputPrecision);
            var search = await pathFinder.FindCandidatePathsAsync(
                codeIn,
                codeOut,
                amount,
                inputPrecision,
                amountDecimals,
                neoFeeBps,
                boundedMaxHops,
                MaxSplitCandidatePaths,
                input,
                output,
                slippage,
                contractAccount,
                ct,
                readAlcorCache);
            var candidates = search.Paths
                .GroupBy(NeoRouteQuoteSupport.PathKey, StringComparer.OrdinalIgnoreCase)
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
                    if (!NeoRouteQuoteSupport.PathsAreDisjoint(candidates[i], candidates[j]))
                        continue;

                    for (int firstPercent = SplitPercentStep; firstPercent <= 100 - SplitPercentStep; firstPercent += SplitPercentStep)
                    {
                        var firstRaw = amount * firstPercent / 100;
                        var secondRaw = amount - firstRaw;
                        var allocation = simulator.EvaluateSplitAllocation(
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
                            if (!NeoRouteQuoteSupport.PathsAreDisjoint(candidates[i], candidates[j], candidates[k]))
                                continue;

                            for (int firstPercent = SplitPercentStep; firstPercent <= 100 - (2 * SplitPercentStep); firstPercent += SplitPercentStep)
                            {
                                for (int secondPercent = SplitPercentStep; secondPercent <= 100 - firstPercent - SplitPercentStep; secondPercent += SplitPercentStep)
                                {
                                    var firstRaw = amount * firstPercent / 100;
                                    var secondRaw = amount * secondPercent / 100;
                                    var thirdRaw = amount - firstRaw - secondRaw;
                                    var allocation = simulator.EvaluateSplitAllocation(
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

            var liveAlcorReference = NeoRouteQuoteSupport.FindLiveAlcorReferencePath(search.Paths);
            if (liveAlcorReference is not null &&
                best.NetOut > liveAlcorReference.NetOut * MaxDirectNeoAlcorImprovementMultiplier)
            {
                log.LogWarning(
                    "Rejected suspicious Neo split quote versus Alcor: input={Input} output={Output} splitOut={SplitOut} alcorOut={AlcorOut} multiplier={Multiplier}",
                    input,
                    output,
                    best.NetOut,
                    liveAlcorReference.NetOut,
                    best.NetOut / liveAlcorReference.NetOut);
                return new NeoSplitRouteQuote(null, "Split quote was rejected by the safety guardrail versus live Alcor.");
            }

            return new NeoSplitRouteQuote(
                resultFactory.BuildSplitRouteResult(best, inputPrecision, outputPrecision, slippage, output, receiver, contractAccount, neoFeeBps, codeIn, codeOut, amount),
                null);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Neo split route quote failed for {Input}->{Output}", input, output);
            return new NeoSplitRouteQuote(null, "Neo split quote failed.");
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
