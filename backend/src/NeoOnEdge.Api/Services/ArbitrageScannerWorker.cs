using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Services;

public sealed class ArbitrageScannerWorker(
    ILogger<ArbitrageScannerWorker> log,
    IServiceScopeFactory scopes,
    IOptions<AppSettings> settings,
    IConnectionMultiplexer redis,
    IHttpClientFactory httpFactory,
    ArbitrageExecutionService execution) : BackgroundService
{
    private const int WaxPrecision = 8;
    private const int MaxArbitrageHops = 3;
    private const int MaxSplitLegs = 3;
    private const decimal MaxReasonableDryRunProfitPercent = 25m;
    private const decimal MinSplitImprovementPercent = 0.05m;

    private static readonly HashSet<string> NeoArbitrageSafeTokenIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "usdt-usdt.alcor",
        "fgl-fgltokenmgmt",
    };

    private readonly IDatabase _cache = redis.GetDatabase();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.Value.ArbitrageScannerEnabled)
        {
            log.LogInformation("Arbitrage scanner is disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Value.ArbitrageDiscordWebhookUrl) &&
            !settings.Value.ArbitrageExecutionEnabled)
        {
            log.LogWarning("Arbitrage scanner is enabled but neither Discord alerts nor automatic execution are configured");
            return;
        }

        if (settings.Value.ArbitrageExecutionEnabled && !execution.IsConfigured)
        {
            log.LogError("Automatic arbitrage execution is enabled but the account or executor secret is missing");
            return;
        }

        await RunScanSafely(ct);

        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.Value.ArbitrageScanIntervalSeconds, 60, 86_400));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct))
            await RunScanSafely(ct);
    }

    private async Task RunScanSafely(CancellationToken ct)
    {
        try
        {
            await RunScan(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Arbitrage scan failed");
        }
    }

    private async Task RunScan(CancellationToken ct)
    {
        var cfg = settings.Value;
        var probeAmounts = GetProbeAmounts(cfg);
        if (probeAmounts.Count == 0)
        {
            log.LogWarning("Arbitrage scanner has no WAX probe amounts to evaluate");
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await scope.ServiceProvider.GetRequiredService<TokenCatalogService>().GetTokensAsync(db, ct);
        var alcorRouter = scope.ServiceProvider.GetRequiredService<AlcorRouterService>();
        var neoRouter = scope.ServiceProvider.GetRequiredService<NeoRouteQuoteService>();

        var wax = tokens.FirstOrDefault(IsWaxToken) ?? new TokenCatalogItem(
            "wax-eosio.token",
            "eosio.token",
            "WAX",
            WaxPrecision,
            "default",
            PopularityScore: 10_000,
            IsTrusted: true);

        var candidates = tokens
            .Where(t => !IsWaxToken(t))
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Contract) && !string.IsNullOrWhiteSpace(t.Symbol))
            .Where(t => t.Precision is >= 0 and <= 18)
            .OrderByDescending(t => t.IsTrusted)
            .ThenByDescending(t => t.PopularityScore)
            .ThenBy(t => t.Symbol)
            .ToArray();

        if (cfg.ArbitrageMaxTokensPerScan > 0)
            candidates = candidates.Take(cfg.ArbitrageMaxTokensPerScan).ToArray();

        var opportunities = new List<ArbitrageOpportunity>();
        int routeLookups = 0;
        int rejectedUnsafeNeoRoutes = 0;
        int rejectedSuspiciousRoundTrips = 0;
        int neoSuppressedForContractSafety = 0;
        int delayMs = Math.Clamp(cfg.ArbitrageRequestDelayMilliseconds, 0, 5_000);

        foreach (var token in candidates)
        {
            var includeNeoRoutes = IsTokenSafeForNeoArbitrage(token);
            if (!includeNeoRoutes)
                neoSuppressedForContractSafety++;

            foreach (var inputWax in probeAmounts)
            {
                var rawWax = ToRawAmount(inputWax, WaxPrecision);
                if (rawWax is null)
                    continue;

                var buyCandidates = await GetQuoteCandidatesAsync(
                    alcorRouter,
                    neoRouter,
                    wax.Id,
                    token.Id,
                    rawWax.Value,
                    WaxPrecision,
                    token.Precision,
                    cfg,
                    includeNeoRoutes,
                    ct);

                routeLookups += buyCandidates.LookupCount;

                foreach (var buyRoute in buyCandidates.Routes)
                {
                    var sellCandidates = await GetQuoteCandidatesAsync(
                        alcorRouter,
                        neoRouter,
                        token.Id,
                        wax.Id,
                        buyRoute.OutputRaw,
                        token.Precision,
                        WaxPrecision,
                        cfg,
                        includeNeoRoutes,
                        ct);

                    routeLookups += sellCandidates.LookupCount;

                    foreach (var sellRoute in sellCandidates.Routes)
                    {
                        if (!IsSafeRouteCombination(token, buyRoute, sellRoute))
                        {
                            rejectedUnsafeNeoRoutes++;
                            continue;
                        }

                        decimal profitWax = sellRoute.OutputAmount - inputWax;
                        if (profitWax <= 0)
                            continue;

                        decimal profitPercent = inputWax == 0 ? 0 : profitWax / inputWax * 100m;
                        if (profitPercent > MaxReasonableDryRunProfitPercent)
                        {
                            rejectedSuspiciousRoundTrips++;
                            log.LogWarning(
                                "Rejected suspicious arbitrage quote for {Token}: {InputWax} WAX -> {FinalWax} WAX ({ProfitPercent}%) via {BuySource}/{SellSource}",
                                token.Id,
                                inputWax,
                                sellRoute.OutputAmount,
                                profitPercent,
                                buyRoute.Source,
                                sellRoute.Source);
                            continue;
                        }

                        if (profitWax < cfg.ArbitrageMinProfitWax || profitPercent < cfg.ArbitrageMinProfitPercent)
                            continue;

                        opportunities.Add(new ArbitrageOpportunity(
                            token,
                            inputWax,
                            sellRoute.OutputAmount,
                            profitWax,
                            profitPercent,
                            buyRoute,
                            sellRoute));
                    }
                }
            }

            if (delayMs > 0)
                await Task.Delay(delayMs, ct);
        }

        var top = opportunities
            .Where(IsAlertSafeOpportunity)
            .OrderByDescending(x => x.ProfitWax)
            .ThenByDescending(x => x.ProfitPercent)
            .Take(Math.Clamp(cfg.ArbitrageAlertTopCount, 1, 25))
            .ToArray();

        if (top.Length > 0)
        {
            await PostOpportunities(top, candidates.Length, routeLookups, ct);
            await TryExecuteTopOpportunityAsync(top, wax, alcorRouter, neoRouter, cfg, ct);
            log.LogInformation(
                "Arbitrage scan found {Count} dry-run opportunities across {Candidates} tokens (alerted={Alerted}, rejectedUnsafeNeoRoutes={RejectedUnsafeNeoRoutes}, rejectedSuspiciousRoundTrips={RejectedSuspiciousRoundTrips}, neoSuppressedForContractSafety={NeoSuppressedForContractSafety})",
                opportunities.Count,
                candidates.Length,
                top.Length,
                rejectedUnsafeNeoRoutes,
                rejectedSuspiciousRoundTrips,
                neoSuppressedForContractSafety);
            return;
        }

        await PostNoOpportunityHeartbeat(candidates.Length, routeLookups, ct);
        log.LogInformation(
            "Arbitrage scan found no alert-safe opportunities across {Candidates} tokens (rawOpportunities={RawOpportunities}, rejectedUnsafeNeoRoutes={RejectedUnsafeNeoRoutes}, rejectedSuspiciousRoundTrips={RejectedSuspiciousRoundTrips}, neoSuppressedForContractSafety={NeoSuppressedForContractSafety})",
            candidates.Length,
            opportunities.Count,
            rejectedUnsafeNeoRoutes,
            rejectedSuspiciousRoundTrips,
            neoSuppressedForContractSafety);
    }

    private async Task<QuoteCandidateSet> GetQuoteCandidatesAsync(
        AlcorRouterService alcorRouter,
        NeoRouteQuoteService neoRouter,
        string input,
        string output,
        long rawAmount,
        int inputPrecision,
        int outputPrecision,
        AppSettings cfg,
        bool includeNeoRoutes,
        CancellationToken ct)
    {
        var receiver = cfg.ArbitrageExecutionEnabled && !string.IsNullOrWhiteSpace(cfg.ArbitrageAccount)
            ? cfg.ArbitrageAccount
            : cfg.ContractAccount;
        var quotes = new List<RouteQuote>(2);
        var maxHops = EffectiveArbitrageMaxHops(cfg);
        var lookupCount = 0;

        var alcor = await alcorRouter.GetRouteAsync(
            input,
            output,
            rawAmount,
            inputPrecision,
            receiver,
            cfg.ArbitrageSlippage,
            maxHops,
            ct);
        lookupCount++;

        if (alcor is not null)
        {
            AddRouteQuote(
                quotes,
                "Alcor",
                alcor.Output,
                outputPrecision,
                alcor.Memo,
                "swap.alcor",
                Math.Max(1, alcor.Route.Length),
                alcor.Route.Length > 0 ? string.Join(" -> ", alcor.Route.Select(x => $"pool {x}")) : "router",
                [new RouteLeg("Alcor", ToTokenAmount(rawAmount, inputPrecision), ParseRouteAmount(alcor.Output), Math.Max(1, alcor.Route.Length), alcor.Route.Length > 0 ? string.Join(" -> ", alcor.Route.Select(x => $"pool {x}")) : "router")]);
        }

        if (includeNeoRoutes)
        {
            var neo = await neoRouter.GetRouteAsync(
                    input,
                    output,
                    rawAmount,
                    receiver,
                    inputPrecision,
                    outputPrecision,
                    cfg.ArbitrageSlippage,
                    maxHops,
                    cfg.ContractAccount,
                    ct);
            lookupCount++;

            if (neo is not null)
            {
                var neoSteps = neo.Steps.Count > 0
                    ? string.Join(" -> ", neo.Steps.Select(step => $"{step.FromSymbol}->{step.ToSymbol} via {SourceLabel(step.Src)} pool {step.PairId}"))
                    : $"{SourceLabel(neo.Src)} pool {neo.PairId}";
                AddRouteQuote(
                    quotes,
                    "NeoOnEdge",
                    neo.Output,
                    outputPrecision,
                    neo.Memo,
                    cfg.ContractAccount,
                    Math.Max(1, neo.Steps.Count),
                    neoSteps,
                    [new RouteLeg("NeoOnEdge", ToTokenAmount(rawAmount, inputPrecision), ParseRouteAmount(neo.Output), Math.Max(1, neo.Steps.Count), neoSteps)]);
            }
        }

        if (includeNeoRoutes && quotes.Count >= 2)
        {
            var split = await GetBestSplitQuoteAsync(
                alcorRouter,
                neoRouter,
                input,
                output,
                rawAmount,
                inputPrecision,
                outputPrecision,
                cfg,
                maxHops,
                receiver,
                ct);

            lookupCount += split.LookupCount;
            var bestDirectOutput = quotes.Max(x => x.OutputAmount);
            if (split.Route is not null && IsMeaningfullyBetter(split.Route.OutputAmount, bestDirectOutput))
                quotes.Add(split.Route);
        }

        return new QuoteCandidateSet(quotes
            .OrderByDescending(x => x.OutputAmount)
            .ToArray(), lookupCount);
    }

    private async Task<SplitQuoteResult> GetBestSplitQuoteAsync(
        AlcorRouterService alcorRouter,
        NeoRouteQuoteService neoRouter,
        string input,
        string output,
        long rawAmount,
        int inputPrecision,
        int outputPrecision,
        AppSettings cfg,
        int maxHops,
        string receiver,
        CancellationToken ct)
    {
        int[] alcorPercentages = [25, 50, 75];
        RouteQuote? best = null;
        int lookupCount = 0;

        foreach (var alcorPercent in alcorPercentages.Take(MaxSplitLegs))
        {
            var alcorRaw = rawAmount * alcorPercent / 100;
            var neoRaw = rawAmount - alcorRaw;
            if (alcorRaw <= 0 || neoRaw <= 0)
                continue;

            var alcor = await alcorRouter.GetRouteAsync(
                input,
                output,
                alcorRaw,
                inputPrecision,
                receiver,
                cfg.ArbitrageSlippage,
                maxHops,
                ct);
            lookupCount++;

            var neo = await neoRouter.GetRouteAsync(
                input,
                output,
                neoRaw,
                receiver,
                inputPrecision,
                outputPrecision,
                cfg.ArbitrageSlippage,
                maxHops,
                cfg.ContractAccount,
                ct);
            lookupCount++;

            if (alcor is null || neo is null)
                continue;

            var alcorOutput = ParseRouteAmount(alcor.Output);
            var neoOutput = ParseRouteAmount(neo.Output);
            if (alcorOutput <= 0 || neoOutput <= 0)
                continue;

            var alcorSteps = alcor.Route.Length > 0
                ? string.Join(" -> ", alcor.Route.Select(x => $"pool {x}"))
                : "router";
            var neoSteps = neo.Steps.Count > 0
                ? string.Join(" -> ", neo.Steps.Select(step => $"{step.FromSymbol}->{step.ToSymbol} via {SourceLabel(step.Src)} pool {step.PairId}"))
                : $"{SourceLabel(neo.Src)} pool {neo.PairId}";

            var legs = new[]
            {
                new RouteLeg("Alcor", ToTokenAmount(alcorRaw, inputPrecision), alcorOutput, Math.Max(1, alcor.Route.Length), alcorSteps),
                new RouteLeg("NeoOnEdge", ToTokenAmount(neoRaw, inputPrecision), neoOutput, Math.Max(1, neo.Steps.Count), neoSteps),
            };

            var totalOutput = alcorOutput + neoOutput;
            var outputRaw = ToRawAmount(totalOutput, outputPrecision);
            if (outputRaw is null)
                continue;

            var quote = new RouteQuote(
                "Split",
                totalOutput,
                outputRaw.Value,
                FormatTokenAmount(totalOutput, outputPrecision),
                "split-dry-run",
                "",
                legs.Length,
                FormatSplitSteps(legs),
                legs);

            if (best is null || quote.OutputAmount > best.OutputAmount)
                best = quote;
        }

        return new SplitQuoteResult(best, lookupCount);
    }

    private static void AddRouteQuote(
        ICollection<RouteQuote> quotes,
        string source,
        string output,
        int outputPrecision,
        string memo,
        string contract,
        int hops,
        string steps,
        IReadOnlyList<RouteLeg> legs)
    {
        decimal outputAmount = ParseRouteAmount(output);
        var outputRaw = ToRawAmount(outputAmount, outputPrecision);
        if (outputRaw is null || outputAmount <= 0)
            return;

        quotes.Add(new RouteQuote(source, outputAmount, outputRaw.Value, output, memo, contract, hops, steps, legs));
    }

    private async Task TryExecuteTopOpportunityAsync(
        IReadOnlyList<ArbitrageOpportunity> opportunities,
        TokenCatalogItem wax,
        AlcorRouterService alcorRouter,
        NeoRouteQuoteService neoRouter,
        AppSettings cfg,
        CancellationToken ct)
    {
        if (!cfg.ArbitrageExecutionEnabled || !execution.IsConfigured)
            return;

        var opportunity = opportunities.FirstOrDefault(x =>
            x.InputWax <= cfg.ArbitrageMaxExecutionWax &&
            IsDirectExecutableRoute(x.BuyRoute) &&
            IsDirectExecutableRoute(x.SellRoute));
        if (opportunity is null)
            return;

        var cooldown = TimeSpan.FromMinutes(Math.Clamp(cfg.ArbitrageExecutionCooldownMinutes, 1, 1_440));
        var lockKey = $"arbitrage:execution:{opportunity.Token.Id}";
        if (!await _cache.StringSetAsync(lockKey, "1", cooldown, When.NotExists))
            return;

        try
        {
            var beforeTokenBalance = await execution.GetBalanceAsync(opportunity.Token, ct);
            var inputRaw = ToRawAmount(opportunity.InputWax, WaxPrecision)
                ?? throw new InvalidOperationException("Could not convert arbitrage WAX input to a raw amount.");

            var buyTransaction = await execution.TransferAsync(
                wax,
                inputRaw,
                opportunity.BuyRoute.Contract,
                opportunity.BuyRoute.Memo,
                ct);

            var receivedToken = await WaitForBalanceIncreaseAsync(
                opportunity.Token,
                beforeTokenBalance,
                cfg.ArbitrageBalanceWaitSeconds,
                ct);
            var receivedRaw = ToRawAmount(receivedToken, opportunity.Token.Precision);
            if (receivedRaw is null)
                throw new InvalidOperationException($"Buy transaction {buyTransaction} produced no observable {opportunity.Token.Symbol} balance increase.");

            var sellCandidates = await GetQuoteCandidatesAsync(
                alcorRouter,
                neoRouter,
                opportunity.Token.Id,
                wax.Id,
                receivedRaw.Value,
                opportunity.Token.Precision,
                WaxPrecision,
                cfg,
                IsTokenSafeForNeoArbitrage(opportunity.Token),
                ct);
            var sellRoute = sellCandidates.Routes
                .Where(IsDirectExecutableRoute)
                .OrderByDescending(x => x.OutputAmount)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No executable unwind route was available after the buy leg.");

            var sellTransaction = await execution.TransferAsync(
                opportunity.Token,
                receivedRaw.Value,
                sellRoute.Contract,
                sellRoute.Memo,
                ct);

            log.LogWarning(
                "Executed arbitrage round trip for {Token}: input={InputWax} WAX expectedFinal={ExpectedFinalWax} WAX buyTrx={BuyTransaction} sellTrx={SellTransaction}",
                opportunity.Token.Id,
                opportunity.InputWax,
                sellRoute.OutputAmount,
                buyTransaction,
                sellTransaction);

            await SendDiscordMessage(
                $"**NeoOnEdge arbitrage executed**\n" +
                $"{FormatWax(opportunity.InputWax)} WAX -> {opportunity.Token.Symbol} -> expected {FormatWax(sellRoute.OutputAmount)} WAX\n" +
                $"Buy: `{buyTransaction}`\nSell: `{sellTransaction}`",
                ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Automatic arbitrage execution failed for {Token}", opportunity.Token.Id);
            await SendDiscordMessage(
                $"**NeoOnEdge automatic arbitrage failed**\nToken: {opportunity.Token.Id}\nError: {ex.Message}",
                ct);
        }
    }

    private async Task<decimal> WaitForBalanceIncreaseAsync(
        TokenCatalogItem token,
        decimal previousBalance,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 120));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var balance = await execution.GetBalanceAsync(token, ct);
            if (balance > previousBalance)
                return balance - previousBalance;
        }

        return 0;
    }

    private async Task PostOpportunities(
        IReadOnlyList<ArbitrageOpportunity> opportunities,
        int tokenCount,
        int routeLookups,
        CancellationToken ct)
    {
        var cfg = settings.Value;
        string signature = string.Join("|", opportunities.Select(x => string.Join(':',
            x.Token.Id,
            x.InputWax.ToString("0.########", CultureInfo.InvariantCulture),
            x.BuyRoute.Source,
            x.SellRoute.Source,
            Math.Round(x.ProfitWax, 4),
            Math.Round(x.ProfitPercent, 3))));

        string key = $"arbitrage:discord:opportunity:{Hash(signature)}";
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(cfg.ArbitrageAlertCooldownMinutes, 1, 1_440));
        if (!await _cache.StringSetAsync(key, "1", cooldown, When.NotExists))
            return;

        var lines = new List<string>
        {
            "**NeoOnEdge dry-run arbitrage monitor**",
            "No trade was executed. These are quote calculations only.",
            $"Scanned {tokenCount} tokens, {routeLookups} route quote attempts, max probe {FormatWax(cfg.ArbitrageMaxInputWax)} WAX.",
            $"Threshold: +{cfg.ArbitrageMinProfitWax.ToString("0.########", CultureInfo.InvariantCulture)} WAX and +{cfg.ArbitrageMinProfitPercent.ToString("0.###", CultureInfo.InvariantCulture)}%.",
            ""
        };

        int index = 1;
        foreach (var opportunity in opportunities)
        {
            lines.Add(
                $"#{index} **WAX -> {opportunity.Token.Symbol} -> WAX** | " +
                $"{FormatWax(opportunity.InputWax)} WAX -> {FormatWax(opportunity.FinalWax)} WAX | " +
                $"+{FormatWax(opportunity.ProfitWax)} WAX (+{opportunity.ProfitPercent.ToString("0.###", CultureInfo.InvariantCulture)}%)");
            lines.Add(
                $"Buy: {FormatRouteForDiscord(opportunity.BuyRoute)}. " +
                $"Sell: {FormatRouteForDiscord(opportunity.SellRoute)}.");
            index++;
        }

        await SendDiscordMessage(string.Join('\n', lines), ct);
    }

    private async Task PostNoOpportunityHeartbeat(int tokenCount, int routeLookups, CancellationToken ct)
    {
        var cfg = settings.Value;
        int heartbeatMinutes = Math.Clamp(cfg.ArbitrageNoOpportunityHeartbeatMinutes, 0, 10_080);
        if (heartbeatMinutes == 0)
            return;

        const string key = "arbitrage:discord:no-opportunity-heartbeat";
        if (!await _cache.StringSetAsync(key, "1", TimeSpan.FromMinutes(heartbeatMinutes), When.NotExists))
            return;

        string content = string.Join('\n',
            "**NeoOnEdge dry-run arbitrage monitor**",
            "No trade was executed. These are quote calculations only.",
            $"No opportunities above threshold in this scan. Scanned {tokenCount} tokens and made {routeLookups} route quote attempts.",
            $"Max probe: {FormatWax(cfg.ArbitrageMaxInputWax)} WAX. Next heartbeat will wait at least {heartbeatMinutes} minutes.");

        await SendDiscordMessage(content, ct);
    }

    private async Task SendDiscordMessage(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Value.ArbitrageDiscordWebhookUrl))
            return;

        if (content.Length > 1_900)
            content = content[..1_850] + "\n...truncated";

        using var http = httpFactory.CreateClient();
        using var response = await http.PostAsJsonAsync(
            settings.Value.ArbitrageDiscordWebhookUrl,
            new
            {
                username = "NeoOnEdge Arbitrage Monitor",
                content
            },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning(
                "Discord arbitrage webhook returned HTTP {StatusCode}",
                (int)response.StatusCode);
        }
    }

    private static IReadOnlyList<decimal> GetProbeAmounts(AppSettings cfg)
    {
        decimal maxWax = Math.Clamp(cfg.ArbitrageMaxInputWax, 0.00000001m, 100m);
        var configured = cfg.ArbitrageProbeWaxAmounts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0)
            .Where(x => x > 0 && x <= maxWax)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        return configured.Length > 0 ? configured : [maxWax];
    }

    private static decimal ParseRouteAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var amount = value.Trim();
        int space = amount.IndexOf(' ');
        if (space > 0)
            amount = amount[..space];

        return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static long? ToRawAmount(decimal amount, int precision)
    {
        decimal scale = Pow10(precision);
        decimal raw = decimal.Floor(amount * scale);
        if (raw <= 0 || raw > long.MaxValue)
            return null;

        return (long)raw;
    }

    private static decimal Pow10(int precision)
    {
        precision = Math.Clamp(precision, 0, 18);
        decimal result = 1m;
        for (int i = 0; i < precision; i++)
            result *= 10m;
        return result;
    }

    private static decimal ToTokenAmount(long rawAmount, int precision)
        => rawAmount / Pow10(precision);

    private static string FormatTokenAmount(decimal value, int precision)
        => value.ToString($"F{Math.Clamp(precision, 0, 18)}", CultureInfo.InvariantCulture);

    private static int EffectiveArbitrageMaxHops(AppSettings cfg)
        => Math.Clamp(cfg.ArbitrageMaxHops, 1, MaxArbitrageHops);

    private static bool IsMeaningfullyBetter(decimal candidate, decimal baseline)
        => baseline <= 0 || candidate >= baseline * (1 + MinSplitImprovementPercent / 100m);

    private static string FormatSplitSteps(IReadOnlyList<RouteLeg> legs)
        => string.Join(" + ", legs.Select(leg =>
            $"{FormatWax(leg.InputAmount)} via {leg.Source} => {FormatWax(leg.OutputAmount)} ({leg.Steps})"));

    private static string FormatRouteForDiscord(RouteQuote route)
    {
        if (route.Source.Equals("Split", StringComparison.OrdinalIgnoreCase))
            return $"Split, {route.Legs.Count} leg(s), {route.Steps}";

        return $"{route.Source}, {route.Hops} hop(s), {route.Steps}";
    }

    private static bool IsWaxToken(TokenCatalogItem token)
        => token.Symbol.Equals("WAX", StringComparison.OrdinalIgnoreCase) &&
           token.Contract.Equals("eosio.token", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeRouteCombination(TokenCatalogItem token, RouteQuote buyRoute, RouteQuote sellRoute)
    {
        if (!UsesNeoRoute(buyRoute) && !UsesNeoRoute(sellRoute))
            return true;

        return IsTokenSafeForNeoArbitrage(token);
    }

    private static bool IsTokenSafeForNeoArbitrage(TokenCatalogItem token)
        => NeoArbitrageSafeTokenIds.Contains(token.Id) ||
           NeoArbitrageSafeTokenIds.Contains($"{token.Symbol.ToLowerInvariant()}-{token.Contract}");

    private static bool IsAlertSafeOpportunity(ArbitrageOpportunity opportunity)
    {
        if (!IsSafeRouteCombination(opportunity.Token, opportunity.BuyRoute, opportunity.SellRoute))
            return false;

        if (opportunity.ProfitPercent <= 0 || opportunity.ProfitPercent > MaxReasonableDryRunProfitPercent)
            return false;

        if (opportunity.FinalWax <= 0 || opportunity.InputWax <= 0)
            return false;

        var maxFinalWax = opportunity.InputWax * (1 + MaxReasonableDryRunProfitPercent / 100m);
        return opportunity.FinalWax <= maxFinalWax;
    }

    private static bool UsesNeoRoute(RouteQuote route)
        => route.Source.Equals("NeoOnEdge", StringComparison.OrdinalIgnoreCase) ||
           route.Legs.Any(leg => leg.Source.Equals("NeoOnEdge", StringComparison.OrdinalIgnoreCase));

    private static bool IsDirectExecutableRoute(RouteQuote route)
        => !route.Source.Equals("Split", StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(route.Contract) &&
           !string.IsNullOrWhiteSpace(route.Memo);

    private static string FormatWax(decimal value)
        => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string SourceLabel(string src) => src switch
    {
        "a2" => "Alcor v2",
        "ad" => "Alcor order book",
        "d" => "Defibox",
        "t" => "Taco",
        "a" => "ADEX",
        "n" => "NeftyBlocks",
        "wf" => "WaxFusion",
        _ => src.ToUpperInvariant()
    };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private sealed record RouteQuote(
        string Source,
        decimal OutputAmount,
        long OutputRaw,
        string OutputDisplay,
        string Memo,
        string Contract,
        int Hops,
        string Steps,
        IReadOnlyList<RouteLeg> Legs);

    private sealed record RouteLeg(
        string Source,
        decimal InputAmount,
        decimal OutputAmount,
        int Hops,
        string Steps);

    private sealed record QuoteCandidateSet(IReadOnlyList<RouteQuote> Routes, int LookupCount);

    private sealed record SplitQuoteResult(RouteQuote? Route, int LookupCount);

    private sealed record ArbitrageOpportunity(
        TokenCatalogItem Token,
        decimal InputWax,
        decimal FinalWax,
        decimal ProfitWax,
        decimal ProfitPercent,
        RouteQuote BuyRoute,
        RouteQuote SellRoute);
}
