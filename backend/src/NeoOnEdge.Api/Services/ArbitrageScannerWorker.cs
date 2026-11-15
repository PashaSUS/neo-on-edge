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
    ArbitrageExecutionService execution,
    ArbitrageScannerState state) : BackgroundService
{
    private const int WaxPrecision = 8;
    private const int MaxArbitrageHops = 3;
    private const int MaxSplitLegs = 3;
    private const decimal MaxReasonableDryRunProfitPercent = 25m;
    private const decimal MinSplitImprovementPercent = 0.05m;

    private readonly IDatabase _cache = redis.GetDatabase();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = settings.Value;
        var intervalSeconds = Math.Clamp(cfg.ArbitrageScanIntervalSeconds, 60, 86_400);
        state.MarkConfigured(
            cfg.ArbitrageScannerEnabled,
            !string.IsNullOrWhiteSpace(cfg.ArbitrageDiscordWebhookUrl),
            cfg.ArbitrageExecutionEnabled,
            execution.IsConfigured,
            intervalSeconds);

        if (!cfg.ArbitrageScannerEnabled)
        {
            log.LogInformation("Arbitrage scanner is disabled");
            state.MarkSkipped("Scanner is disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.ArbitrageDiscordWebhookUrl) &&
            !cfg.ArbitrageExecutionEnabled)
        {
            log.LogWarning("Arbitrage scanner is enabled without Discord alerts or automatic execution; scans will still run and only write logs/status");
        }

        if (cfg.ArbitrageExecutionEnabled && !execution.IsConfigured)
        {
            log.LogError("Automatic arbitrage execution is enabled but the account or executor secret is missing");
            state.MarkSkipped("Automatic execution is enabled but the account or executor secret is missing.");
            return;
        }

        if (cfg.ArbitrageExecutionEnabled)
        {
            var preflight = await execution.GetExecutorPreflightAsync(forceRefresh: true, ct);
            state.MarkExecutorPreflight(preflight);
            if (!preflight.Ready)
            {
                log.LogError(
                    "Automatic execution preflight failed at {Stage}: {Error}. Quote scanning will continue without submitting trades until preflight succeeds.",
                    preflight.Stage,
                    preflight.Error);
            }
        }

        log.LogInformation(
            "Arbitrage scanner starting: intervalSeconds={IntervalSeconds}, maxInputWax={MaxInputWax}, maxTokensPerScan={MaxTokensPerScan}, discordConfigured={DiscordConfigured}, executionEnabled={ExecutionEnabled}",
            intervalSeconds,
            cfg.ArbitrageMaxInputWax,
            cfg.ArbitrageMaxTokensPerScan,
            !string.IsNullOrWhiteSpace(cfg.ArbitrageDiscordWebhookUrl),
            cfg.ArbitrageExecutionEnabled);

        await RunScanSafely(ct);

        var interval = TimeSpan.FromSeconds(intervalSeconds);
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
            state.MarkScanFailed(ex);
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
            state.MarkSkipped("Scanner has no WAX probe amounts to evaluate.");
            return;
        }

        state.MarkScanStarted();
        log.LogInformation(
            "Arbitrage scan started: probeAmounts={ProbeAmounts}, maxTokensPerScan={MaxTokensPerScan}, minProfitWax={MinProfitWax}, minProfitPercent={MinProfitPercent}",
            string.Join(',', probeAmounts.Select(FormatWax)),
            cfg.ArbitrageMaxTokensPerScan,
            cfg.ArbitrageMinProfitWax,
            cfg.ArbitrageMinProfitPercent);

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

        log.LogInformation(
            "Arbitrage scan loaded {CandidateCount} candidate tokens from {TokenCount} catalog tokens",
            candidates.Length,
            tokens.Count);

        var opportunities = new List<ArbitrageOpportunity>();
        int routeLookups = 0;
        int rejectedUnsafeNeoRoutes = 0;
        int rejectedSuspiciousRoundTrips = 0;
        int neoSuppressedForContractSafety = 0;
        var routeDiagnostics = new ArbitrageRouteDiagnostics();
        int delayMs = Math.Clamp(cfg.ArbitrageRequestDelayMilliseconds, 0, 5_000);

        foreach (var token in candidates)
        {
            // TokenCatalogService already removes the durable route_excluded_tokens deny-list.
            // Intermediate NeoOnEdge hops are independently checked by NeoRouteQuoteService.
            const bool includeNeoRoutes = true;

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
                    ct,
                    routeDiagnostics);

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
                        ct,
                        routeDiagnostics);

                    routeLookups += sellCandidates.LookupCount;

                    foreach (var sellRoute in sellCandidates.Routes)
                    {
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
        LogRouteDiagnostics(routeDiagnostics);

        if (top.Length > 0)
        {
            var executionResult = await TryExecuteTopOpportunityAsync(top, wax, alcorRouter, neoRouter, cfg, ct);
            await PostOpportunities(top, candidates.Length, routeLookups, executionResult, ct);
            state.MarkScanCompleted(
                candidates.Length,
                routeLookups,
                opportunities.Count,
                top.Length,
                rejectedUnsafeNeoRoutes,
                rejectedSuspiciousRoundTrips,
                neoSuppressedForContractSafety,
                routeDiagnostics.ComparableQuoteCount,
                routeDiagnostics.NeoBetterQuoteCount,
                routeDiagnostics.BestImprovementPercent,
                routeDiagnostics.BestComparison);
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
        state.MarkScanCompleted(
            candidates.Length,
            routeLookups,
            opportunities.Count,
            0,
            rejectedUnsafeNeoRoutes,
            rejectedSuspiciousRoundTrips,
            neoSuppressedForContractSafety,
            routeDiagnostics.ComparableQuoteCount,
            routeDiagnostics.NeoBetterQuoteCount,
            routeDiagnostics.BestImprovementPercent,
            routeDiagnostics.BestComparison);
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
        CancellationToken ct,
        ArbitrageRouteDiagnostics? routeDiagnostics = null,
        bool useAlcorCache = true,
        bool includeSplitRoutes = true,
        double? slippageOverride = null)
    {
        var receiver = cfg.ArbitrageExecutionEnabled && !string.IsNullOrWhiteSpace(cfg.ArbitrageAccount)
            ? cfg.ArbitrageAccount
            : cfg.ContractAccount;
        var quotes = new List<RouteQuote>(2);
        var maxHops = EffectiveArbitrageMaxHops(cfg);
        var lookupCount = 0;
        var slippage = slippageOverride ?? cfg.ArbitrageSlippage;

        routeDiagnostics?.RecordAttempt("Alcor");
        var alcor = useAlcorCache
            ? await alcorRouter.GetRouteAsync(
                input,
                output,
                rawAmount,
                inputPrecision,
                receiver,
                slippage,
                maxHops,
                ct)
            : await alcorRouter.GetFreshRouteAsync(
                input,
                output,
                rawAmount,
                inputPrecision,
                receiver,
                slippage,
                maxHops,
                ct);
        lookupCount++;

        if (alcor is not null)
        {
            routeDiagnostics?.RecordSuccess("Alcor");
            AddRouteQuote(
                quotes,
                "Alcor",
                rawAmount,
                alcor.Output,
                outputPrecision,
                alcor.Memo,
                "swap.alcor",
                Math.Max(1, alcor.Route.Length),
                alcor.Route.Length > 0 ? string.Join(" -> ", alcor.Route.Select(x => $"pool {x}")) : "router",
                [new RouteLeg("Alcor", rawAmount, ToTokenAmount(rawAmount, inputPrecision), ParseRouteAmount(alcor.Output), Math.Max(1, alcor.Route.Length), alcor.Route.Length > 0 ? string.Join(" -> ", alcor.Route.Select(x => $"pool {x}")) : "router", "swap.alcor", alcor.Memo)]);
        }
        else
        {
            routeDiagnostics?.RecordFailure(
                "Alcor",
                "Alcor router returned no route.",
                input,
                output,
                rawAmount,
                inputPrecision,
                useAlcorCache,
                "direct");
        }

        if (includeNeoRoutes)
        {
            routeDiagnostics?.RecordAttempt("NeoOnEdge");
            var neoQuote = await neoRouter.GetRouteQuoteAsync(
                    input,
                    output,
                    rawAmount,
                    receiver,
                    inputPrecision,
                    outputPrecision,
                    slippage,
                    maxHops,
                    cfg.ContractAccount,
                    ct,
                    readAlcorCache: useAlcorCache);
            var neo = neoQuote.Route;
            lookupCount++;

            if (neo is not null)
            {
                routeDiagnostics?.RecordSuccess("NeoOnEdge");
                var neoSteps = neo.Steps.Count > 0
                    ? string.Join(" -> ", neo.Steps.Select(step => $"{step.FromSymbol}->{step.ToSymbol} via {SourceLabel(step.Src)} pool {step.PairId}"))
                    : $"{SourceLabel(neo.Src)} pool {neo.PairId}";
                AddRouteQuote(
                    quotes,
                    "NeoOnEdge",
                    rawAmount,
                    neo.Output,
                    outputPrecision,
                    neo.Memo,
                    cfg.ContractAccount,
                    Math.Max(1, neo.Steps.Count),
                    neoSteps,
                    [new RouteLeg("NeoOnEdge", rawAmount, ToTokenAmount(rawAmount, inputPrecision), ParseRouteAmount(neo.Output), Math.Max(1, neo.Steps.Count), neoSteps, cfg.ContractAccount, neo.Memo)]);
            }
            else
            {
                routeDiagnostics?.RecordFailure(
                    "NeoOnEdge",
                    neoQuote.Reason ?? "NeoOnEdge route quote returned no route.",
                    input,
                    output,
                    rawAmount,
                    inputPrecision,
                    useAlcorCache,
                    "direct");
            }
        }

        var directAlcorQuote = quotes.FirstOrDefault(route =>
            route.Source.Equals("Alcor", StringComparison.OrdinalIgnoreCase));
        var directNeoQuote = quotes.FirstOrDefault(route =>
            route.Source.Equals("NeoOnEdge", StringComparison.OrdinalIgnoreCase));
        if (directAlcorQuote is not null && directNeoQuote is not null)
        {
            routeDiagnostics?.RecordQuoteComparison(
                directNeoQuote.Source,
                input,
                output,
                rawAmount,
                directNeoQuote.OutputAmount,
                directAlcorQuote.OutputAmount);
        }

        if (includeSplitRoutes && includeNeoRoutes && quotes.Count >= 2)
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
                ct,
                routeDiagnostics);

            lookupCount += split.LookupCount;
            var bestDirectOutput = quotes.Max(x => x.OutputAmount);
            if (split.Route is not null && directAlcorQuote is not null)
            {
                routeDiagnostics?.RecordQuoteComparison(
                    split.Route.Source,
                    input,
                    output,
                    rawAmount,
                    split.Route.OutputAmount,
                    directAlcorQuote.OutputAmount);
            }
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
        CancellationToken ct,
        ArbitrageRouteDiagnostics? routeDiagnostics = null)
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

            routeDiagnostics?.RecordAttempt("SplitAlcorLeg");
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

            routeDiagnostics?.RecordAttempt("SplitNeoLeg");
            var neoQuote = await neoRouter.GetRouteQuoteAsync(
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
            var neo = neoQuote.Route;
            lookupCount++;

            if (alcor is null)
            {
                routeDiagnostics?.RecordFailure(
                    "SplitAlcorLeg",
                    "Alcor router returned no split-leg route.",
                    input,
                    output,
                    alcorRaw,
                    inputPrecision,
                    readAlcorCache: true,
                    "split");
            }
            else
            {
                routeDiagnostics?.RecordSuccess("SplitAlcorLeg");
            }

            if (neo is null)
            {
                routeDiagnostics?.RecordFailure(
                    "SplitNeoLeg",
                    neoQuote.Reason ?? "NeoOnEdge split leg returned no route.",
                    input,
                    output,
                    neoRaw,
                    inputPrecision,
                    readAlcorCache: true,
                    "split");
            }
            else
            {
                routeDiagnostics?.RecordSuccess("SplitNeoLeg");
            }

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
                new RouteLeg("Alcor", alcorRaw, ToTokenAmount(alcorRaw, inputPrecision), alcorOutput, Math.Max(1, alcor.Route.Length), alcorSteps, "swap.alcor", alcor.Memo),
                new RouteLeg("NeoOnEdge", neoRaw, ToTokenAmount(neoRaw, inputPrecision), neoOutput, Math.Max(1, neo.Steps.Count), neoSteps, cfg.ContractAccount, neo.Memo),
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
        long inputRaw,
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

        var executableLegs = legs
            .Select(leg => leg with
            {
                InputRaw = leg.InputRaw > 0 ? leg.InputRaw : inputRaw,
                Contract = string.IsNullOrWhiteSpace(leg.Contract) ? contract : leg.Contract,
                Memo = string.IsNullOrWhiteSpace(leg.Memo) ? memo : leg.Memo
            })
            .ToArray();

        quotes.Add(new RouteQuote(source, outputAmount, outputRaw.Value, output, memo, contract, hops, steps, executableLegs));
    }

    private async Task<ExecutionPlan?> GetFreshExecutionPlanAsync(
        ArbitrageOpportunity opportunity,
        TokenCatalogItem wax,
        AlcorRouterService alcorRouter,
        NeoRouteQuoteService neoRouter,
        AppSettings cfg,
        CancellationToken ct)
    {
        var inputRaw = ToRawAmount(opportunity.InputWax, WaxPrecision);
        if (inputRaw is null)
            return null;

        var executionSlippage = Math.Clamp(cfg.ArbitrageExecutionSlippage, 0.1, 25.0);
        const bool includeNeoRoutes = true;
        var buyCandidates = await GetQuoteCandidatesAsync(
            alcorRouter,
            neoRouter,
            wax.Id,
            opportunity.Token.Id,
            inputRaw.Value,
            WaxPrecision,
            opportunity.Token.Precision,
            cfg,
            includeNeoRoutes,
            ct,
            useAlcorCache: false,
            includeSplitRoutes: true,
            slippageOverride: executionSlippage);

        ExecutionPlan? best = null;
        foreach (var buyRoute in buyCandidates.Routes.Where(IsAutoExecutableRoute))
        {
            var sellCandidates = await GetQuoteCandidatesAsync(
                alcorRouter,
                neoRouter,
                opportunity.Token.Id,
                wax.Id,
                buyRoute.OutputRaw,
                opportunity.Token.Precision,
                WaxPrecision,
                cfg,
                includeNeoRoutes,
                ct,
                useAlcorCache: false,
                includeSplitRoutes: true,
                slippageOverride: executionSlippage);

            var sellRoute = sellCandidates.Routes
                .Where(IsAutoExecutableRoute)
                .OrderByDescending(route => route.OutputAmount)
                .FirstOrDefault();
            if (sellRoute is null)
                continue;

            var profitWax = sellRoute.OutputAmount - opportunity.InputWax;
            var profitPercent = opportunity.InputWax > 0
                ? profitWax / opportunity.InputWax * 100m
                : 0;
            if (profitWax < cfg.ArbitrageMinProfitWax ||
                profitPercent < cfg.ArbitrageMinProfitPercent ||
                profitPercent > MaxReasonableDryRunProfitPercent)
                continue;

            var candidate = new ExecutionPlan(buyRoute, sellRoute, executionSlippage);
            if (best is null || candidate.SellRoute.OutputAmount > best.SellRoute.OutputAmount)
                best = candidate;
        }

        return best;
    }

    private async Task<ExecutionAttemptResult> TryExecuteTopOpportunityAsync(
        IReadOnlyList<ArbitrageOpportunity> opportunities,
        TokenCatalogItem wax,
        AlcorRouterService alcorRouter,
        NeoRouteQuoteService neoRouter,
        AppSettings cfg,
        CancellationToken ct)
    {
        if (!cfg.ArbitrageExecutionEnabled || !execution.IsConfigured)
        {
            log.LogInformation("Automatic arbitrage execution is disabled; scanner will only publish quote results");
            return new ExecutionAttemptResult(false, false, "Automatic execution is disabled for this scanner.");
        }

        var opportunity = opportunities.FirstOrDefault(x =>
            x.InputWax <= cfg.ArbitrageMaxExecutionWax &&
            IsAutoExecutableRoute(x.BuyRoute) &&
            IsAutoExecutableRoute(x.SellRoute));
        if (opportunity is null)
        {
            log.LogInformation(
                "Automatic arbitrage execution skipped because no opportunity met the executable-route and max-execution filters (maxExecutionWax={MaxExecutionWax})",
                cfg.ArbitrageMaxExecutionWax);
            return new ExecutionAttemptResult(false, false, "No opportunity met the automatic execution rules in this scan.");
        }

        var preflight = await execution.GetExecutorPreflightAsync(forceRefresh: false, ct);
        state.MarkExecutorPreflight(preflight);
        if (!preflight.Ready)
        {
            log.LogError(
                "Automatic arbitrage execution skipped because executor preflight failed at {Stage}: {Error}",
                preflight.Stage,
                preflight.Error);
            return new ExecutionAttemptResult(
                false,
                false,
                $"Automatic execution skipped: executor preflight failed at {preflight.Stage}: {preflight.Error ?? "unknown error"}.");
        }

        var cooldown = TimeSpan.FromMinutes(Math.Clamp(cfg.ArbitrageExecutionCooldownMinutes, 1, 1_440));
        var lockKey = $"arbitrage:execution:{opportunity.Token.Id}";
        if (!await _cache.StringSetAsync(lockKey, "1", cooldown, When.NotExists))
        {
            log.LogInformation(
                "Automatic arbitrage execution skipped for {Token} because the cooldown lock is active",
                opportunity.Token.Id);
            return new ExecutionAttemptResult(false, false, $"Execution cooldown is active for {opportunity.Token.Symbol} ({opportunity.Token.Id}).");
        }

        try
        {
            var executionPlan = await GetFreshExecutionPlanAsync(
                opportunity,
                wax,
                alcorRouter,
                neoRouter,
                cfg,
                ct);
            if (executionPlan is null)
            {
                log.LogInformation(
                    "Skipped stale arbitrage opportunity for {Token}; fresh execution quotes no longer met the profit threshold",
                    opportunity.Token.Id);
                return new ExecutionAttemptResult(true, false, $"Fresh execution quotes for {opportunity.Token.Symbol} ({opportunity.Token.Id}) no longer met the profit threshold.");
            }

            var beforeTokenBalance = await execution.GetBalanceAsync(opportunity.Token, ct);
            var inputRaw = ToRawAmount(opportunity.InputWax, WaxPrecision)
                ?? throw new InvalidOperationException("Could not convert arbitrage WAX input to a raw amount.");

            var buyTransactions = await ExecuteRouteAsync(
                wax,
                inputRaw,
                executionPlan.BuyRoute,
                ct);

            var receivedToken = await WaitForBalanceIncreaseAsync(
                opportunity.Token,
                beforeTokenBalance,
                cfg.ArbitrageBalanceWaitSeconds,
                ct);
            var receivedRaw = ToRawAmount(receivedToken, opportunity.Token.Precision);
            if (receivedRaw is null)
                throw new InvalidOperationException($"Buy transactions {string.Join(", ", buyTransactions)} produced no observable {opportunity.Token.Symbol} balance increase.");

            var sellCandidates = await GetQuoteCandidatesAsync(
                alcorRouter,
                neoRouter,
                opportunity.Token.Id,
                wax.Id,
                receivedRaw.Value,
                opportunity.Token.Precision,
                WaxPrecision,
                cfg,
                includeNeoRoutes: true,
                ct,
                useAlcorCache: false,
                includeSplitRoutes: true,
                slippageOverride: executionPlan.Slippage);
            var sellRoute = sellCandidates.Routes
                .Where(IsAutoExecutableRoute)
                .OrderByDescending(x => x.OutputAmount)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No executable unwind route was available after the buy leg.");

            var sellTransactions = await ExecuteRouteAsync(
                opportunity.Token,
                receivedRaw.Value,
                sellRoute,
                ct);

            log.LogWarning(
                "Executed arbitrage round trip for {Token}: input={InputWax} WAX expectedFinal={ExpectedFinalWax} WAX buyTrx={BuyTransaction} sellTrx={SellTransaction}",
                opportunity.Token.Id,
                opportunity.InputWax,
                sellRoute.OutputAmount,
                string.Join(", ", buyTransactions),
                string.Join(", ", sellTransactions));

            await SendDiscordMessage(
                $"**NeoOnEdge arbitrage executed**\n" +
                $"{FormatWax(opportunity.InputWax)} WAX -> {opportunity.Token.Symbol} -> expected {FormatWax(sellRoute.OutputAmount)} WAX\n" +
                $"Buy: `{string.Join(", ", buyTransactions)}`\nSell: `{string.Join(", ", sellTransactions)}`",
                ct);
            return new ExecutionAttemptResult(
                true,
                true,
                $"Automatic execution submitted for {opportunity.Token.Symbol}: buy `{string.Join(", ", buyTransactions)}`, sell `{string.Join(", ", sellTransactions)}`.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Automatic arbitrage execution failed for {Token}", opportunity.Token.Id);
            await SendDiscordMessage(
                $"**NeoOnEdge automatic arbitrage failed**\nToken: {opportunity.Token.Id}\nError: {ex.Message}",
                ct);
            return new ExecutionAttemptResult(true, false, $"Automatic execution failed for {opportunity.Token.Symbol} ({opportunity.Token.Id}): {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> ExecuteRouteAsync(
        TokenCatalogItem token,
        long expectedInputRaw,
        RouteQuote route,
        CancellationToken ct)
    {
        if (route.Source.Equals("Split", StringComparison.OrdinalIgnoreCase))
        {
            var executableLegs = route.Legs
                .Where(leg => leg.InputRaw > 0)
                .ToArray();
            if (executableLegs.Length == 0)
                throw new InvalidOperationException("Split route has no executable legs.");

            var totalRaw = executableLegs.Sum(leg => leg.InputRaw);
            if (totalRaw != expectedInputRaw)
                throw new InvalidOperationException($"Split route input mismatch. Expected {expectedInputRaw}, got {totalRaw}.");

            var transactionIds = new List<string>(executableLegs.Length);
            foreach (var leg in executableLegs)
            {
                if (string.IsNullOrWhiteSpace(leg.Contract) || string.IsNullOrWhiteSpace(leg.Memo))
                    throw new InvalidOperationException($"Split route leg {leg.Source} is missing execution metadata.");

                var transactionId = await execution.TransferAsync(
                    token,
                    leg.InputRaw,
                    leg.Contract,
                    leg.Memo,
                    ct);
                transactionIds.Add(transactionId);
            }

            return transactionIds;
        }

        if (string.IsNullOrWhiteSpace(route.Contract) || string.IsNullOrWhiteSpace(route.Memo))
            throw new InvalidOperationException("Route is missing execution metadata.");

        return
        [
            await execution.TransferAsync(
                token,
                expectedInputRaw,
                route.Contract,
                route.Memo,
                ct)
        ];
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
        ExecutionAttemptResult executionResult,
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
            executionResult.Attempted || executionResult.Executed
                ? "**NeoOnEdge arbitrage monitor**"
                : "**NeoOnEdge quote-only arbitrage monitor**",
            executionResult.Summary,
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
            cfg.ArbitrageExecutionEnabled && execution.IsConfigured
                ? "**NeoOnEdge arbitrage monitor**"
                : "**NeoOnEdge quote-only arbitrage monitor**",
            cfg.ArbitrageExecutionEnabled && execution.IsConfigured
                ? "Automatic execution is enabled, but no opportunity qualified in this scan."
                : "Automatic execution is disabled. These are quote calculations only.",
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

    private void LogRouteDiagnostics(ArbitrageRouteDiagnostics diagnostics)
    {
        if (diagnostics.TotalAttempts == 0)
            return;

        log.LogInformation(
            "Arbitrage route diagnostics: attempts={Attempts}, successes={Successes}, failures={Failures}, comparableQuotes={ComparableQuotes}, neoBetterQuotes={NeoBetterQuotes}, bestImprovementPercent={BestImprovementPercent}, bestComparison={BestComparison}, attemptsBySource={AttemptsBySource}, failuresByReason={FailuresByReason}",
            diagnostics.TotalAttempts,
            diagnostics.TotalSuccesses,
            diagnostics.TotalFailures,
            diagnostics.ComparableQuoteCount,
            diagnostics.NeoBetterQuoteCount,
            diagnostics.BestImprovementPercent,
            diagnostics.BestComparison,
            diagnostics.FormatAttemptsBySource(),
            diagnostics.FormatFailuresByReason());

        foreach (var sample in diagnostics.Samples)
        {
            log.LogInformation(
                "Arbitrage route rejection sample: source={Source}, phase={Phase}, input={Input}, output={Output}, inputRaw={InputRaw}, inputAmount={InputAmount}, reason={Reason}, readAlcorCache={ReadAlcorCache}",
                sample.Source,
                sample.Phase,
                sample.Input,
                sample.Output,
                sample.InputRaw,
                FormatTokenAmount(ToTokenAmount(sample.InputRaw, sample.InputPrecision), sample.InputPrecision),
                sample.Reason,
                sample.ReadAlcorCache);
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

    private static bool IsAlertSafeOpportunity(ArbitrageOpportunity opportunity)
    {
        if (opportunity.ProfitPercent <= 0 || opportunity.ProfitPercent > MaxReasonableDryRunProfitPercent)
            return false;

        if (opportunity.FinalWax <= 0 || opportunity.InputWax <= 0)
            return false;

        var maxFinalWax = opportunity.InputWax * (1 + MaxReasonableDryRunProfitPercent / 100m);
        return opportunity.FinalWax <= maxFinalWax;
    }

    private static bool IsDirectExecutableRoute(RouteQuote route)
        => !route.Source.Equals("Split", StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(route.Contract) &&
           !string.IsNullOrWhiteSpace(route.Memo);

    private static bool IsAutoExecutableRoute(RouteQuote route)
        => IsDirectExecutableRoute(route);

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
        long InputRaw,
        decimal InputAmount,
        decimal OutputAmount,
        int Hops,
        string Steps,
        string Contract,
        string Memo);

    private sealed record QuoteCandidateSet(IReadOnlyList<RouteQuote> Routes, int LookupCount);

    private sealed record SplitQuoteResult(RouteQuote? Route, int LookupCount);

    private sealed record ExecutionPlan(RouteQuote BuyRoute, RouteQuote SellRoute, double Slippage);

    private sealed record ExecutionAttemptResult(bool Attempted, bool Executed, string Summary);

    private sealed class ArbitrageRouteDiagnostics
    {
        private const int MaxSamples = 20;
        private readonly Dictionary<string, int> attemptsBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> successesBySource = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> failuresByReason = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RouteFailureSample> samples = [];

        public int TotalAttempts => attemptsBySource.Values.Sum();
        public int TotalSuccesses => successesBySource.Values.Sum();
        public int TotalFailures => failuresByReason.Values.Sum();
        public int ComparableQuoteCount { get; private set; }
        public int NeoBetterQuoteCount { get; private set; }
        public decimal BestImprovementPercent { get; private set; }
        public string? BestComparison { get; private set; }
        public IReadOnlyList<RouteFailureSample> Samples => samples;

        public void RecordAttempt(string source)
            => Increment(attemptsBySource, source);

        public void RecordSuccess(string source)
            => Increment(successesBySource, source);

        public void RecordQuoteComparison(
            string candidateSource,
            string input,
            string output,
            long inputRaw,
            decimal candidateOutput,
            decimal alcorOutput)
        {
            if (candidateOutput <= 0 || alcorOutput <= 0)
                return;

            ComparableQuoteCount++;
            var improvementPercent = (candidateOutput - alcorOutput) / alcorOutput * 100m;
            if (improvementPercent <= 0)
                return;

            NeoBetterQuoteCount++;
            if (BestComparison is not null && improvementPercent <= BestImprovementPercent)
                return;

            BestImprovementPercent = improvementPercent;
            BestComparison = $"{candidateSource}: {input}->{output}, inputRaw={inputRaw}, improvement={improvementPercent:0.####}%";
        }

        public void RecordFailure(
            string source,
            string reason,
            string input,
            string output,
            long inputRaw,
            int inputPrecision,
            bool readAlcorCache,
            string phase)
        {
            Increment(failuresByReason, $"{source}: {reason}");
            if (samples.Count >= MaxSamples)
                return;

            samples.Add(new RouteFailureSample(
                source,
                phase,
                input,
                output,
                inputRaw,
                inputPrecision,
                readAlcorCache,
                reason));
        }

        public string FormatAttemptsBySource()
            => FormatCounts(attemptsBySource);

        public string FormatFailuresByReason()
            => FormatCounts(failuresByReason);

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        private static string FormatCounts(IReadOnlyDictionary<string, int> counts)
            => counts.Count == 0
                ? "none"
                : string.Join("; ", counts
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => $"{x.Key}={x.Value}"));
    }

    private sealed record RouteFailureSample(
        string Source,
        string Phase,
        string Input,
        string Output,
        long InputRaw,
        int InputPrecision,
        bool ReadAlcorCache,
        string Reason);

    private sealed record ArbitrageOpportunity(
        TokenCatalogItem Token,
        decimal InputWax,
        decimal FinalWax,
        decimal ProfitWax,
        decimal ProfitPercent,
        RouteQuote BuyRoute,
        RouteQuote SellRoute);
}
