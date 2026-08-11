namespace NeoOnEdge.Api.Services;

public sealed class ArbitrageScannerState
{
    private readonly object gate = new();

    public ArbitrageScannerSnapshot Snapshot()
    {
        lock (gate)
            return Current;
    }

    private ArbitrageScannerSnapshot Current { get; set; } = new(
        Enabled: false,
        Running: false,
        DiscordConfigured: false,
        ExecutionEnabled: false,
        ExecutionConfigured: false,
        ScanIntervalSeconds: 0,
        LastStartedAt: null,
        LastCompletedAt: null,
        LastErrorAt: null,
        LastError: null,
        LastSkipReason: "Scanner has not started yet.",
        LastCandidateCount: 0,
        LastRouteLookups: 0,
        LastRawOpportunityCount: 0,
        LastAlertedOpportunityCount: 0,
        LastRejectedUnsafeNeoRoutes: 0,
        LastRejectedSuspiciousRoundTrips: 0,
        LastNeoSuppressedForContractSafety: 0);

    public void MarkConfigured(
        bool enabled,
        bool discordConfigured,
        bool executionEnabled,
        bool executionConfigured,
        int scanIntervalSeconds)
    {
        lock (gate)
        {
            Current = Current with
            {
                Enabled = enabled,
                DiscordConfigured = discordConfigured,
                ExecutionEnabled = executionEnabled,
                ExecutionConfigured = executionConfigured,
                ScanIntervalSeconds = scanIntervalSeconds
            };
        }
    }

    public void MarkSkipped(string reason)
    {
        lock (gate)
        {
            Current = Current with
            {
                Running = false,
                LastSkipReason = reason
            };
        }
    }

    public void MarkScanStarted()
    {
        lock (gate)
        {
            Current = Current with
            {
                Running = true,
                LastStartedAt = DateTimeOffset.UtcNow,
                LastError = null,
                LastErrorAt = null,
                LastSkipReason = null
            };
        }
    }

    public void MarkScanCompleted(
        int candidateCount,
        int routeLookups,
        int rawOpportunityCount,
        int alertedOpportunityCount,
        int rejectedUnsafeNeoRoutes,
        int rejectedSuspiciousRoundTrips,
        int neoSuppressedForContractSafety)
    {
        lock (gate)
        {
            Current = Current with
            {
                Running = false,
                LastCompletedAt = DateTimeOffset.UtcNow,
                LastCandidateCount = candidateCount,
                LastRouteLookups = routeLookups,
                LastRawOpportunityCount = rawOpportunityCount,
                LastAlertedOpportunityCount = alertedOpportunityCount,
                LastRejectedUnsafeNeoRoutes = rejectedUnsafeNeoRoutes,
                LastRejectedSuspiciousRoundTrips = rejectedSuspiciousRoundTrips,
                LastNeoSuppressedForContractSafety = neoSuppressedForContractSafety
            };
        }
    }

    public void MarkScanFailed(Exception ex)
    {
        lock (gate)
        {
            Current = Current with
            {
                Running = false,
                LastErrorAt = DateTimeOffset.UtcNow,
                LastError = ex.Message
            };
        }
    }
}

public sealed record ArbitrageScannerSnapshot(
    bool Enabled,
    bool Running,
    bool DiscordConfigured,
    bool ExecutionEnabled,
    bool ExecutionConfigured,
    int ScanIntervalSeconds,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    string? LastSkipReason,
    int LastCandidateCount,
    int LastRouteLookups,
    int LastRawOpportunityCount,
    int LastAlertedOpportunityCount,
    int LastRejectedUnsafeNeoRoutes,
    int LastRejectedSuspiciousRoundTrips,
    int LastNeoSuppressedForContractSafety);
