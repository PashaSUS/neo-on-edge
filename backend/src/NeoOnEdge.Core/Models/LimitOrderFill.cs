namespace NeoOnEdge.Core.Models;

/// <summary>Alcor limit-order fill event.</summary>
public class LimitOrderFill
{
    public string TrxId { get; set; } = default!;
    public string Src { get; set; } = default!;
    public string Mode { get; set; } = default!;
    public int ActionOrdinal { get; set; }
    public string Owner { get; set; } = default!;
    public string AskContract { get; set; } = default!;
    public string AskCode { get; set; } = default!;
    public string BidContract { get; set; } = default!;
    public string BidCode { get; set; } = default!;
    public long MarketId { get; set; }
    public decimal AmountAsk { get; set; }
    public decimal AmountBid { get; set; }
    public long GlobalSequence { get; set; }
    public long UpdatedAtTime { get; set; }
}
