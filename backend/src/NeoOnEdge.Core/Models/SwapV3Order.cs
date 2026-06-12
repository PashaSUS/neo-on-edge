namespace NeoOnEdge.Core.Models;

/// <summary>Alcor v3 concentrated-liquidity swap (swapVThreeOrders).</summary>
public class SwapV3Order
{
    public string TrxId { get; set; } = default!;
    public string Src { get; set; } = default!;
    public string Mode { get; set; } = default!;
    public int ActionOrdinal { get; set; }
    public string PairId { get; set; } = default!;
    public string Maker { get; set; } = default!;
    public decimal AmountIn { get; set; }
    public decimal AmountOut { get; set; }
    public string CodeIn { get; set; } = default!;
    public string CodeOut { get; set; } = default!;
    public int PrecisionIn { get; set; }
    public int PrecisionOut { get; set; }
    public long CreatedAtBlock { get; set; }
    public long GlobalSequence { get; set; }
    public long UpdatedAtTime { get; set; }
}
