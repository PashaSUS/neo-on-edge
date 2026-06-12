using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Services;

// ── Alcor router API response models ─────────────────────────────────────────

public sealed record AlcorTokenId(
    [property: JsonPropertyName("id")]     string Id,
    [property: JsonPropertyName("symbol")] string Symbol);

public sealed record AlcorPool(
    [property: JsonPropertyName("fee")]    int          Fee,
    [property: JsonPropertyName("tokenA")] AlcorTokenId TokenA,
    [property: JsonPropertyName("tokenB")] AlcorTokenId TokenB);

public sealed record AlcorSwap(
    [property: JsonPropertyName("input")]       string Input,
    [property: JsonPropertyName("output")]      string Output,
    [property: JsonPropertyName("route")]       int[]  Route,
    [property: JsonPropertyName("percent")]     int    Percent,
    [property: JsonPropertyName("memo")]        string Memo,
    [property: JsonPropertyName("maxSent")]     string MaxSent,
    [property: JsonPropertyName("minReceived")] string MinReceived);

public sealed record AlcorRouteResult(
    [property: JsonPropertyName("route")]        int[]                         Route,
    [property: JsonPropertyName("memo")]         string                        Memo,
    [property: JsonPropertyName("swaps")]        IReadOnlyList<AlcorSwap>      Swaps,
    [property: JsonPropertyName("input")]        string                        Input,
    [property: JsonPropertyName("output")]       string                        Output,
    [property: JsonPropertyName("minReceived")]  string                        MinReceived,
    [property: JsonPropertyName("maxSent")]      string                        MaxSent,
    [property: JsonPropertyName("priceImpact")]  string                        PriceImpact,
    [property: JsonPropertyName("pools")]        Dictionary<string, AlcorPool> Pools);

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Calls the Alcor v2 swap-router API to get an optimised multi-hop route.
/// Results are cached in Redis for <see cref="CacheTtl"/> so that rapid
/// quote refreshes do not hammer the upstream API.
/// </summary>
public sealed class AlcorRouterService(
    IHttpClientFactory httpFactory,
    IOptions<AppSettings> cfg,
    IConnectionMultiplexer redis,
    ILogger<AlcorRouterService> log)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDatabase _cache = redis.GetDatabase();

    /// <summary>
    /// Fetches the Alcor router's best route for a given token pair.
    /// </summary>
    /// <param name="input">Alcor token id, e.g. <c>wax-eosio.token</c>.</param>
    /// <param name="output">Alcor token id, e.g. <c>usdt-usdt.alcor</c>.</param>
    /// <param name="amount">Raw integer amount using <paramref name="inputPrecision" />.</param>
    /// <param name="inputPrecision">Precision of the input token.</param>
    /// <param name="receiver">WAX account that will receive the output tokens.</param>
    /// <param name="slippage">Slippage tolerance in percent (default 1).</param>
    /// <param name="maxHops">Maximum Alcor v2 pool hops (default 3).</param>
    public async Task<AlcorRouteResult?> GetRouteAsync(
        string input,
        string output,
        long   amount,
        int    inputPrecision,
        string receiver,
        double slippage = 1.0,
        int    maxHops  = 3,
        CancellationToken ct = default)
    {
        // ── Cache lookup ──────────────────────────────────────────────────
        string alcorAmount = FormatRawTokenAmount(amount, inputPrecision);
        string cacheKey = $"alcor:route:v2:{input}:{output}:{amount}:{inputPrecision}:{receiver}:{slippage}:{maxHops}";
        var cached = await _cache.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            try
            {
                return JsonSerializer.Deserialize<AlcorRouteResult>(cached.ToString(), JsonOpts);
            }
            catch (JsonException)
            {
                // stale / corrupted cache entry — fall through to fresh fetch
            }
        }

        // ── HTTP call ─────────────────────────────────────────────────────
        string url =
            $"{cfg.Value.AlcorEndpoint}/api/v2/swapRouter/getRoute" +
            $"?trade_type=EXACT_INPUT" +
            $"&input={Uri.EscapeDataString(input)}" +
            $"&output={Uri.EscapeDataString(output)}" +
            $"&amount={Uri.EscapeDataString(alcorAmount)}" +
            $"&slippage={slippage:G}" +
            $"&v2=true" +
            $"&includePoolDetails=true" +
            $"&receiver={Uri.EscapeDataString(receiver)}" +
            $"&maxHops={maxHops}";

        try
        {
            using var http = httpFactory.CreateClient("alcor");
            var result = await http.GetFromJsonAsync<AlcorRouteResult>(url, JsonOpts, ct);

            if (result is not null)
            {
                string json = JsonSerializer.Serialize(result, JsonOpts);
                await _cache.StringSetAsync(cacheKey, json, CacheTtl);
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "Alcor router API request failed for {Input}→{Output}", input, output);
            return null;
        }
    }

    private static string FormatRawTokenAmount(long rawAmount, int precision)
    {
        precision = Math.Clamp(precision, 0, 18);
        decimal scale = 1m;
        for (int i = 0; i < precision; i++)
            scale *= 10m;

        return (rawAmount / scale).ToString(
            $"F{precision}",
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
