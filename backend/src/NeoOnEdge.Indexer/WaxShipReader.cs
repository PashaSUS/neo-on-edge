using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using NeoOnEdge.Core.Models;
using StackExchange.Redis;

namespace NeoOnEdge.Indexer;

/// <summary>
/// Connects to a WAX SHIP (State History Plugin) WebSocket endpoint and
/// processes ABI-decoded actions from the relevant DEX contracts.
///
/// On first run it fast-forwards via Hyperion HTTP until caught up to the
/// live head, then switches to the real-time SHIP stream.
/// </summary>
public sealed class WaxShipReader : BackgroundService
{
    private static readonly string[] WatchedContracts =
    [
        "swap.alcor", "alcordexmain", "swap.box", "swap.taco", "swap.nefty",
        "swap.adex",  "dapp.fusion",  "token.fusion"
    ];

    private readonly ILogger<WaxShipReader> _log;
    private readonly AppSettings _cfg;
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _pub;
    private DateTimeOffset _lastHyperionFallbackAt = DateTimeOffset.MinValue;

    public WaxShipReader(
        ILogger<WaxShipReader> log,
        IOptions<AppSettings> cfg,
        IServiceScopeFactory scopes,
        IConnectionMultiplexer redis)
    {
        _log    = log;
        _cfg    = cfg.Value;
        _scopes = scopes;
        _redis  = redis;
        _pub    = redis.GetSubscriber();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 1. Catch up historical data via Hyperion HTTP
        await RunHyperionCatchup(ct);

        var endpoints = GetShipEndpoints();
        if (!_cfg.ShipEnabled || endpoints.Count == 0)
        {
            _log.LogWarning("SHIP streaming is disabled or has no endpoints. Using Hyperion fallback polling only.");
            await RunHyperionFallbackLoop(ct);
            return;
        }

        // 2. Follow the live chain via SHIP
        var nextEndpoint = 0;
        while (!ct.IsCancellationRequested)
        {
            var endpoint = endpoints[nextEndpoint++ % endpoints.Count];
            try
            {
                await ConnectAndStream(endpoint, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ShipBinaryAbiNotSupportedException ex)
            {
                _log.LogWarning(ex, "SHIP endpoint returned ABI-encoded binary messages, but no SHIP ABI decoder is configured. Switching to Hyperion fallback polling only.");
                await RunHyperionFallbackLoop(ct);
                return;
            }
            catch (Exception ex)
            {
                var delaySeconds = Math.Max(1, _cfg.ShipReconnectDelaySeconds);
                _log.LogError(ex, "SHIP connection to {Endpoint} lost; trying next endpoint in {DelaySeconds} s", endpoint, delaySeconds);

                await RunHyperionFallbackIfDue(ct);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
        }
    }

    private async Task ConnectAndStream(string endpoint, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("binary");
        _log.LogInformation("Connecting to SHIP at {Endpoint}", endpoint);
        await ws.ConnectAsync(new Uri(endpoint), ct);
        _log.LogInformation("SHIP connected");

        // Receive ABI before requesting blocks
        var (_, abiIsBinary) = await ReceiveMessage(ws, ct);
        if (abiIsBinary)
            throw new ShipBinaryAbiNotSupportedException(endpoint);

        long startBlock = await GetShipStartBlockAsync(ct);
        _log.LogInformation("Requesting SHIP blocks from block {StartBlock}", startBlock);
        var request = new
        {
            start_block_num = startBlock,
            end_block_num   = 0xFFFF_FFFF,
            max_messages_in_flight = 50,
            have_positions  = Array.Empty<object>(),
            irreversible_only = false,
            fetch_block     = false,
            fetch_traces    = true,
            fetch_deltas    = false,
        };
        await SendJson(ws, request, ct);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var (msg, isBinary) = await ReceiveMessage(ws, ct);
            if (msg.Length == 0 && !isBinary) continue;

            // Always ack so SHIP's in-flight window doesn't stall
            await SendJson(ws, new { type = "get_blocks_ack", num_messages = 1 }, ct);

            // Binary ABI-encoded SHIP messages: no decoder yet — skip without
            // opening a DbContext or parsing JSON. The message was already drained
            // in ReceiveMessage so the WebSocket frame is fully consumed.
            if (isBinary) continue;

            using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            long? lastSeq = ProcessBlock(msg, db);
            await db.SaveChangesAsync(ct);
            // scope is disposed here — DbContext and connection freed automatically

            if (lastSeq.HasValue)
                _log.LogDebug("SHIP block processed lastSeq={LastSeq}", lastSeq.Value);
        }
    }

    private async Task RunHyperionCatchup(CancellationToken ct)
    {
        // HyperionCatchup manages its own per-page DbContext scopes internally
        await HyperionCatchup.RunAsync(_cfg, _scopes, _pub, _log, ct);
        _lastHyperionFallbackAt = DateTimeOffset.UtcNow;
    }

    private async Task<long> GetShipStartBlockAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var latestSwapBlock = await db.SwapOrders.AsNoTracking()
            .Select(x => (long?)x.CreatedAtBlock)
            .MaxAsync(ct) ?? 0;
        var latestV3Block = await db.SwapV3Orders.AsNoTracking()
            .Select(x => (long?)x.CreatedAtBlock)
            .MaxAsync(ct) ?? 0;
        var latestMarketBlock = await db.MarketMatches.AsNoTracking()
            .Select(x => (long?)x.CreatedAtBlock)
            .MaxAsync(ct) ?? 0;

        var latestIndexedBlock = Math.Max(latestSwapBlock, Math.Max(latestV3Block, latestMarketBlock));
        var configuredStartBlock = Math.Max(0, _cfg.StartBlock);

        if (latestIndexedBlock <= 0)
            return configuredStartBlock;

        return Math.Max(configuredStartBlock, latestIndexedBlock + 1);
    }

    private async Task RunHyperionFallbackIfDue(CancellationToken ct)
    {
        if (!_cfg.HyperionFallbackEnabled) return;

        var interval = TimeSpan.FromSeconds(Math.Max(5, _cfg.HyperionFallbackIntervalSeconds));
        if (DateTimeOffset.UtcNow - _lastHyperionFallbackAt < interval) return;

        await RunHyperionCatchup(ct);
    }

    private async Task RunHyperionFallbackLoop(CancellationToken ct)
    {
        var intervalSeconds = Math.Max(5, _cfg.HyperionFallbackIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            await RunHyperionCatchup(ct);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    private IReadOnlyList<string> GetShipEndpoints()
    {
        var endpoints = new List<string>();
        AddEndpoint(_cfg.WaxShipEndpoint);

        foreach (var endpoint in (_cfg.WaxShipEndpoints ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddEndpoint(endpoint);

        return endpoints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        void AddEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return;
            endpoints.Add(endpoint.Trim());
        }
    }

    // ─── Parsing ───────────────────────────────────────────────────────────────

    private long? ProcessBlock(ReadOnlyMemory<byte> msg, AppDbContext db)
    {
        // SHIP messages are ABI-packed binary. In production, use the SHIP ABI
        // to decode. Here we delegate to the ActionTrace parser.
        try
        {
            using var doc = JsonDocument.Parse(msg);
            if (!doc.RootElement.TryGetProperty("traces", out var traces)) return null;
            long lastSeq = 0;
            foreach (var trace in traces.EnumerateArray())
            {
                if (trace.TryGetProperty("global_sequence", out var gs) && gs.ValueKind == JsonValueKind.Number)
                {
                    try { var seq = gs.GetInt64(); if (seq > lastSeq) lastSeq = seq; } catch { }
                }
                ProcessTrace(trace, db);
            }
            return lastSeq == 0 ? null : lastSeq;
        }
        catch (JsonException)
        {
            // Binary SHIP messages require ABI decode — log and skip for now
            _log.LogTrace("Non-JSON SHIP message skipped (ABI binary)");
            return null;
        }
    }

    private void ProcessTrace(JsonElement trace, AppDbContext db)
    {
        if (!trace.TryGetProperty("act", out var act)) return;
        var account = act.GetProperty("account").GetString() ?? "";
        var name    = act.GetProperty("name").GetString() ?? "";
        var data    = act.TryGetProperty("data", out var d) ? d : default;
        var seqElem = trace.TryGetProperty("global_sequence", out var gs) ? gs : default;
        long seq    = seqElem.ValueKind == JsonValueKind.Number ? seqElem.GetInt64() : 0;
        long block  = trace.TryGetProperty("block_num", out var bn)
            ? bn.GetInt64() : 0;
        long ts     = trace.TryGetProperty("block_time_ms", out var bt)
            ? bt.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string trx  = trace.TryGetProperty("trx_id", out var ti)
            ? ti.GetString() ?? "" : "";
        int ord     = trace.TryGetProperty("action_ordinal", out var ao) ? ao.GetInt32() : 0;

        ActionDispatch.Dispatch(account, name, data, trx, ord, seq, block, ts, db, _pub);
    }

    // ─── WebSocket helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads one complete WebSocket message.
    /// Returns (data, isBinary=true) for ABI-encoded binary SHIP messages — the
    /// frame is fully drained but NOT accumulated, so no LOH allocation occurs.
    /// Returns (data, isBinary=false) for JSON messages with the full payload.
    /// Returns (empty, false) if the frame has zero bytes (connection closing).
    /// </summary>
    private static async Task<(ReadOnlyMemory<byte> Data, bool IsBinary)> ReceiveMessage(
        ClientWebSocket ws, CancellationToken ct)
    {
        // Use ArrayPool for the receive chunk — avoids a new byte[] allocation per call
        var tmp = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            var result = await ws.ReceiveAsync(tmp, ct);
            if (result.Count == 0 && result.EndOfMessage)
                return (ReadOnlyMemory<byte>.Empty, false);

            bool isBinary = result.Count == 0 || tmp[0] != (byte)'{';

            if (isBinary)
            {
                // Drain remaining chunks without accumulating — avoids LOH pressure
                // for large binary ABI SHIP blocks (can be 500 KB – several MB each).
                while (!result.EndOfMessage)
                    result = await ws.ReceiveAsync(tmp, ct);
                return (ReadOnlyMemory<byte>.Empty, true);
            }

            // JSON message — accumulate all chunks into a contiguous buffer
            var buf = new List<byte>(Math.Max(65536, result.Count));
            buf.AddRange(tmp.AsSpan(0, result.Count));
            while (!result.EndOfMessage)
            {
                result = await ws.ReceiveAsync(tmp, ct);
                buf.AddRange(tmp.AsSpan(0, result.Count));
            }
            return (buf.ToArray(), false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    private static async Task SendJson(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private sealed class ShipBinaryAbiNotSupportedException(string endpoint)
        : NotSupportedException($"SHIP endpoint {endpoint} returned binary ABI payloads.");
}
