using Microsoft.AspNetCore.Mvc;
using NeoOnEdge.Api.Services;
using NeoOnEdge.Core.Data;
using System.Globalization;
using System.Text.Json;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TokensController(
    TokenCatalogService tokens,
    AppDbContext db,
    IHttpClientFactory httpFactory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTokens(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 500,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var all = await tokens.GetTokensAsync(db, ct);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            all = all.Where(t =>
                    t.Symbol.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    t.Contract.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    t.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return Ok(all.Take(limit));
    }

    [HttpGet("owned/{account}")]
    public async Task<IActionResult> GetOwnedTokens(
        string account,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        account = account.Trim();
        if (string.IsNullOrWhiteSpace(account))
            return BadRequest("account is required");

        limit = Math.Clamp(limit, 1, 500);
        var catalog = await tokens.GetTokensAsync(db, ct);
        var catalogById = catalog.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        using var http = httpFactory.CreateClient("wax-light-api");
        using var response = await http.GetAsync($"/api/balances/wax/{account}", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var owned = new Dictionary<string, OwnedTokenResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var balance in EnumerateBalances(doc.RootElement))
        {
            var contract = balance.TryGetProperty("contract", out var c)
                ? c.GetString()?.Trim()
                : null;
            var symbol = balance.TryGetProperty("currency", out var cur)
                ? cur.GetString()?.Trim().ToUpperInvariant()
                : null;
            var amountText = balance.TryGetProperty("amount", out var amountNode)
                ? amountNode.GetString()?.Trim()
                : null;
            var decimals = balance.TryGetProperty("decimals", out var d)
                ? TryParseInt(d, 4)
                : 4;

            if (string.IsNullOrWhiteSpace(contract) ||
                string.IsNullOrWhiteSpace(symbol) ||
                !TryParseDecimal(amountText, out var amount) ||
                amount <= 0)
                continue;

            var id = BuildTokenId(symbol, contract);
            if (!catalogById.TryGetValue(id, out var token))
                continue;

            var usdValue = token.UsdPrice is { } usdPrice ? usdPrice * amount : (decimal?)null;
            owned[id] = new OwnedTokenResult(
                token.Id,
                token.Contract,
                token.Symbol,
                token.Precision,
                token.Source,
                token.LogoUrl,
                token.UsdPrice,
                token.PopularityScore,
                token.IsTrusted,
                amount,
                $"{amount.ToString($"F{Math.Clamp(decimals, 0, 18)}", CultureInfo.InvariantCulture)} {token.Symbol}",
                usdValue);
        }

        return Ok(owned.Values
            .OrderByDescending(t => t.UsdValue ?? 0)
            .ThenByDescending(t => t.BalanceAmount)
            .ThenByDescending(t => t.PopularityScore)
            .ThenBy(t => t.Symbol)
            .Take(limit));
    }

    private static string BuildTokenId(string symbol, string contract)
        => $"{symbol.ToLowerInvariant()}-{contract}";

    private static bool TryParseDecimal(string? value, out decimal amount)
        => decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount);

    private static IEnumerable<JsonElement> EnumerateBalances(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        if (root.TryGetProperty("balances", out var directBalances) &&
            directBalances.ValueKind == JsonValueKind.Array)
        {
            foreach (var balance in directBalances.EnumerateArray())
                yield return balance;

            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("balances", out var balances) ||
                balances.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var balance in balances.EnumerateArray())
                yield return balance;
        }
    }

    private static int TryParseInt(JsonElement node, int fallback)
    {
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number))
            return number;

        if (node.ValueKind == JsonValueKind.String &&
            int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;

        return fallback;
    }

    private sealed record OwnedTokenResult(
        string Id,
        string Contract,
        string Symbol,
        int Precision,
        string Source,
        string? LogoUrl,
        decimal? UsdPrice,
        decimal PopularityScore,
        bool IsTrusted,
        decimal BalanceAmount,
        string BalanceAsset,
        decimal? UsdValue);
}
