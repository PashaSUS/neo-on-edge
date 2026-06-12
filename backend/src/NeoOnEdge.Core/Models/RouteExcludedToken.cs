namespace NeoOnEdge.Core.Models;

public class RouteExcludedToken
{
    public string Contract { get; set; } = default!;
    public string Symbol { get; set; } = default!;
    public string Reason { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}
