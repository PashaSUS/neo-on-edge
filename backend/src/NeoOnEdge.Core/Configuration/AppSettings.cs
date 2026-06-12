namespace NeoOnEdge.Core.Configuration;

public class AppSettings
{
    public string PostgresConnectionString { get; set; } = default!;
    public string RedisConnectionString { get; set; } = default!;
    public string WaxShipEndpoint { get; set; } = "";
    public string WaxShipEndpoints { get; set; } = "wss://wax.eosphere.io/ship,wss://wax.eu.eosamsterdam.net/ship,wss://api.waxsweden.org/ship";
    public string HyperionEndpoint { get; set; } = "https://wax.eosusa.io";
    public bool ShipEnabled { get; set; } = true;
    public bool HyperionFallbackEnabled { get; set; } = true;
    public int HyperionFallbackIntervalSeconds { get; set; } = 60;
    public int HyperionInitialLookbackHours { get; set; } = 168;
    public int HyperionRequestTimeoutSeconds { get; set; } = 20;
    public int ShipReconnectDelaySeconds { get; set; } = 5;
    public long StartBlock { get; set; } = 0;
    public bool ReplayEnabled { get; set; } = true;
    public string ContractAccount { get; set; } = "swap.neo";
    public int NeoRouteFeeBps { get; set; } = 30;
    public string AdminSecret { get; set; } = default!;
    public string AllowedOrigins { get; set; } = "*";
    public string AlcorEndpoint { get; set; } = "https://wax.alcor.exchange";
    public string WaxRpcEndpoint { get; set; } = "https://wax.greymass.com";
    public int DataRetentionDays { get; set; } = 30;
    public int DataRetentionCleanupIntervalHours { get; set; } = 6;
    public bool ArbitrageScannerEnabled { get; set; } = false;
    public string ArbitrageDiscordWebhookUrl { get; set; } = "";
    public int ArbitrageScanIntervalSeconds { get; set; } = 300;
    public decimal ArbitrageMaxInputWax { get; set; } = 100m;
    public string ArbitrageProbeWaxAmounts { get; set; } = "5,10,25,50,100";
    public double ArbitrageSlippage { get; set; } = 2.0;
    public int ArbitrageMaxHops { get; set; } = 3;
    public int ArbitrageMaxTokensPerScan { get; set; } = 150;
    public decimal ArbitrageMinProfitPercent { get; set; } = 0.10m;
    public decimal ArbitrageMinProfitWax { get; set; } = 0.01m;
    public int ArbitrageAlertTopCount { get; set; } = 10;
    public int ArbitrageAlertCooldownMinutes { get; set; } = 30;
    public int ArbitrageNoOpportunityHeartbeatMinutes { get; set; } = 60;
    public int ArbitrageRequestDelayMilliseconds { get; set; } = 100;
    // Seq logging
    public bool SeqEnabled { get; set; } = false;
    public string SeqUrl { get; set; } = "";
    public string SeqApiKey { get; set; } = "";
    // Neo route / safety caps (tunable)
    public int NeoRouteMaxHops { get; set; } = 4;
    public int NeoRouteMaxPoolsPerToken { get; set; } = 24;
    public int NeoRouteMaxSearchStates { get; set; } = 20000;
    public decimal NeoRouteMaxPoolOutputReserveShare { get; set; } = 0.60m;
    public decimal AlcorMaxEdgeVolumeShare { get; set; } = 0.20m;
    public decimal AlcorMaxAverageTradeMultiple { get; set; } = 4m;
}
