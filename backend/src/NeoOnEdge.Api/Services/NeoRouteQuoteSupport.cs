namespace NeoOnEdge.Api.Services;

internal static class NeoRouteQuoteSupport
{
    public static string ParseCode(string alcorId)
    {
        int dash = alcorId.IndexOf('-');
        return dash > 0 ? alcorId[..dash].ToUpperInvariant() : alcorId.ToUpperInvariant();
    }

    public static string ParseContract(string alcorId)
    {
        int dash = alcorId.IndexOf('-');
        return dash > 0 ? alcorId[(dash + 1)..] : "eosio.token";
    }

    public static decimal ParseRouteAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var amount = value.Trim();
        int space = amount.IndexOf(' ');
        if (space > 0)
            amount = amount[..space];

        return decimal.TryParse(
            amount,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }

    public static bool IsExcludedAlcorToken(AlcorTokenId token, IReadOnlySet<string> excludedTokenKeys)
        => excludedTokenKeys.Contains(TokenKey(ParseContract(token.Id), token.Symbol));

    public static bool TryValidatePathTokenIds(IReadOnlyList<PoolInfo> pools, string inputTokenId, string outputTokenId)
        => ValidatePathTokenIds(pools, inputTokenId, outputTokenId).IsValid;

    public static PathTokenValidationResult ValidatePathTokenIds(IReadOnlyList<PoolInfo> pools, string inputTokenId, string outputTokenId)
    {
        var currentTokenId = inputTokenId;
        foreach (var pool in pools)
        {
            if (string.IsNullOrWhiteSpace(pool.TokenIdA) || string.IsNullOrWhiteSpace(pool.TokenIdB))
            {
                return new PathTokenValidationResult(
                    false,
                    "Pool token contract metadata is missing.",
                    currentTokenId,
                    PoolKey(pool),
                    pool.TokenIdA,
                    pool.TokenIdB);
            }

            if (pool.TokenIdA.Equals(currentTokenId, StringComparison.OrdinalIgnoreCase))
            {
                currentTokenId = pool.TokenIdB;
                continue;
            }

            if (pool.TokenIdB.Equals(currentTokenId, StringComparison.OrdinalIgnoreCase))
            {
                currentTokenId = pool.TokenIdA;
                continue;
            }

            return new PathTokenValidationResult(
                false,
                "Pool token contracts do not continue from the current route token.",
                currentTokenId,
                PoolKey(pool),
                pool.TokenIdA,
                pool.TokenIdB);
        }

        return currentTokenId.Equals(outputTokenId, StringComparison.OrdinalIgnoreCase)
            ? new PathTokenValidationResult(true, null, currentTokenId, null, null, null)
            : new PathTokenValidationResult(
                false,
                "Path ended at a different token contract than requested.",
                currentTokenId,
                null,
                null,
                null);
    }

    public static PoolPath? FindLiveAlcorReferencePath(IReadOnlyList<PoolPath> paths)
        => paths.FirstOrDefault(path =>
            path.Pools.Length > 0 &&
            path.Pools.All(pool => pool.Src.Equals("a2", StringComparison.OrdinalIgnoreCase)));

    public static bool IsSuspiciousComparedToLiveAlcor(PoolPath candidate, PoolPath? liveAlcorReference, decimal maxImprovementMultiplier)
        => liveAlcorReference is not null &&
           !string.Equals(PathKey(candidate), PathKey(liveAlcorReference), StringComparison.OrdinalIgnoreCase) &&
           liveAlcorReference.NetOut > 0 &&
           candidate.NetOut > liveAlcorReference.NetOut * maxImprovementMultiplier;

    public static string TokenKey(string contract, string symbol)
        => $"{contract.Trim().ToLowerInvariant()}:{symbol.Trim().ToUpperInvariant()}";

    public static string BuildTokenId(string symbol, string contract)
        => $"{symbol.Trim().ToLowerInvariant()}-{contract.Trim()}";

    public static (string? TokenIdA, string? TokenIdB) ResolvePoolTokenIds(
        string codeReserveA,
        string codeReserveB,
        string? listingCodeA,
        string? listingCodeB,
        string? listingContractA,
        string? listingContractB)
    {
        if (string.IsNullOrWhiteSpace(listingCodeA) ||
            string.IsNullOrWhiteSpace(listingCodeB) ||
            string.IsNullOrWhiteSpace(listingContractA) ||
            string.IsNullOrWhiteSpace(listingContractB))
            return (null, null);

        if (codeReserveA.Equals(listingCodeA, StringComparison.OrdinalIgnoreCase) &&
            codeReserveB.Equals(listingCodeB, StringComparison.OrdinalIgnoreCase))
            return (BuildTokenId(listingCodeA, listingContractA), BuildTokenId(listingCodeB, listingContractB));

        if (codeReserveA.Equals(listingCodeB, StringComparison.OrdinalIgnoreCase) &&
            codeReserveB.Equals(listingCodeA, StringComparison.OrdinalIgnoreCase))
            return (BuildTokenId(listingCodeB, listingContractB), BuildTokenId(listingCodeA, listingContractA));

        return (null, null);
    }

    public static decimal Pow10(int precision)
    {
        precision = Math.Clamp(precision, 0, 18);
        decimal result = 1m;
        for (int i = 0; i < precision; i++)
            result *= 10m;
        return result;
    }

    public static string FormatTokenAmount(decimal amount, int precision)
        => amount.ToString($"F{Math.Clamp(precision, 0, 18)}", System.Globalization.CultureInfo.InvariantCulture);

    public static long ToRawAmount(decimal amount, int precision)
    {
        if (amount <= 0)
            return 0;

        return (long)Math.Floor(amount * Pow10(precision));
    }

    public static decimal ApplyProtocolFee(decimal amount, int protocolFeeBps)
        => amount * (10_000m - protocolFeeBps) / 10_000m;

    public static decimal DexFeeBps(PoolInfo pool)
        => pool.FeeBps > 0 ? pool.FeeBps : pool.Src switch
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

    public static decimal NormalizeAlcorFeeBps(int fee)
        => fee > 100 ? fee / 100m : fee;

    public static decimal PoolLiquidity(PoolInfo pool)
        => pool.LiquidityScore > 0 ? pool.LiquidityScore : pool.ReserveA + pool.ReserveB;

    public static string PoolDictionaryKey(string src, string pairId)
        => $"{src}:{pairId}";

    public static string BuildMemo(IReadOnlyList<PoolInfo> pools, long minRaw, string codeOut, string contractOut)
    {
        var routeParts = pools.Select(pool => $"{pool.PairId}@{CanonicalRouteSource(pool.Src)}");
        return $"swap,{minRaw},{codeOut}@{contractOut}|{string.Join('-', routeParts)}";
    }

    public static int EstimateWeightedFeeBps(IReadOnlyList<SplitLegAllocation> legs, long totalInputRaw, int neoFeeBps)
    {
        if (totalInputRaw <= 0)
            return 0;

        decimal weighted = 0;
        foreach (var leg in legs)
        {
            var legFeeBps = leg.Path.Pools.Sum(pool => (int)DexFeeBps(pool)) + neoFeeBps;
            weighted += (decimal)leg.InputRaw / totalInputRaw * legFeeBps;
        }

        return (int)Math.Round(weighted, MidpointRounding.AwayFromZero);
    }

    public static bool PathsAreDisjoint(params PoolPath[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in paths.SelectMany(path => path.Pools))
        {
            if (!seen.Add(PoolKey(pool)))
                return false;
        }

        return true;
    }

    public static string PathKey(PoolPath path)
        => string.Join('-', path.Pools.Select(PoolKey));

    public static string PoolKey(PoolInfo pool)
        => $"{CanonicalRouteSource(pool.Src)}:{pool.PairId}";

    public static string CanonicalRouteSource(string src)
        => src.Equals("a3", StringComparison.OrdinalIgnoreCase) ? "a2" : src;

    public static string BuildSplitLegLabel(PoolPath path, int index)
    {
        var labels = path.Pools
            .Select(pool => SourceLabel(pool.Src))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length > 0
            ? string.Join(" + ", labels)
            : $"Neo leg {index + 1}";
    }

    public static string SourceLabel(string src) => src switch
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

    public static IReadOnlyList<NeoRouteStep> BuildRouteSteps(IReadOnlyList<PoolInfo> pools, string codeIn)
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

    public static bool IsPoolSane(PoolInfo pool)
    {
        try
        {
            const decimal MaxReserve = 1_000_000_000_000m;
            const decimal MinReserve = 0.000000000001m;

            if (pool.RateAtoB is not null || pool.RateBtoA is not null)
            {
                if (!(pool.Src.Equals("a2", StringComparison.OrdinalIgnoreCase) ||
                      pool.Src.Equals("a3", StringComparison.OrdinalIgnoreCase) ||
                      pool.Src.Equals("ad", StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (string.IsNullOrWhiteSpace(pool.PairId) || string.IsNullOrWhiteSpace(pool.CodeA) || string.IsNullOrWhiteSpace(pool.CodeB))
                    return false;
                if (pool.RateAtoB is { } rateAtoB && (rateAtoB <= 0 || rateAtoB < 0.000001m || rateAtoB > 1_000_000m || pool.MaxInputA <= 0))
                    return false;
                if (pool.RateBtoA is { } rateBtoA && (rateBtoA <= 0 || rateBtoA < 0.000001m || rateBtoA > 1_000_000m || pool.MaxInputB <= 0))
                    return false;
                return true;
            }

            if (pool.ReserveA <= 0 || pool.ReserveB <= 0)
                return false;
            if (pool.ReserveA < MinReserve || pool.ReserveB < MinReserve)
                return false;
            if (pool.ReserveA > MaxReserve || pool.ReserveB > MaxReserve)
                return false;

            var price = pool.ReserveB / pool.ReserveA;
            if (price <= 0 || price < 0.000001m || price > 1_000_000m)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
