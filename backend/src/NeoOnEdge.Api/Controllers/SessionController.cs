using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using StackExchange.Redis;
using System.Security.Cryptography;

namespace NeoOnEdge.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class SessionController : ControllerBase
{
    private readonly IDatabase _cache;

    public SessionController(IConnectionMultiplexer redis) => _cache = redis.GetDatabase();

    /// POST /api/socket-session-token — issues a short-lived Redis-backed token
    [HttpPost("socket-session-token")]
    public async Task<IActionResult> CreateToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _cache.StringSetAsync($"session_token:{token}", "1", TimeSpan.FromMinutes(5));
        return Ok(new { token });
    }
}
