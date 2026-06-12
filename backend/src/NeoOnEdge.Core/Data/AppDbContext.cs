using Microsoft.EntityFrameworkCore;
using NeoOnEdge.Core.Models;

namespace NeoOnEdge.Core.Data;

/// <summary>
/// Primary EF Core context — maps to PostgreSQL.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SwapOrder>      SwapOrders      { get; set; } = default!;
    public DbSet<MarketMatch>    MarketMatches   { get; set; } = default!;
    public DbSet<SwapV3Order>    SwapV3Orders    { get; set; } = default!;
    public DbSet<LimitOrderFill> LimitOrderFills { get; set; } = default!;
    public DbSet<LimitOrderClose> LimitOrderCloses { get; set; } = default!;
    public DbSet<ListingEvent>   ListingEvents   { get; set; } = default!;
    public DbSet<KlineBar>       KlineBars       { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SwapOrder>(e =>
        {
            e.ToTable("swap_orders");
            e.HasKey(x => new { x.TrxId, x.ActionOrdinal, x.Src });
            e.HasIndex(x => new { x.Src, x.PairId, x.UpdatedAtTime });
            e.HasIndex(x => new { x.Maker, x.UpdatedAtTime });
            e.HasIndex(x => x.GlobalSequence).IsUnique();
        });

        model.Entity<MarketMatch>(e =>
        {
            e.ToTable("market_matches");
            e.HasKey(x => new { x.TrxId, x.ActionOrdinal, x.Src });
            e.HasIndex(x => new { x.Src, x.MarketId, x.UpdatedAtTime });
            e.HasIndex(x => new { x.Asker, x.UpdatedAtTime });
            e.HasIndex(x => new { x.Bidder, x.UpdatedAtTime });
            e.HasIndex(x => x.GlobalSequence).IsUnique();
        });

        model.Entity<SwapV3Order>(e =>
        {
            e.ToTable("swap_v3_orders");
            e.HasKey(x => new { x.TrxId, x.ActionOrdinal, x.Src });
            e.HasIndex(x => new { x.Src, x.PairId, x.UpdatedAtTime });
            e.HasIndex(x => x.GlobalSequence).IsUnique();
        });

        model.Entity<LimitOrderFill>(e =>
        {
            e.ToTable("limit_order_fills");
            e.HasKey(x => new { x.TrxId, x.ActionOrdinal, x.Src });
            e.HasIndex(x => new { x.Src, x.MarketId, x.UpdatedAtTime });
            e.HasIndex(x => new { x.Owner, x.UpdatedAtTime });
            e.HasIndex(x => x.GlobalSequence).IsUnique();
        });

        model.Entity<LimitOrderClose>(e =>
        {
            e.ToTable("limit_order_closes");
            e.HasKey(x => new { x.TrxId, x.ActionOrdinal, x.Src });
            e.HasIndex(x => new { x.Owner, x.UpdatedAtTime });
            e.HasIndex(x => x.GlobalSequence).IsUnique();
        });

        model.Entity<ListingEvent>(e =>
        {
            e.ToTable("listing_events");
            e.HasKey(x => new { x.TrxId, x.Src, x.PairId });
            e.HasIndex(x => x.UpdatedAtTime);
        });

        model.Entity<KlineBar>(e =>
        {
            e.ToTable("klines");
            e.HasKey(x => new { x.Src, x.PairId, x.Duration, x.BucketTs });
            e.HasIndex(x => new { x.Src, x.PairId, x.Duration, x.BucketTs });
        });
    }
}
