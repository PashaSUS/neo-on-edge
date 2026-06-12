using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using StackExchange.Redis;

namespace NeoOnEdge.Indexer;

/// <summary>
/// Fast historical catch-up using Hyperion history API before attaching to SHIP.
/// Pages through /v2/history/get_actions for every watched contract.
/// </summary>
public static class HyperionCatchup
{
    private static readonly Watch[] Watches =
    [
        new("swap.alcor", "swap.alcor:logswap", ReplayTable.SwapV3, "a2"),
        new("swap.box", "swap.box:swaplog", ReplayTable.Swap, "d"),
        new("swap.taco", "swap.taco:exchangelog", ReplayTable.Swap, "t"),
        new("swap.nefty", "swap.nefty:logswap", ReplayTable.Swap, "n"),
        new("swap.adex", "swap.adex:swaplog", ReplayTable.Swap, "a"),
        new("alcordexmain", "alcordexmain:buymatch", ReplayTable.MarketMatch, "ad"),
        new("alcordexmain", "alcordexmain:sellmatch", ReplayTable.MarketMatch, "ad"),
        new("alcordexmain", "alcordexmain:orderfilled", ReplayTable.LimitFill, "ad"),
        new("alcordexmain", "alcordexmain:orderclosed", ReplayTable.LimitClose, "ad"),
    ];

    public static async Task RunAsync(
        AppSettings cfg, IServiceScopeFactory scopes, ISubscriber pub, ILogger log, CancellationToken ct)
    {
        if (!cfg.ReplayEnabled) return;

        using var http = new HttpClient
        {
            BaseAddress = new Uri(cfg.HyperionEndpoint),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.HyperionRequestTimeoutSeconds, 5, 120))
        };
        http.DefaultRequestHeaders.Add("Accept", "application/json");

        // Determine replay start timestamps — use a short-lived scope per watch
        var replayStarts = new Dictionary<Watch, DateTimeOffset>();
        foreach (var watch in Watches)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            replayStarts[watch] = await GetReplayAfterAsync(db, cfg, watch, ct);
        }

        foreach (var watch in Watches)
        {
            if (ct.IsCancellationRequested) break;
            var after = replayStarts[watch];
            log.LogInformation("Hyperion catch-up: {Filter} after {After}", watch.Filter, after);
            await PageAccount(http, watch, after, scopes, pub, log, ct);
        }

        log.LogInformation("Hyperion catch-up complete");
    }

    private static async Task<DateTimeOffset> GetReplayAfterAsync(AppDbContext db, AppSettings cfg, Watch watch, CancellationToken ct)
    {
        var latest = watch.Table switch
        {
            ReplayTable.Swap => await db.SwapOrders.AsNoTracking()
                .Where(x => x.Src == watch.Src)
                .Select(x => (long?)x.UpdatedAtTime)
                .MaxAsync(ct) ?? 0,
            ReplayTable.SwapV3 => await db.SwapV3Orders.AsNoTracking()
                .Where(x => x.Src == watch.Src)
                .Select(x => (long?)x.UpdatedAtTime)
                .MaxAsync(ct) ?? 0,
            ReplayTable.MarketMatch => await db.MarketMatches.AsNoTracking()
                .Where(x => x.Src == watch.Src)
                .Select(x => (long?)x.UpdatedAtTime)
                .MaxAsync(ct) ?? 0,
            ReplayTable.LimitFill => await db.LimitOrderFills.AsNoTracking()
                .Where(x => x.Src == watch.Src)
                .Select(x => (long?)x.UpdatedAtTime)
                .MaxAsync(ct) ?? 0,
            ReplayTable.LimitClose => await db.LimitOrderCloses.AsNoTracking()
                .Where(x => x.Src == watch.Src)
                .Select(x => (long?)x.UpdatedAtTime)
                .MaxAsync(ct) ?? 0,
            _ => 0,
        };

        if (latest > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest + 1);

        if (cfg.StartBlock > 1_000_000_000_000)
            return DateTimeOffset.FromUnixTimeMilliseconds(cfg.StartBlock);

        return DateTimeOffset.UtcNow.AddHours(-Math.Clamp(cfg.HyperionInitialLookbackHours, 1, 24 * 365));
    }

    private static async Task PageAccount(
        HttpClient http, Watch watch, DateTimeOffset after,
        IServiceScopeFactory scopes, ISubscriber pub, ILogger log, CancellationToken ct)
    {
        const int limit = 100;
        var cursor = after;

        while (!ct.IsCancellationRequested)
        {
            var afterParam = Uri.EscapeDataString(cursor.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
            var url = $"/v2/history/get_actions?account={watch.Account}" +
                      $"&after={afterParam}&limit={limit}&sort=asc" +
                      $"&filter={Uri.EscapeDataString(watch.Filter)}&simple=false&noBinary=true&checkLib=false";

            JsonDocument? doc = null;
            try
            {
                doc = await http.GetFromJsonAsync<JsonDocument>(url, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Hyperion page failed for {Filter}", watch.Filter);
                break;
            }

            if (doc is null) break;
            // Dispose the JSON document as soon as we're done with the page to return
            // its pooled byte buffer immediately rather than waiting for GC.
            using (doc)
            {
                var actions = doc.RootElement.GetProperty("actions");
                int fetched = 0;
                int dispatched = 0;
                int skippedDuplicates = 0;
                var lastTimestamp = cursor;
                long lastSeqInPage = 0;

                // Fresh DbContext per page — connection is released when scope is disposed,
                // so memory doesn't accumulate across a multi-hour catch-up run.
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                var pageSequences = actions.EnumerateArray()
                    .Select(GetGlobalSequence)
                    .Where(seq => seq > 0)
                    .Distinct()
                    .ToArray();
                var existingSequences = await GetExistingSequencesAsync(db, watch.Table, pageSequences, ct);

                foreach (var action in actions.EnumerateArray())
                {
                    fetched++;
                    var act  = action.GetProperty("act");
                    var trx  = action.TryGetProperty("trx_id", out var t) ? t.GetString() ?? "" : "";
                    int ord  = action.TryGetProperty("action_ordinal", out var ao) ? ao.GetInt32() : 0;
                    long seq = GetGlobalSequence(action);
                    if (seq > lastSeqInPage) lastSeqInPage = seq;
                    long blk = action.TryGetProperty("block_num", out var bn) ? bn.GetInt64() : 0;
                    long ts  = 0;
                    if (action.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String)
                    {
                        if (DateTimeOffset.TryParse(tsEl.GetString(), out var dto))
                        {
                            ts = dto.ToUnixTimeMilliseconds();
                            if (dto > lastTimestamp)
                                lastTimestamp = dto;
                        }
                    }

                    if (seq > 0 && existingSequences.Contains(seq))
                    {
                        skippedDuplicates++;
                        continue;
                    }

                    var data = act.TryGetProperty("data", out var d) ? d : default;
                    // skipPublish=true: historical actions don't need live Redis events
                    ActionDispatch.Dispatch(
                        act.GetProperty("account").GetString() ?? "",
                        act.GetProperty("name").GetString() ?? "",
                        data, trx, ord, seq, blk, ts, db, pub, skipPublish: true);
                    dispatched++;
                }

                if (dispatched > 0)
                {
                    await db.SaveChangesAsync(ct);
                    // scope disposed at end of using block — DbContext and connection freed automatically
                }

                if (fetched > 0)
                    log.LogInformation("Hyperion page: filter={Filter} fetched={Fetched} dispatched={Dispatched} skippedDuplicates={SkippedDuplicates} lastSeq={LastSeq} lastTimestamp={LastTimestamp}", watch.Filter, fetched, dispatched, skippedDuplicates, lastSeqInPage, lastTimestamp);

                if (fetched < limit) break;

                var nextCursor = lastTimestamp.AddSeconds(-1);
                cursor = nextCursor > cursor ? nextCursor : cursor.AddSeconds(1);
            } // doc disposed here — JSON buffer returned to pool

            // Throttle: avoid hammering Hyperion
            await Task.Delay(50, ct);
        }
    }

    private static long GetGlobalSequence(JsonElement action)
    {
        if (!action.TryGetProperty("global_sequence", out var gs)) return 0;
        if (gs.ValueKind != JsonValueKind.Number) return 0;

        try { return gs.GetInt64(); }
        catch { return 0; }
    }

    private static async Task<HashSet<long>> GetExistingSequencesAsync(
        AppDbContext db, ReplayTable table, long[] sequences, CancellationToken ct)
    {
        if (sequences.Length == 0)
            return new HashSet<long>();

        var existing = table switch
        {
            ReplayTable.Swap => await db.SwapOrders.AsNoTracking()
                .Where(x => sequences.Contains(x.GlobalSequence))
                .Select(x => x.GlobalSequence)
                .ToListAsync(ct),
            ReplayTable.SwapV3 => await db.SwapV3Orders.AsNoTracking()
                .Where(x => sequences.Contains(x.GlobalSequence))
                .Select(x => x.GlobalSequence)
                .ToListAsync(ct),
            ReplayTable.MarketMatch => await db.MarketMatches.AsNoTracking()
                .Where(x => sequences.Contains(x.GlobalSequence))
                .Select(x => x.GlobalSequence)
                .ToListAsync(ct),
            ReplayTable.LimitFill => await db.LimitOrderFills.AsNoTracking()
                .Where(x => sequences.Contains(x.GlobalSequence))
                .Select(x => x.GlobalSequence)
                .ToListAsync(ct),
            ReplayTable.LimitClose => await db.LimitOrderCloses.AsNoTracking()
                .Where(x => sequences.Contains(x.GlobalSequence))
                .Select(x => x.GlobalSequence)
                .ToListAsync(ct),
            _ => []
        };

        return existing.ToHashSet();
    }

    private sealed record Watch(string Account, string Filter, ReplayTable Table, string Src);

    private enum ReplayTable
    {
        Swap,
        SwapV3,
        MarketMatch,
        LimitFill,
        LimitClose,
    }
}
