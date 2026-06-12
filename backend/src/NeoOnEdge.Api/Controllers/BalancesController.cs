using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Net.Http.Json;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class BalancesController(IHttpClientFactory httpFactory) : ControllerBase
{
    [HttpGet("{account}")]
    public async Task<IActionResult> GetBalances(
        string account,
        [FromQuery] string tokens,
        CancellationToken ct = default)
    {
        account = account.Trim();
        if (string.IsNullOrWhiteSpace(account))
            return BadRequest("account is required");

        var requested = tokens
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTokenId)
            .Where(x => x is not null)
            .Select(x => x!)
            .DistinctBy(x => x.Id)
            .Take(25)
            .ToList();

        if (requested.Count == 0)
            return BadRequest("tokens must include at least one Alcor-style token id");

        using var http = httpFactory.CreateClient("wax-rpc");
        var balances = new List<TokenBalanceResult>();

        foreach (var token in requested)
        {
            try
            {
                using var response = await http.PostAsJsonAsync(
                    "/v1/chain/get_currency_balance",
                    new { code = token.Contract, account, symbol = token.Symbol },
                    ct);

                response.EnsureSuccessStatusCode();
                var assets = await response.Content.ReadFromJsonAsync<string[]>(cancellationToken: ct) ?? [];
                var asset = assets.FirstOrDefault() ?? $"0 {token.Symbol}";
                balances.Add(new TokenBalanceResult(
                    token.Id,
                    token.Contract,
                    token.Symbol,
                    ParseAssetAmount(asset),
                    asset,
                    null));
            }
            catch (Exception ex)
            {
                balances.Add(new TokenBalanceResult(
                    token.Id,
                    token.Contract,
                    token.Symbol,
                    0,
                    $"0 {token.Symbol}",
                    ex.Message));
            }
        }

        return Ok(balances);
    }

    private static TokenRequest? ParseTokenId(string id)
    {
        var dash = id.IndexOf('-');
        if (dash <= 0 || dash == id.Length - 1) return null;

        return new TokenRequest(
            id,
            id[..dash].ToUpperInvariant(),
            id[(dash + 1)..]);
    }

    private static decimal ParseAssetAmount(string asset)
    {
        var space = asset.IndexOf(' ');
        if (space <= 0) return 0;

        return decimal.TryParse(
            asset.AsSpan(0, space),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }

    private sealed record TokenRequest(string Id, string Symbol, string Contract);

    private sealed record TokenBalanceResult(
        string Id,
        string Contract,
        string Symbol,
        decimal Amount,
        string Asset,
        string? Error);
}