using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Data;
using NeoOnEdge.Core.Models;
using StackExchange.Redis;

namespace NeoOnEdge.Indexer;

/// <summary>
/// Converts parsed action-trace JSON into database records and Redis pub/sub events.
/// </summary>
public static class ActionDispatch
{
    // Tracks recently-published token keys with a timestamp to allow TTL-based suppression
    private static readonly ConcurrentDictionary<string, long> PublishedTokenKeys = new(StringComparer.OrdinalIgnoreCase);

    public static void Dispatch(
        string account, string name, JsonElement data,
        string trx, int ordinal, long globalSeq, long block, long tsMs,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
    {
        try { Interlocked.Exchange(ref _lastProcessedGlobalSequence, globalSeq); } catch { }
        switch (account)
        {
            case "swap.alcor" when name == "logswap":
                HandleAlcorV3Swap(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
            case "alcordexmain" when name is "buymatch" or "sellmatch" or "marketmatch":
                HandleAlcorMarketMatch(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
            case "alcordexmain" when name == "orderfilled":
                HandleLimitFill(data, "ad", trx, ordinal, globalSeq, tsMs, db, pub, skipPublish);
                break;
            case "alcordexmain" when name == "orderclosed":
                HandleLimitClose(data, "ad", trx, ordinal, globalSeq, tsMs, db, pub, skipPublish);
                break;
            case "swap.box" when name == "swaplog":
                HandleDefiboxSwap(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
            case "swap.taco" when name == "exchangelog":
                HandleTacoSwap(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
            case "swap.nefty" when name == "logswap":
                HandleNeftySwap(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
            case "swap.adex" when name == "swaplog":
                HandleAdexSwap(data, trx, ordinal, globalSeq, block, tsMs, db, pub, skipPublish);
                break;
        }
    }

    // ── Alcor v3 ──────────────────────────────────────────────────────────────

    private static void HandleAlcorV3Swap(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
    {
        if (d.ValueKind != JsonValueKind.Object) return;

        var amountIn = GetStr(d, "amountIn");
        var amountOut = GetStr(d, "amountOut");
        if (string.IsNullOrWhiteSpace(amountIn) || string.IsNullOrWhiteSpace(amountOut))
            (amountIn, amountOut) = GetSignedSwapAssets(d, "tokenA", "tokenB");

        var row = new SwapV3Order
        {
            TrxId         = trx,
            Src           = "a2",
            Mode          = "swap",
            ActionOrdinal = ord,
            PairId        = GetStr(d, "poolId"),
            Maker         = FirstNonEmpty(GetStr(d, "sender"), GetStr(d, "recipient")),
            AmountIn      = ParseAssetAmount(amountIn),
            AmountOut     = ParseAssetAmount(amountOut),
            CodeIn        = ParseAssetCode(amountIn),
            CodeOut       = ParseAssetCode(amountOut),
            PrecisionIn   = ParseAssetPrecision(amountIn),
            PrecisionOut  = ParseAssetPrecision(amountOut),
            GlobalSequence = seq,
            CreatedAtBlock = block,
            UpdatedAtTime  = ts,
        };

        if (IsTrackedDuplicate(
                db.SwapV3Orders.Local,
                row.GlobalSequence,
                x => x.GlobalSequence,
                x => x.TrxId == row.TrxId &&
                     x.ActionOrdinal == row.ActionOrdinal &&
                     x.Src == row.Src))
            return;

        db.SwapV3Orders.Add(row);

        if (!skipPublish)
        {
            pub.Publish(
                RedisChannel.Literal($"trades:a2:{row.PairId}"),
                System.Text.Json.JsonSerializer.Serialize(row));
            PublishTokenUpdate(pub, row.CodeIn, "", row.PrecisionIn);
            PublishTokenUpdate(pub, row.CodeOut, "", row.PrecisionOut);
        }
    }

    // ── Alcor DEX order-book ──────────────────────────────────────────────────

    private static void HandleAlcorMarketMatch(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var record = GetObject(d, "record") ?? d;
        var market = GetObject(record, "market");
        var baseToken = market is { } ? GetObject(market.Value, "base_token") : null;
        var quoteToken = market is { } ? GetObject(market.Value, "quote_token") : null;
        var ask = GetStr(record, "ask");
        var bid = GetStr(record, "bid");

        var row = new MarketMatch
        {
            TrxId          = trx,
            Src            = "ad",
            Mode           = "match",
            ActionOrdinal  = ord,
            OrderId        = FirstNonZero(GetLong(record, "orderId"), GetLong(record, "id")),
            Asker          = GetStr(record, "asker"),
            Bidder         = GetStr(record, "bidder"),
            UnitPrice      = GetLong(record, "unit_price"),
            AmountAsk      = ParseAssetAmount(ask),
            AmountBid      = ParseAssetAmount(bid),
            CodeAsk        = ParseAssetCode(ask),
            CodeBid        = ParseAssetCode(bid),
            PrecisionAsk   = ParseAssetPrecision(ask),
            PrecisionBid   = ParseAssetPrecision(bid),
            MarketId       = FirstNonZero(GetLong(record, "market_id"), market is { } ? GetLong(market.Value, "id") : 0),
            MarketFrozen   = market is { } && GetBool(market.Value, "frozen"),
            MarketContractBaseToken = baseToken is { } ? GetStr(baseToken.Value, "contract") : "",
            MarketContractQuoteToken = quoteToken is { } ? GetStr(quoteToken.Value, "contract") : "",
            MarketCodeBaseToken = baseToken is { } ? ParseSymbolCode(GetStr(baseToken.Value, "sym")) : "",
            MarketCodeQuoteToken = quoteToken is { } ? ParseSymbolCode(GetStr(quoteToken.Value, "sym")) : "",
            GlobalSequence = seq,
            CreatedAtBlock = block,
            UpdatedAtTime  = ts,
        };

        if (IsTrackedDuplicate(
                db.MarketMatches.Local,
                row.GlobalSequence,
                x => x.GlobalSequence,
                x => x.TrxId == row.TrxId &&
                     x.ActionOrdinal == row.ActionOrdinal &&
                     x.Src == row.Src))
            return;

        db.MarketMatches.Add(row);

        if (!skipPublish)
        {
            pub.Publish(
                RedisChannel.Literal($"trades:ad:{row.MarketId}"),
                System.Text.Json.JsonSerializer.Serialize(row));
            PublishTokenUpdate(pub, row.MarketCodeBaseToken, row.MarketContractBaseToken, row.PrecisionAsk);
            PublishTokenUpdate(pub, row.MarketCodeQuoteToken, row.MarketContractQuoteToken, row.PrecisionBid);
        }
    }

    private static void HandleLimitFill(
        JsonElement d, string src, string trx, int ord, long seq, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var row = new LimitOrderFill
        {
            TrxId          = trx, Src = src, Mode = "fill",
            ActionOrdinal  = ord,
            Owner          = GetStr(d, "owner"),
            AskContract    = GetStr(d, "ask_contract"),
            AskCode        = GetStr(d, "ask_code"),
            BidContract    = GetStr(d, "bid_contract"),
            BidCode        = GetStr(d, "bid_code"),
            MarketId       = GetLong(d, "market_id"),
            AmountAsk      = ParseAssetAmount(GetStr(d, "ask")),
            AmountBid      = ParseAssetAmount(GetStr(d, "bid")),
            GlobalSequence = seq,
            UpdatedAtTime  = ts,
        };
        if (IsTrackedDuplicate(
                db.LimitOrderFills.Local,
                row.GlobalSequence,
                x => x.GlobalSequence,
                x => x.TrxId == row.TrxId &&
                     x.ActionOrdinal == row.ActionOrdinal &&
                     x.Src == row.Src))
            return;

        db.LimitOrderFills.Add(row);

        if (!skipPublish)
        {
            PublishTokenUpdate(pub, row.AskCode, row.AskContract, 4);
            PublishTokenUpdate(pub, row.BidCode, row.BidContract, 4);
        }
    }

    private static void HandleLimitClose(
        JsonElement d, string src, string trx, int ord, long seq, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var row = new LimitOrderClose
        {
            TrxId          = trx, Src = src, Mode = "close",
            ActionOrdinal  = ord,
            Owner          = GetStr(d, "owner"),
            AskContract    = GetStr(d, "ask_contract"),
            AskCode        = GetStr(d, "ask_code"),
            BidContract    = GetStr(d, "bid_contract"),
            BidCode        = GetStr(d, "bid_code"),
            MarketId       = GetLong(d, "market_id"),
            GlobalSequence = seq,
            UpdatedAtTime  = ts,
        };
        if (IsTrackedDuplicate(
                db.LimitOrderCloses.Local,
                row.GlobalSequence,
                x => x.GlobalSequence,
                x => x.TrxId == row.TrxId &&
                     x.ActionOrdinal == row.ActionOrdinal &&
                     x.Src == row.Src))
            return;

        db.LimitOrderCloses.Add(row);

        if (!skipPublish)
        {
            PublishTokenUpdate(pub, row.AskCode, row.AskContract, 4);
            PublishTokenUpdate(pub, row.BidCode, row.BidContract, 4);
        }
    }

    // ── AMM helpers (Defibox, Taco, Nefty, Adex) ─────────────────────────────

    private static void HandleDefiboxSwap(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
        => HandleGenericAmm(d, "d", trx, ord, seq, block, ts, db, pub, skipPublish,
            pairIdField: "pair_id", makerField: "owner",
            amountInField: "quantity_in", amountOutField: "quantity_out",
            reserveAField: "reserve0", reserveBField: "reserve1");

    private static void HandleTacoSwap(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
        => HandleGenericAmm(d, "t", trx, ord, seq, block, ts, db, pub, skipPublish,
            pairIdField: "id", makerField: "maker",
            amountInField: "quantity_in", amountOutField: "quantity_out",
            reserveAField: "pool1", reserveBField: "pool2");

    private static void HandleNeftySwap(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
        => HandleGenericAmm(d, "n", trx, ord, seq, block, ts, db, pub, skipPublish,
            pairIdField: "code", makerField: "owner",
            amountInField: "quantity_in", amountOutField: "quantity_out",
            reserveAField: "pool0", reserveBField: "pool1");

    private static void HandleAdexSwap(
        JsonElement d, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish = false)
        => HandleGenericAmm(d, "a", trx, ord, seq, block, ts, db, pub, skipPublish,
            pairIdField: "pool_id", makerField: "owner",
            amountInField: "quantity_in", amountOutField: "quantity_out",
            reserveAField: "reserve_a", reserveBField: "reserve_b");

    private static void HandleGenericAmm(
        JsonElement d, string src, string trx, int ord, long seq, long block, long ts,
        AppDbContext db, ISubscriber pub, bool skipPublish,
        string pairIdField, string makerField,
        string amountInField, string amountOutField,
        string reserveAField, string reserveBField)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var row = new SwapOrder
        {
            TrxId          = trx, Src = src, Mode = "swap",
            ActionOrdinal  = ord,
            PairId         = GetStr(d, pairIdField),
            Maker          = GetStr(d, makerField),
            AmountIn       = ParseAssetAmount(GetStr(d, amountInField)),
            AmountOut      = ParseAssetAmount(GetStr(d, amountOutField)),
            CodeIn         = ParseAssetCode(GetStr(d, amountInField)),
            CodeOut        = ParseAssetCode(GetStr(d, amountOutField)),
            PrecisionIn    = ParseAssetPrecision(GetStr(d, amountInField)),
            PrecisionOut   = ParseAssetPrecision(GetStr(d, amountOutField)),
            AmountReserveA = ParseAssetAmount(GetStr(d, reserveAField)),
            AmountReserveB = ParseAssetAmount(GetStr(d, reserveBField)),
            CodeReserveA   = ParseAssetCode(GetStr(d, reserveAField)),
            CodeReserveB   = ParseAssetCode(GetStr(d, reserveBField)),
            PrecisionReserveA = ParseAssetPrecision(GetStr(d, reserveAField)),
            PrecisionReserveB = ParseAssetPrecision(GetStr(d, reserveBField)),
            GlobalSequence = seq, CreatedAtBlock = block, UpdatedAtTime = ts,
        };

        if (IsTrackedDuplicate(
                db.SwapOrders.Local,
                row.GlobalSequence,
                x => x.GlobalSequence,
                x => x.TrxId == row.TrxId &&
                     x.ActionOrdinal == row.ActionOrdinal &&
                     x.Src == row.Src))
            return;

        db.SwapOrders.Add(row);

        if (!skipPublish)
        {
            pub.Publish(
                RedisChannel.Literal($"trades:{src}:{row.PairId}"),
                System.Text.Json.JsonSerializer.Serialize(row));
            PublishTokenUpdate(pub, row.CodeIn, "", row.PrecisionIn);
            PublishTokenUpdate(pub, row.CodeOut, "", row.PrecisionOut);
        }
    }

    private static bool IsTrackedDuplicate<TEntity>(
        IEnumerable<TEntity> tracked,
        long globalSequence,
        Func<TEntity, long> sequenceSelector,
        Func<TEntity, bool> hasSamePrimaryKey)
    {
        return tracked.Any(entity =>
            (globalSequence > 0 && sequenceSelector(entity) == globalSequence) ||
            hasSamePrimaryKey(entity));
    }

    private static void PublishTokenUpdate(ISubscriber pub, string symbol, string contract, int precision)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (string.IsNullOrWhiteSpace(contract)) return;

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var normalizedContract = contract.Trim();
        var tokenKey = $"{normalizedContract}:{normalizedSymbol}:{precision}";

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const long ttlMs = 24 * 60 * 60 * 1000; // 24 hours

        // If we've published this token recently, skip re-publishing until TTL expires
        if (PublishedTokenKeys.TryGetValue(tokenKey, out var last) && (now - last) < ttlMs)
            return;

        // Update last-seen timestamp
        PublishedTokenKeys[tokenKey] = now;

        // Occasional pruning to remove very old entries and free memory
        if (PublishedTokenKeys.Count > 10000)
        {
            foreach (var kv in PublishedTokenKeys)
            {
                if (now - kv.Value > ttlMs)
                    PublishedTokenKeys.TryRemove(kv.Key, out _);
            }
        }

        pub.Publish(
            RedisChannel.Literal("tokens_update"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                symbol = normalizedSymbol,
                contract = normalizedContract,
                precision,
                seenAt = now
            }));
    }

    // ── Asset string helpers ──────────────────────────────────────────────────
    // EOSIO asset string format: "1.23456789 WAX"

    private static string GetStr(JsonElement el, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        if (!el.TryGetProperty(key, out var v)) return "";

        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                return v.GetString() ?? "";
            case JsonValueKind.Number:
                return v.GetRawText();
            case JsonValueKind.Object:
                // common shapes: { "quantity": "1.23 WAX" }, { "amount": "1.23 WAX" }, { "asset": { "quantity": "1.23 WAX" } }
                if (v.TryGetProperty("quantity", out var q))
                {
                    if (q.ValueKind == JsonValueKind.String) return q.GetString() ?? "";
                    if (q.ValueKind == JsonValueKind.Number) return q.GetRawText();
                }
                if (v.TryGetProperty("amount", out var a))
                {
                    if (a.ValueKind == JsonValueKind.String) return a.GetString() ?? "";
                    if (a.ValueKind == JsonValueKind.Number) return a.GetRawText();
                }
                if (v.TryGetProperty("asset", out var a2))
                {
                    if (a2.ValueKind == JsonValueKind.String) return a2.GetString() ?? "";
                    if (a2.ValueKind == JsonValueKind.Number) return a2.GetRawText();
                    if (a2.ValueKind == JsonValueKind.Object && a2.TryGetProperty("quantity", out var q2) && q2.ValueKind == JsonValueKind.String)
                        return q2.GetString() ?? "";
                }
                return "";
            default:
                return "";
        }
    }

    private static JsonElement? GetObject(JsonElement el, string key)
        => el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Object ? prop : null;

    private static long GetLong(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt64();
        if (prop.ValueKind == JsonValueKind.String)
        {
            long tmpLong;
            if (long.TryParse(prop.GetString(), out tmpLong)) return tmpLong;
        }
        return 0;
    }

    private static bool GetBool(JsonElement el, string key)
        => el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static long FirstNonZero(params long[] values)
        => values.FirstOrDefault(x => x != 0);

    private static decimal ParseAssetAmount(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return 0;
        var sp = asset.IndexOf(' ');
        decimal parsedAmount;
        if (sp < 0)
        {
            // asset may be a plain numeric string like "123.45" or an integer in raw units
            return decimal.TryParse(asset.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsedAmount) ? parsedAmount : 0;
        }
        return decimal.TryParse(asset.AsSpan(0, sp), NumberStyles.Number, CultureInfo.InvariantCulture, out parsedAmount) ? parsedAmount : 0;
    }

    private static string ParseAssetCode(string asset)
    {
        var sp = asset.IndexOf(' ');
        return sp >= 0 ? asset[(sp + 1)..] : "";
    }

    private static int ParseAssetPrecision(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return 0;
        var sp = asset.IndexOf(' ');
        // If there's no symbol part (plain numeric), derive precision from decimal places in the string
        if (sp < 0)
        {
            var dotOnly = asset.IndexOf('.');
            if (dotOnly < 0) return 0;
            return asset.Length - dotOnly - 1;
        }
        var dot = asset.IndexOf('.');
        if (dot < 0 || dot > sp) return 0;
        return sp - dot - 1;
    }

    private static string ParseSymbolCode(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "";
        var comma = symbol.IndexOf(',');
        return comma >= 0 ? symbol[(comma + 1)..] : symbol;
    }

    private static (string AmountIn, string AmountOut) GetSignedSwapAssets(JsonElement el, string tokenAKey, string tokenBKey)
    {
        var tokenA = GetStr(el, tokenAKey);
        var tokenB = GetStr(el, tokenBKey);
        var amountA = ParseAssetAmount(tokenA);
        var amountB = ParseAssetAmount(tokenB);

        if (amountA > 0 && amountB < 0)
            return (NormalizePositiveAsset(tokenA), NormalizePositiveAsset(tokenB));

        if (amountB > 0 && amountA < 0)
            return (NormalizePositiveAsset(tokenB), NormalizePositiveAsset(tokenA));

        return (NormalizePositiveAsset(tokenA), NormalizePositiveAsset(tokenB));
    }

    private static string NormalizePositiveAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return "";
        var trimmed = asset.Trim();
        return trimmed.StartsWith("-", StringComparison.Ordinal) ? trimmed[1..] : trimmed;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    // Diagnostic helpers
    public static int GetPublishedTokenCount() => PublishedTokenKeys.Count;
    private static long _lastProcessedGlobalSequence = 0;

    public static long GetLastProcessedSequence() => Interlocked.Read(ref _lastProcessedGlobalSequence);

}
