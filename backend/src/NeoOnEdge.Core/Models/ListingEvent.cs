namespace NeoOnEdge.Core.Models;

/// <summary>New pool / token listing event.</summary>
public class ListingEvent
{
    public string TrxId { get; set; } = default!;
    public string Src { get; set; } = default!;
    public string PairId { get; set; } = default!;
    public string CodeA { get; set; } = default!;
    public string CodeB { get; set; } = default!;
    public string ContractA { get; set; } = default!;
    public string ContractB { get; set; } = default!;
    public long GlobalSequence { get; set; }
    public long UpdatedAtTime { get; set; }
}
