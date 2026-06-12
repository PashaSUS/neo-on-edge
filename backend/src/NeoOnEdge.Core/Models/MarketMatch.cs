namespace NeoOnEdge.Core.Models;

/// <summary>Alcor order-book trade (marketMatches).</summary>
public class MarketMatch
{
    public string TrxId { get; set; } = default!;
    public string Src { get; set; } = default!;
    public string Mode { get; set; } = default!;
    public int ActionOrdinal { get; set; }
    public long OrderId { get; set; }
    public string Asker { get; set; } = default!;
    public string Bidder { get; set; } = default!;
    public long UnitPrice { get; set; }
    public decimal AmountAsk { get; set; }
    public decimal AmountBid { get; set; }
    public string CodeAsk { get; set; } = default!;
    public string CodeBid { get; set; } = default!;
    public int PrecisionAsk { get; set; }
    public int PrecisionBid { get; set; }
    public long MarketId { get; set; }
    public bool MarketFrozen { get; set; }
    public string MarketContractBaseToken { get; set; } = default!;
    public string MarketContractQuoteToken { get; set; } = default!;
    public string MarketCodeBaseToken { get; set; } = default!;
    public string MarketCodeQuoteToken { get; set; } = default!;
    public long CreatedAtBlock { get; set; }
    public long GlobalSequence { get; set; }
    public long UpdatedAtTime { get; set; }
}
