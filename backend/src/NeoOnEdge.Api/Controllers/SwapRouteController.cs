using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NeoOnEdge.Api.Services;
using NeoOnEdge.Core.Configuration;

namespace NeoOnEdge.Api.Controllers;

/// <summary>
/// Quote / route comparison endpoint.
///
/// GET /api/route
///   ?input=wax-eosio.token        — Alcor token id of the token being sold
///   &output=usdt-usdt.alcor       — Alcor token id of the token being bought
///   &amount=3000000000            — Raw integer amount using inputPrecision
///   &receiver=myaccount.wam       — WAX account receiving output tokens (optional for preview)
///   &inputPrecision=8             — Input token precision
///   &outputPrecision=6            — Output token precision
///   &slippage=1                   — Slippage tolerance in percent (default 1)
///   &maxHops=3                    — Maximum NeoOnEdge pool hops (default 3)
///
/// Response shape:
/// {
///   "alcor":  null,  // Alcor is quoted directly by the frontend to avoid backend rate limits
///   "neo":    { "output": "...", "memo": "...", "route": [...] },  // from our indexed pools
///   "split":  { "output": "...", "legs": [...] }, // multi-leg NeoOnEdge route (may be null)
///   "neoReason": "...",   // when neo is null
///   "splitReason": "...", // when split is null
///   "best":   "neo" | "split" | "none"   // frontend folds direct Alcor quote into final best choice
/// }
/// </summary>
[ApiController]
[Route("api/route")]
public sealed class SwapRouteController(
    NeoRouteQuoteService neoRoutes,
    IOptions<AppSettings> settings) : ControllerBase
{
    // Max route hops are configurable via AppSettings.NeoRouteMaxHops

    [HttpGet]
    public async Task<IActionResult> GetRoute(
        [FromQuery] string input,
        [FromQuery] string output,
        [FromQuery] long   amount,
        [FromQuery] string? receiver,
        [FromQuery] int    inputPrecision = 8,
        [FromQuery] int    outputPrecision = 8,
        [FromQuery] double slippage = 1.0,
        [FromQuery] int    maxHops  = 3,
        [FromQuery] bool   fresh = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)  ||
            string.IsNullOrWhiteSpace(output) ||
            amount <= 0)
            return BadRequest("input, output and amount are required.");

        var quoteReceiver = string.IsNullOrWhiteSpace(receiver)
            ? settings.Value.ContractAccount
            : receiver.Trim();
        var boundedMaxHops = Math.Clamp(maxHops, 1, Math.Max(1, settings.Value.NeoRouteMaxHops));

        var neoQuote = await neoRoutes.GetRouteQuoteAsync(input, output, amount, quoteReceiver, inputPrecision, outputPrecision, slippage, boundedMaxHops, settings.Value.ContractAccount, ct, readAlcorCache: !fresh);
        var splitQuote = await neoRoutes.GetSplitRouteQuoteAsync(input, output, amount, quoteReceiver, inputPrecision, outputPrecision, slippage, boundedMaxHops, settings.Value.ContractAccount, ct, readAlcorCache: !fresh);
        var neoRoute = neoQuote.Route;
        var splitRoute = splitQuote.Route;

        // ── Compare output amounts and pick a winner ───────────────────────
        decimal neoOutput = neoRoute is not null ? ParseRouteAmount(neoRoute.Output) : 0;
        decimal splitOutput = splitRoute is not null ? ParseRouteAmount(splitRoute.Output) : 0;

        string best = (neoOutput, splitOutput) switch
        {
            (0, 0) => "none",
            var (no, so) when so > no * 1.0005m => "split",
            var (no, _) when no > 0 => "neo",
            _ => "none"
        };

        return Ok(new
        {
            alcor = (object?)null,
            neo   = neoRoute,
            split = splitRoute,
            neoReason = neoRoute is null ? neoQuote.Reason : null,
            splitReason = splitRoute is null ? splitQuote.Reason : null,
            best
        });
    }

    private static decimal ParseRouteAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        var amount = value.Trim();
        var space = amount.IndexOf(' ');
        if (space > 0) amount = amount[..space];

        return decimal.TryParse(
            amount,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0;
    }

}
