using Microsoft.AspNetCore.Mvc;
using NeoOnEdge.Api.Services;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api/arbitrage")]
public sealed class ArbitrageController(ArbitrageScannerState state) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
        => Ok(state.Snapshot());
}
