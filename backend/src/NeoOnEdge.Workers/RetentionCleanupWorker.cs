using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;

namespace NeoOnEdge.Workers;

public sealed class RetentionCleanupWorker(
    ILogger<RetentionCleanupWorker> log,
    IServiceScopeFactory scopes,
    IOptions<AppSettings> settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RunCleanupSafely(ct);

        var interval = TimeSpan.FromHours(Math.Clamp(settings.Value.DataRetentionCleanupIntervalHours, 1, 168));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct))
            await RunCleanupSafely(ct);
    }

    private async Task RunCleanupSafely(CancellationToken ct)
    {
        try
        {
            await Cleanup(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Retention cleanup failed");
        }
    }

    private async Task Cleanup(CancellationToken ct)
    {
        var retentionDays = Math.Clamp(settings.Value.DataRetentionDays, 1, 365);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var deletedSwapOrders = await db.SwapOrders
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedSwapV3Orders = await db.SwapV3Orders
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedMarketMatches = await db.MarketMatches
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedLimitOrderFills = await db.LimitOrderFills
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedLimitOrderCloses = await db.LimitOrderCloses
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedListingEvents = await db.ListingEvents
            .Where(x => x.UpdatedAtTime < cutoff)
            .ExecuteDeleteAsync(ct);
        var deletedKlines = await db.KlineBars
            .Where(x => x.BucketTs < cutoff)
            .ExecuteDeleteAsync(ct);

        var total = deletedSwapOrders + deletedSwapV3Orders + deletedMarketMatches +
                    deletedLimitOrderFills + deletedLimitOrderCloses + deletedListingEvents +
                    deletedKlines;

        if (total > 0)
        {
            log.LogInformation(
                "Retention cleanup removed {Total} rows older than {RetentionDays} days: swap={SwapOrders}, v3={SwapV3Orders}, markets={MarketMatches}, fills={LimitOrderFills}, closes={LimitOrderCloses}, listings={ListingEvents}, klines={Klines}",
                total,
                retentionDays,
                deletedSwapOrders,
                deletedSwapV3Orders,
                deletedMarketMatches,
                deletedLimitOrderFills,
                deletedLimitOrderCloses,
                deletedListingEvents,
                deletedKlines);
        }
    }
}