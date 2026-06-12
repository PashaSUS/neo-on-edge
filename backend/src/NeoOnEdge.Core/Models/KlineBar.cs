namespace NeoOnEdge.Core.Models;

/// <summary>OHLCV candlestick bar.</summary>
public class KlineBar
{
    public string Src { get; set; } = default!;
    public string PairId { get; set; } = default!;
    public string Duration { get; set; } = default!;
    public long BucketTs { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public decimal VolumeA { get; set; }
    public decimal VolumeB { get; set; }
    public int TradeCount { get; set; }
    public int UniqueAccounts { get; set; }
}
