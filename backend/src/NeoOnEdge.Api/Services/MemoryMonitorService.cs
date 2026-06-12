using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeoOnEdge.Core.Services;
using NeoOnEdge.Indexer;

namespace NeoOnEdge.Api.Services;

/// <summary>
/// Lightweight diagnostic background service that periodically logs
/// GC and process memory metrics to aid production troubleshooting.
/// Safe to enable in production; does not force collections.
/// Also logs token registry and indexer-published token counts.
/// </summary>
public sealed class MemoryMonitorService : BackgroundService
{
    private readonly ILogger<MemoryMonitorService> _log;
    private readonly TokenRegistry _tokens;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    public MemoryMonitorService(ILogger<MemoryMonitorService> log, TokenRegistry tokens)
    {
        _log = log;
        _tokens = tokens;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var proc = Process.GetCurrentProcess();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long totalMemory = GC.GetTotalMemory(false);
                var gcInfo = GC.GetGCMemoryInfo();
                var workingSet = proc.WorkingSet64;
                var privateBytes = proc.PrivateMemorySize64;
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);

                int registryCount = _tokens.All().Count;
                int publishedCount = 0;
                long lastSeq = 0;
                try { publishedCount = ActionDispatch.GetPublishedTokenCount(); } catch { }
                try { lastSeq = ActionDispatch.GetLastProcessedSequence(); } catch { }

                _log.LogInformation(
                    "MemoryMonitor: totalMemory={TotalMemory} heapSize={HeapSize} fragmented={Fragmented} memoryLoad={MemoryLoad} highMemoryThreshold={HighMemoryThreshold} gen0={Gen0} gen1={Gen1} gen2={Gen2} workingSet={WorkingSet} privateBytes={PrivateBytes} tokenRegistry={RegistryCount} publishedTokens={PublishedCount} lastSeq={LastSequence}",
                    totalMemory,
                    gcInfo.HeapSizeBytes,
                    gcInfo.FragmentedBytes,
                    gcInfo.MemoryLoadBytes,
                    gcInfo.HighMemoryLoadThresholdBytes,
                    gen0,
                    gen1,
                    gen2,
                    workingSet,
                    privateBytes,
                    registryCount,
                    publishedCount,
                    lastSeq);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MemoryMonitor error");
            }

            try { await Task.Delay(_interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
