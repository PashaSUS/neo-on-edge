param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [string]$RequestedContractAccount = "",

    [Parameter(Mandatory = $true)]
    [string]$OutCmd
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path $Root).Path
$envPath = Join-Path $Root ".env"

function New-Secret([int]$Bytes) {
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($buffer)
    }
    finally {
        $rng.Dispose()
    }

    return ([Convert]::ToBase64String($buffer).TrimEnd("=") -replace "\+", "-" -replace "/", "_")
}

function Read-EnvValues([string]$Path) {
    $values = @{}
    if (!(Test-Path $Path)) {
        return $values
    }

    foreach ($line in Get-Content $Path) {
        if ($line -match "^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$") {
            $values[$Matches[1]] = $Matches[2]
        }
    }

    return $values
}

function Get-ConfiguredValue($Values, [string]$Key, [string]$Default = "") {
    if ($Values.ContainsKey($Key) -and ![string]::IsNullOrWhiteSpace($Values[$Key])) {
        return $Values[$Key]
    }

    return $Default
}

function Read-Text([string]$Prompt, [string]$Default = "") {
    if ([string]::IsNullOrWhiteSpace($Default)) {
        $answer = Read-Host $Prompt
    }
    else {
        $answer = Read-Host "$Prompt [$Default]"
    }

    if ([string]::IsNullOrWhiteSpace($answer)) {
        return $Default
    }

    return $answer.Trim()
}

function Read-Port([string]$Prompt, [string]$Default) {
    while ($true) {
        $answer = Read-Text $Prompt $Default
        $port = 0
        if ([int]::TryParse($answer, [ref]$port) -and $port -ge 1 -and $port -le 65535) {
            return $answer
        }

        Write-Host "Please enter a valid TCP port from 1 to 65535."
    }
}

function Read-YesNo([string]$Prompt, [bool]$Default) {
    if ($Default) {
        $suffix = "[Y/n]"
    }
    else {
        $suffix = "[y/N]"
    }

    while ($true) {
        $answer = Read-Host "$Prompt $suffix"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $Default
        }

        switch ($answer.Trim().ToLowerInvariant()) {
            "y" { return $true }
            "yes" { return $true }
            "n" { return $false }
            "no" { return $false }
            default { Write-Host "Please answer y or n." }
        }
    }
}

function Read-HiddenText([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    if ($secure.Length -eq 0) {
        return ""
    }

    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

function Needs-GeneratedSecret([string]$Value) {
    return [string]::IsNullOrWhiteSpace($Value) -or
        $Value -match "change_me" -or
        $Value -match "changeme" -or
        $Value -match "replace_with_generated"
}

function To-Bool([string]$Value, [bool]$Default) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Default
    }

    switch ($Value.Trim().ToLowerInvariant()) {
        "true" { return $true }
        "1" { return $true }
        "yes" { return $true }
        "y" { return $true }
        "false" { return $false }
        "0" { return $false }
        "no" { return $false }
        "n" { return $false }
        default { return $Default }
    }
}

function Bool-Text([bool]$Value) {
    if ($Value) {
        return "true"
    }

    return "false"
}

function Normalize-PublicEndpoint([string]$Value, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Path
    }

    $trimmed = $Value.Trim().TrimEnd("/")
    if ($trimmed.StartsWith("http://") -or $trimmed.StartsWith("https://") -or $trimmed.StartsWith("/")) {
        return $trimmed
    }

    $host = ($trimmed -split "[/?#]", 2)[0]
    $pathPart = $trimmed.Substring($host.Length)
    if ([string]::IsNullOrWhiteSpace($pathPart)) {
        $pathPart = $Path
    }

    if ($host -match "^(localhost|127\.0\.0\.1)(:[0-9]+)?$") {
        return "http://$host$pathPart"
    }

    if ($host.Contains(".") -or $host.Contains(":")) {
        return "https://$host$pathPart"
    }

    if ($trimmed.StartsWith($Path.TrimStart("/"))) {
        return "/$trimmed"
    }

    return $trimmed
}

function Cmd-Value([string]$Value) {
    return ($Value -replace '"', '')
}

$values = Read-EnvValues $envPath

Write-Host ""
Write-Host "== NeoOnEdge deployment setup =="
Write-Host "This updates local .env values. Secrets are not written to appsettings.json."
Write-Host ""

$contractDefault = $RequestedContractAccount
if ([string]::IsNullOrWhiteSpace($contractDefault)) {
    $contractDefault = Get-ConfiguredValue $values "CONTRACT_ACCOUNT" $env:CONTRACT_ACCOUNT
}
if ([string]::IsNullOrWhiteSpace($contractDefault)) {
    $contractDefault = "swap.neo"
}
$contractAccount = Read-Text "Contract account" $contractDefault

$appPortDefault = Get-ConfiguredValue $values "APP_HOST_PORT" ""
if ([string]::IsNullOrWhiteSpace($appPortDefault)) {
    $appPortDefault = "15173"
}
$appHostPort = Read-Port "Host port for the website" $appPortDefault

$apiPortDefault = Get-ConfiguredValue $values "API_HOST_PORT" ""
if ([string]::IsNullOrWhiteSpace($apiPortDefault)) {
    $apiPortDefault = "15174"
}
$apiHostPort = Read-Port "Host port for the backend API/SignalR" $apiPortDefault

$allowedDefault = Get-ConfiguredValue $values "ALLOWED_ORIGINS" "http://localhost:$appHostPort,http://localhost:3177"
$allowedDefault = $allowedDefault -replace "localhost:18080", "localhost:$appHostPort"
$allowedOrigins = Read-Text "Allowed origins" $allowedDefault

$publicApiDefault = Normalize-PublicEndpoint (Get-ConfiguredValue $values "PUBLIC_API_BASE_URL" "/api") "/api"
$publicHubDefault = Normalize-PublicEndpoint (Get-ConfiguredValue $values "PUBLIC_HUB_URL" "/hub") "/hub"
$publicApiBaseUrl = Normalize-PublicEndpoint (Read-Text "Public API base URL for the website (/api or full backend URL)" $publicApiDefault) "/api"
$publicHubUrl = Normalize-PublicEndpoint (Read-Text "Public SignalR hub URL for the website (/hub or full backend URL)" $publicHubDefault) "/hub"

$deployDefault = To-Bool $env:DEPLOY_CONTRACT $false
$deployContract = Read-YesNo "Deploy the smart contract with cleos now" $deployDefault
$cleosWallet = Get-ConfiguredValue $values "CLEOS_WALLET" $env:CLEOS_WALLET
if ([string]::IsNullOrWhiteSpace($cleosWallet)) {
    $cleosWallet = "default"
}
$importContractPrivateKey = $false
$setCodePermission = $false
if ($deployContract) {
    $cleosWallet = Read-Text "cleos wallet name" $cleosWallet
    $importContractPrivateKey = Read-YesNo "Import the contract private key into this cleos wallet before deploy" $false
    $setCodePermission = Read-YesNo "Ensure the contract account has eosio.code permission for inline swaps" $true
}

$scannerDefault = To-Bool (Get-ConfiguredValue $values "ARBITRAGE_SCANNER_ENABLED" "false") $false
$enableScanner = Read-YesNo "Enable arbitrage scanner" $scannerDefault
$discordWebhook = Get-ConfiguredValue $values "ARBITRAGE_DISCORD_WEBHOOK_URL" ""
if ($enableScanner) {
    $enableDiscord = Read-YesNo "Send scanner alerts to Discord" (![string]::IsNullOrWhiteSpace($discordWebhook))
    if ($enableDiscord) {
        if (![string]::IsNullOrWhiteSpace($discordWebhook)) {
            $keepWebhook = Read-YesNo "Keep the existing Discord webhook URL from .env" $true
            if (!$keepWebhook) {
                $discordWebhook = ""
            }
        }

        while ([string]::IsNullOrWhiteSpace($discordWebhook)) {
            $discordWebhook = Read-HiddenText "Discord webhook URL (hidden)"
            if ([string]::IsNullOrWhiteSpace($discordWebhook)) {
                Write-Host "Discord webhook URL is required when Discord alerts are enabled."
            }
        }
    }
    else {
        $discordWebhook = ""
    }
}
else {
    $discordWebhook = ""
}

$executionDefault = $enableScanner -and (To-Bool (Get-ConfiguredValue $values "ARBITRAGE_EXECUTION_ENABLED" "false") $false)
$enableExecution = $false
$arbitrageAccount = Get-ConfiguredValue $values "ARBITRAGE_ACCOUNT" ""
$arbitragePermission = Get-ConfiguredValue $values "ARBITRAGE_PERMISSION" "active"
$arbitragePrivateKey = Get-ConfiguredValue $values "ARBITRAGE_PRIVATE_KEY" ""
$executorSecret = Get-ConfiguredValue $values "ARBITRAGE_EXECUTOR_SECRET" ""
if ($enableScanner) {
    $enableExecution = Read-YesNo "Allow the scanner to execute trades automatically" $executionDefault
}
if ($enableExecution) {
    while ([string]::IsNullOrWhiteSpace($arbitrageAccount)) {
        $arbitrageAccount = Read-Text "Arbitrage WAX account"
    }
    $arbitragePermission = Read-Text "Arbitrage account permission" $arbitragePermission

    if (![string]::IsNullOrWhiteSpace($arbitragePrivateKey)) {
        $keepArbitrageKey = Read-YesNo "Keep the existing arbitrage private key from .env" $true
        if (!$keepArbitrageKey) {
            $arbitragePrivateKey = ""
        }
    }
    while ([string]::IsNullOrWhiteSpace($arbitragePrivateKey)) {
        $arbitragePrivateKey = Read-HiddenText "Arbitrage private key (hidden)"
        if ([string]::IsNullOrWhiteSpace($arbitragePrivateKey)) {
            Write-Host "A private key is required for automatic execution."
        }
    }

    if (Needs-GeneratedSecret $executorSecret) {
        $executorSecret = New-Secret 48
        Write-Host "Generated arbitrage executor secret."
    }
}

$postgresPassword = Get-ConfiguredValue $values "POSTGRES_PASSWORD" ""
if (Needs-GeneratedSecret $postgresPassword) {
    $postgresPassword = New-Secret 36
    Write-Host "Generated PostgreSQL password."
}
else {
    $regenPostgres = Read-YesNo "Regenerate PostgreSQL password in .env" $false
    if ($regenPostgres) {
        $postgresPassword = New-Secret 36
        Write-Host "Generated PostgreSQL password. Existing database volumes may need ALTER USER if already initialized."
    }
}

$adminSecret = Get-ConfiguredValue $values "ADMIN_SECRET" ""
if (Needs-GeneratedSecret $adminSecret) {
    $adminSecret = New-Secret 48
    Write-Host "Generated admin secret."
}
else {
    $regenAdmin = Read-YesNo "Regenerate admin secret in .env" $false
    if ($regenAdmin) {
        $adminSecret = New-Secret 48
        Write-Host "Generated admin secret."
    }
}

$postgresDb = Read-Text "PostgreSQL database" (Get-ConfiguredValue $values "POSTGRES_DB" "neoedge")
$postgresUser = Read-Text "PostgreSQL user" (Get-ConfiguredValue $values "POSTGRES_USER" "neoedge")

$envText = @"
# -- PostgreSQL ---------------------------------------------------------------
POSTGRES_DB=$postgresDb
POSTGRES_USER=$postgresUser
POSTGRES_PASSWORD=$postgresPassword

# -- Host ports ---------------------------------------------------------------
APP_HOST_PORT=$appHostPort
API_HOST_PORT=$apiHostPort
PUBLIC_API_BASE_URL=$publicApiBaseUrl
PUBLIC_HUB_URL=$publicHubUrl

# -- WAX Blockchain -----------------------------------------------------------
WAX_SHIP_ENDPOINT=$(Get-ConfiguredValue $values "WAX_SHIP_ENDPOINT" "")
WAX_SHIP_ENDPOINTS=$(Get-ConfiguredValue $values "WAX_SHIP_ENDPOINTS" "wss://wax.eosphere.io/ship,wss://wax.eu.eosamsterdam.net/ship,wss://api.waxsweden.org/ship")
HYPERION_ENDPOINT=$(Get-ConfiguredValue $values "HYPERION_ENDPOINT" "https://wax.eosusa.io")
SHIP_ENABLED=$(Get-ConfiguredValue $values "SHIP_ENABLED" "true")
HYPERION_FALLBACK_ENABLED=$(Get-ConfiguredValue $values "HYPERION_FALLBACK_ENABLED" "true")
HYPERION_FALLBACK_INTERVAL_SECONDS=$(Get-ConfiguredValue $values "HYPERION_FALLBACK_INTERVAL_SECONDS" "60")
HYPERION_INITIAL_LOOKBACK_HOURS=$(Get-ConfiguredValue $values "HYPERION_INITIAL_LOOKBACK_HOURS" "168")
HYPERION_REQUEST_TIMEOUT_SECONDS=$(Get-ConfiguredValue $values "HYPERION_REQUEST_TIMEOUT_SECONDS" "20")
SHIP_RECONNECT_DELAY_SECONDS=$(Get-ConfiguredValue $values "SHIP_RECONNECT_DELAY_SECONDS" "5")
START_BLOCK=$(Get-ConfiguredValue $values "START_BLOCK" "0")
REPLAY_ENABLED=$(Get-ConfiguredValue $values "REPLAY_ENABLED" "true")
CONTRACT_ACCOUNT=$contractAccount

# -- API ----------------------------------------------------------------------
ADMIN_SECRET=$adminSecret
ALLOWED_ORIGINS=$allowedOrigins
ALCOR_ENDPOINT=$(Get-ConfiguredValue $values "ALCOR_ENDPOINT" "https://wax.alcor.exchange")
WAX_RPC_ENDPOINT=$(Get-ConfiguredValue $values "WAX_RPC_ENDPOINT" "https://wax.greymass.com")
DATA_RETENTION_DAYS=$(Get-ConfiguredValue $values "DATA_RETENTION_DAYS" "30")
DATA_RETENTION_CLEANUP_INTERVAL_HOURS=$(Get-ConfiguredValue $values "DATA_RETENTION_CLEANUP_INTERVAL_HOURS" "6")

# -- Dry-run arbitrage monitor ------------------------------------------------
ARBITRAGE_SCANNER_ENABLED=$(Bool-Text $enableScanner)
ARBITRAGE_DISCORD_WEBHOOK_URL=$discordWebhook
ARBITRAGE_SCAN_INTERVAL_SECONDS=$(Get-ConfiguredValue $values "ARBITRAGE_SCAN_INTERVAL_SECONDS" "300")
ARBITRAGE_MAX_INPUT_WAX=$(Get-ConfiguredValue $values "ARBITRAGE_MAX_INPUT_WAX" "100")
ARBITRAGE_PROBE_WAX_AMOUNTS=$(Get-ConfiguredValue $values "ARBITRAGE_PROBE_WAX_AMOUNTS" "5,10,25,50,100")
ARBITRAGE_SLIPPAGE=$(Get-ConfiguredValue $values "ARBITRAGE_SLIPPAGE" "2")
ARBITRAGE_MAX_HOPS=$(Get-ConfiguredValue $values "ARBITRAGE_MAX_HOPS" "6")
ARBITRAGE_MAX_TOKENS_PER_SCAN=$(Get-ConfiguredValue $values "ARBITRAGE_MAX_TOKENS_PER_SCAN" "150")
ARBITRAGE_MIN_PROFIT_PERCENT=$(Get-ConfiguredValue $values "ARBITRAGE_MIN_PROFIT_PERCENT" "0.1")
ARBITRAGE_MIN_PROFIT_WAX=$(Get-ConfiguredValue $values "ARBITRAGE_MIN_PROFIT_WAX" "0.01")
ARBITRAGE_ALERT_TOP_COUNT=$(Get-ConfiguredValue $values "ARBITRAGE_ALERT_TOP_COUNT" "10")
ARBITRAGE_ALERT_COOLDOWN_MINUTES=$(Get-ConfiguredValue $values "ARBITRAGE_ALERT_COOLDOWN_MINUTES" "30")
ARBITRAGE_NO_OPPORTUNITY_HEARTBEAT_MINUTES=$(Get-ConfiguredValue $values "ARBITRAGE_NO_OPPORTUNITY_HEARTBEAT_MINUTES" "60")
ARBITRAGE_REQUEST_DELAY_MILLISECONDS=$(Get-ConfiguredValue $values "ARBITRAGE_REQUEST_DELAY_MILLISECONDS" "100")
ARBITRAGE_EXECUTION_ENABLED=$(Bool-Text $enableExecution)
ARBITRAGE_ACCOUNT=$arbitrageAccount
ARBITRAGE_PERMISSION=$arbitragePermission
ARBITRAGE_PRIVATE_KEY=$arbitragePrivateKey
ARBITRAGE_EXECUTOR_SECRET=$executorSecret
ARBITRAGE_MAX_EXECUTION_WAX=$(Get-ConfiguredValue $values "ARBITRAGE_MAX_EXECUTION_WAX" "5")
ARBITRAGE_EXECUTION_COOLDOWN_MINUTES=$(Get-ConfiguredValue $values "ARBITRAGE_EXECUTION_COOLDOWN_MINUTES" "30")
ARBITRAGE_BALANCE_WAIT_SECONDS=$(Get-ConfiguredValue $values "ARBITRAGE_BALANCE_WAIT_SECONDS" "30")
"@

Set-Content -Path $envPath -Value $envText -Encoding ASCII
Write-Host "Wrote local .env."

$cmdText = @"
@echo off
set "CONTRACT_ACCOUNT=$(Cmd-Value $contractAccount)"
set "DEPLOY_CONTRACT=$(Bool-Text $deployContract)"
set "CLEOS_WALLET=$(Cmd-Value $cleosWallet)"
set "IMPORT_CONTRACT_PRIVATE_KEY=$(Bool-Text $importContractPrivateKey)"
set "SET_CODE_PERMISSION=$(Bool-Text $setCodePermission)"
set "APP_HOST_PORT=$(Cmd-Value $appHostPort)"
set "API_HOST_PORT=$(Cmd-Value $apiHostPort)"
"@
Set-Content -Path $OutCmd -Value $cmdText -Encoding ASCII
