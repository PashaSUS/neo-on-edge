using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class KlinesController(
    AppDbContext db,
    IHttpClientFactory httpFactory,
    IOptions<AppSettings> settings,
    ILogger<KlinesController> log) : ControllerBase
{
    private static readonly HashSet<string> ValidDurations =
        ["1m", "5m", "15m", "1h", "4h", "1d", "1w"];
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// GET /api/klines?src=d&pairId=1&duration=1h&limit=500&before=<ts>
    [HttpGet]
    public async Task<IActionResult> GetKlines(
        [FromQuery] string src, [FromQuery] string pairId,
        [FromQuery] string duration = "1h",
        [FromQuery] int limit = 500, [FromQuery] long? before = null)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(pairId))
            return BadRequest("src and pairId are required");

        if (!ValidDurations.Contains(duration))
            return BadRequest($"duration must be one of: {string.Join(", ", ValidDurations)}");

        limit = Math.Clamp(limit, 1, 2000);

        var query = db.KlineBars.AsNoTracking()
            .Where(x => x.Src == src && x.PairId == pairId && x.Duration == duration);

        if (before.HasValue)
            query = query.Where(x => x.BucketTs < before.Value);

        var rows = await query
            .OrderByDescending(x => x.BucketTs)
            .Take(limit)
            .ToListAsync();

        return Ok(rows);
    }

    /// GET /api/klines/alcor?tokenA=wax-eosio.token&tokenB=fgl-fgltokenmgmt&resolution=240&from=<ms>&to=<ms>
    [HttpGet("alcor")]
    public async Task<IActionResult> GetAlcorKlines(
        [FromQuery] string tokenA,
        [FromQuery] string tokenB,
        [FromQuery] int resolution = 240,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        [FromQuery] int limit = 500,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenA) || string.IsNullOrWhiteSpace(tokenB))
            return BadRequest("tokenA and tokenB are required");

        resolution = Math.Clamp(resolution, 1, 10_080);
        limit = Math.Clamp(limit, 1, 2_000);

        long toMs = to ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fromMs = from ?? toMs - (long)resolution * 60_000L * limit;

        using var http = httpFactory.CreateClient("alcor");
        var url = $"{settings.Value.AlcorEndpoint}/api/v2/swap/candles" +
                  $"?tokenA={Uri.EscapeDataString(tokenA)}" +
                  $"&tokenB={Uri.EscapeDataString(tokenB)}" +
                  $"&resolution={resolution}" +
                  $"&from={fromMs}&to={toMs}";

        AlcorCandlesResponse? payload;
        try
        {
            payload = await http.GetFromJsonAsync<AlcorCandlesResponse>(url, JsonOpts, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug(ex, "Alcor candle proxy failed for {TokenA}->{TokenB} resolution={Resolution}", tokenA, tokenB, resolution);
            return Ok(Array.Empty<object>());
        }

        var candles = payload?.Candles ?? [];

        var rows = candles
            .OrderByDescending(x => x.Time)
            .Take(limit)
            .Select(x => new
            {
                bucketTs = x.Time,
                open = ParseDecimal(x.Open),
                high = ParseDecimal(x.High),
                low = ParseDecimal(x.Low),
                close = ParseDecimal(x.Close),
                volumeA = x.Volume,
                volumeB = 0m,
                tradeCount = x.Volume > 0 ? 1 : 0
            })
            .ToList();

        return Ok(rows);
    }

    private static decimal ParseDecimal(string value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private sealed record AlcorCandlesResponse(
        [property: JsonPropertyName("candles")] IReadOnlyList<AlcorCandle> Candles);

    private sealed record AlcorCandle(
        [property: JsonPropertyName("open")] string Open,
        [property: JsonPropertyName("high")] string High,
        [property: JsonPropertyName("low")] string Low,
        [property: JsonPropertyName("close")] string Close,
        [property: JsonPropertyName("time")] long Time,
        [property: JsonPropertyName("volume")] decimal Volume);
}
