using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using StackExchange.Redis;

namespace NeoOnEdge.Api.Services;

public sealed record TokenCatalogItem(
    string Id,
    string Contract,
    string Symbol,
    int Precision,
    string Source,
    string? LogoUrl = null,
    decimal? UsdPrice = null,
    decimal PopularityScore = 0,
    bool IsTrusted = false);

internal sealed record AlcorTokenDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("contract")] string? Contract,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("decimals")] int? Decimals,
    [property: JsonPropertyName("logo")] string? Logo,
    [property: JsonPropertyName("logo_url")] string? LogoUrl,
    [property: JsonPropertyName("usd_price")] decimal? UsdPrice,
    [property: JsonPropertyName("score")] decimal? Score,
    [property: JsonPropertyName("is_scam")] bool? IsScam,
    [property: JsonPropertyName("is_trusted")] bool? IsTrusted);

public sealed class TokenCatalogService(
    IHttpClientFactory httpFactory,
    IOptions<AppSettings> cfg,
    IConnectionMultiplexer redis,
    ILogger<TokenCatalogService> log)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly IDatabase _cache = redis.GetDatabase();

    public async Task<IReadOnlyList<TokenCatalogItem>> GetTokensAsync(AppDbContext db, CancellationToken ct = default)
    {
        var tokens = new Dictionary<string, TokenCatalogItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in WellKnownTokens())
            tokens[token.Id] = token;

        foreach (var token in await GetAlcorTokensAsync(ct))
            tokens[token.Id] = token;

        foreach (var token in await GetIndexedTokensAsync(db, ct))
            tokens.TryAdd(token.Id, token);

        return tokens.Values
            .Where(t => !string.IsNullOrWhiteSpace(t.Contract) && !string.IsNullOrWhiteSpace(t.Symbol))
            .OrderByDescending(t => t.Symbol == "WAX" && t.Contract == "eosio.token")
            .ThenByDescending(t => t.PopularityScore)
            .ThenByDescending(t => t.IsTrusted)
            .ThenByDescending(t => t.UsdPrice.HasValue)
            .ThenBy(t => t.Symbol)
            .ThenBy(t => t.Contract)
            .ToArray();
    }

    private async Task<IReadOnlyList<TokenCatalogItem>> GetAlcorTokensAsync(CancellationToken ct)
    {
        const string cacheKey = "tokens:alcor:v2:popular";
        var cached = await _cache.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<TokenCatalogItem[]>(cached.ToString()) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                await _cache.KeyDeleteAsync(cacheKey);
            }
        }

        try
        {
            using var http = httpFactory.CreateClient("alcor");
            var upstream = await http.GetFromJsonAsync<AlcorTokenDto[]>($"{cfg.Value.AlcorEndpoint}/api/v2/tokens", cancellationToken: ct) ?? [];
            var tokens = upstream
                .Where(t => !string.IsNullOrWhiteSpace(t.Contract) && !string.IsNullOrWhiteSpace(t.Symbol))
                .Select(t => new TokenCatalogItem(
                    Id: string.IsNullOrWhiteSpace(t.Id) ? BuildId(t.Symbol!, t.Contract!) : t.Id!,
                    Contract: t.Contract!,
                    Symbol: t.Symbol!.ToUpperInvariant(),
                    Precision: t.Decimals ?? 4,
                    Source: "alcor",
                    LogoUrl: string.IsNullOrWhiteSpace(t.LogoUrl) ? t.Logo : t.LogoUrl,
                    UsdPrice: t.UsdPrice,
                    PopularityScore: GetAlcorPopularityScore(t),
                    IsTrusted: t.IsTrusted == true))
                .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            await _cache.StringSetAsync(cacheKey, System.Text.Json.JsonSerializer.Serialize(tokens), CacheTtl);
            return tokens;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "Could not refresh Alcor token catalog");
            return [];
        }
    }

    private static async Task<IReadOnlyList<TokenCatalogItem>> GetIndexedTokensAsync(AppDbContext db, CancellationToken ct)
    {
        long since = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        var tokens = new List<IndexedToken>();

        tokens.AddRange(await db.ListingEvents.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.ContractA != "" && x.CodeA != "")
            .GroupBy(x => new { Contract = x.ContractA, Symbol = x.CodeA })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));
        tokens.AddRange(await db.ListingEvents.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.ContractB != "" && x.CodeB != "")
            .GroupBy(x => new { Contract = x.ContractB, Symbol = x.CodeB })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));

        tokens.AddRange(await db.LimitOrderFills.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.AskContract != "" && x.AskCode != "")
            .GroupBy(x => new { Contract = x.AskContract, Symbol = x.AskCode })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));
        tokens.AddRange(await db.LimitOrderFills.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.BidContract != "" && x.BidCode != "")
            .GroupBy(x => new { Contract = x.BidContract, Symbol = x.BidCode })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));

        tokens.AddRange(await db.LimitOrderCloses.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.AskContract != "" && x.AskCode != "")
            .GroupBy(x => new { Contract = x.AskContract, Symbol = x.AskCode })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));
        tokens.AddRange(await db.LimitOrderCloses.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.BidContract != "" && x.BidCode != "")
            .GroupBy(x => new { Contract = x.BidContract, Symbol = x.BidCode })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, 4, g.Count()))
            .ToListAsync(ct));

        tokens.AddRange(await db.MarketMatches.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.MarketContractBaseToken != "" && x.MarketCodeBaseToken != "")
            .GroupBy(x => new { Contract = x.MarketContractBaseToken, Symbol = x.MarketCodeBaseToken, Precision = x.PrecisionAsk })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, g.Key.Precision, g.Count()))
            .ToListAsync(ct));
        tokens.AddRange(await db.MarketMatches.AsNoTracking()
            .Where(x => x.UpdatedAtTime >= since && x.MarketContractQuoteToken != "" && x.MarketCodeQuoteToken != "")
            .GroupBy(x => new { Contract = x.MarketContractQuoteToken, Symbol = x.MarketCodeQuoteToken, Precision = x.PrecisionBid })
            .Select(g => new IndexedToken(g.Key.Contract, g.Key.Symbol, g.Key.Precision, g.Count()))
            .ToListAsync(ct));

        return tokens
            .Where(x => !string.IsNullOrWhiteSpace(x.Contract) && !string.IsNullOrWhiteSpace(x.Symbol))
            .GroupBy(x => BuildId(x.Symbol, x.Contract), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var token = g.OrderByDescending(x => x.Count).First();
                return new TokenCatalogItem(
                    BuildId(token.Symbol, token.Contract),
                    token.Contract,
                    token.Symbol.ToUpperInvariant(),
                    token.Precision,
                    "indexed",
                    PopularityScore: Math.Min(500, 50 + g.Sum(x => x.Count)));
            })
            .ToArray();
    }

    private static TokenCatalogItem[] WellKnownTokens() =>
    [
        new("wax-eosio.token", "eosio.token", "WAX", 8, "default", PopularityScore: 10_000, IsTrusted: true),
        new("usdt-usdt.alcor", "usdt.alcor", "USDT", 6, "default", PopularityScore: 9_500, IsTrusted: true),
        new("fgl-fgltokenmgmt", "fgltokenmgmt", "FGL", 4, "default", PopularityScore: 8_500, IsTrusted: true),
    ];

    private static string BuildId(string symbol, string contract) => $"{symbol.ToLowerInvariant()}-{contract}";

    private static decimal GetAlcorPopularityScore(AlcorTokenDto token)
    {
        var score = token.Score ?? 0;
        if (token.IsTrusted == true) score += 1_000;
        if (token.IsScam == true) score -= 10_000;
        return score;
    }

    private sealed record IndexedToken(string Contract, string Symbol, int Precision, int Count);
}