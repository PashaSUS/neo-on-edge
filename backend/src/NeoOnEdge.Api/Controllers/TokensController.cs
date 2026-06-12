using Microsoft.AspNetCore.Mvc;
using NeoOnEdge.Api.Services;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TokensController(TokenCatalogService tokens, AppDbContext db) : ControllerBase
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
}