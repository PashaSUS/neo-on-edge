# Manual arbitrage scan script: queries /api/tokens and /api/route to find dry-run opportunities
$api = 'http://localhost:15174'
$timeout = 60
$waxPrecision = 8
$probeAmounts = @(5,10,25)
$maxTokens = 80
$slippage = 2
$maxHops = 6
$minProfitWax = 0.01
$minProfitPercent = 0.1

Write-Output "Fetching tokens..."
try {
    $tokens = Invoke-RestMethod -TimeoutSec $timeout "$api/api/tokens?limit=5000"
} catch {
    Write-Error "Failed fetching tokens: $_"
    exit 1
}

# Determine WAX token id
$waxToken = $tokens | Where-Object { $_.symbol -eq 'WAX' -and $_.contract -eq 'eosio.token' } | Select-Object -First 1
if (-not $waxToken) {
    $waxToken = [pscustomobject]@{ id = 'wax-eosio.token'; contract='eosio.token'; symbol='WAX'; precision=8 }
}
Write-Output "Using WAX token id: $($waxToken.id)"

# Select candidate tokens
$candidates = $tokens | Where-Object { $_.symbol -ne 'WAX' -and $_.id -and $_.contract -and $_.symbol -and ($_.precision -ge 0 -and $_.precision -le 18) } | Sort-Object -Property @{Expression={$_.isTrusted};Descending=$true}, @{Expression={$_.popularityScore};Descending=$true}, @{Expression={$_.symbol};Descending=$false}
if ($candidates.Count -gt $maxTokens) { $candidates = $candidates | Select-Object -First $maxTokens }
Write-Output "Scanning $($candidates.Count) tokens, probes: $($probeAmounts -join ', ')"

$opps = @()

foreach ($token in $candidates) {
    foreach ($inputWax in $probeAmounts) {
        $rawWax = [math]::Floor([decimal]$inputWax * [decimal]1e8)
        if ($rawWax -le 0) { continue }

        $buyUrl = "$api/api/route?input=$($waxToken.id)&output=$($token.id)&amount=$rawWax&inputPrecision=$waxPrecision&outputPrecision=$($token.precision)&slippage=$slippage&maxHops=$maxHops"
        try {
            $buyResp = Invoke-RestMethod -TimeoutSec $timeout $buyUrl
        } catch {
            Write-Verbose "Buy route failed for $($token.id) at $inputWax WAX: $_"
            continue
        }

        $buyQuotes = @()
        if ($buyResp.alcor) {
            $out = ($buyResp.alcor.output -split ' ')[0]
            [decimal]$alcorOutput = 0
            if ([decimal]::TryParse($out, [ref]$alcorOutput)) {
                $alcorOutputRaw = [math]::Floor($alcorOutput * [math]::Pow(10,$token.precision))
                if ($alcorOutputRaw -gt 0) { $buyQuotes += [pscustomobject]@{source='Alcor'; output=$alcorOutput; raw=$alcorOutputRaw; memo=$buyResp.alcor.memo } }
            }
        }
        if ($buyResp.neo) {
            $out = ($buyResp.neo.output -split ' ')[0]
            [decimal]$neoOutput = 0
            if ([decimal]::TryParse($out, [ref]$neoOutput)) {
                $neoOutputRaw = [math]::Floor($neoOutput * [math]::Pow(10,$token.precision))
                if ($neoOutputRaw -gt 0) { $buyQuotes += [pscustomobject]@{source='NeoOnEdge'; output=$neoOutput; raw=$neoOutputRaw; memo=$buyResp.neo.memo } }
            }
        }

        foreach ($buy in $buyQuotes) {
            $sellUrl = "$api/api/route?input=$($token.id)&output=$($waxToken.id)&amount=$($buy.raw)&inputPrecision=$($token.precision)&outputPrecision=$waxPrecision&slippage=$slippage&maxHops=$maxHops"
            try {
                $sellResp = Invoke-RestMethod -TimeoutSec $timeout $sellUrl
            } catch {
                Write-Verbose "Sell route failed for $($token.id) amount $($buy.raw): $_"
                continue
            }

            $sellBestAmt = 0
            $sellBestSource = $null
            if ($sellResp.alcor) {
                $out = ($sellResp.alcor.output -split ' ')[0]
                [decimal]$amt=0
                if ([decimal]::TryParse($out, [ref]$amt) -and $amt -gt $sellBestAmt) { $sellBestAmt = $amt; $sellBestSource='Alcor' }
            }
            if ($sellResp.neo) {
                $out = ($sellResp.neo.output -split ' ')[0]
                [decimal]$amt=0
                if ([decimal]::TryParse($out, [ref]$amt) -and $amt -gt $sellBestAmt) { $sellBestAmt = $amt; $sellBestSource='NeoOnEdge' }
            }

            if ($sellBestAmt -gt 0) {
                $profitWax = $sellBestAmt - $inputWax
                $profitPercent = if ($inputWax -ne 0) { $profitWax / $inputWax * 100 } else { 0 }
                if ($profitWax -ge $minProfitWax -and $profitPercent -ge $minProfitPercent) {
                    $opps += [pscustomobject]@{
                        Token = $token.symbol
                        TokenId = $token.id
                        InputWax = $inputWax
                        BuySource = $buy.source
                        BuyOutput = $buy.output
                        SellSource = $sellBestSource
                        FinalWax = [math]::Round($sellBestAmt,8)
                        ProfitWax = [math]::Round($profitWax,8)
                        ProfitPercent = [math]::Round($profitPercent,3)
                    }
                }
            }
        }
    }
}

# Output top results
if ($opps.Count -eq 0) {
    Write-Output "No profitable opportunities found."
} else {
    $opps | Sort-Object -Property @{Expression={$_.ProfitWax};Descending=$true}, @{Expression={$_.ProfitPercent};Descending=$true} | Select-Object -First 20 | Format-Table -AutoSize
}

Write-Output "Scan completed."