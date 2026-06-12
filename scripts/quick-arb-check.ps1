$api='http://localhost:15174'
$pairs = @('DMTWAXB','DMTWAXC','SHIWAXB')
foreach ($p in $pairs) {
    try {
        $s = Invoke-RestMethod -TimeoutSec 20 "$api/api/markets/t/$p/stats"
        Write-Output "=== Pair $p stats ==="
        $s | ConvertTo-Json -Depth 4 | Write-Output
    } catch {
        Write-Output "Pair $($p): not found or error: $($_)"
    }
}

# Check Alcor order-book market 9271
try {
    $s = Invoke-RestMethod -TimeoutSec 20 "$api/api/markets/ad/9271/stats"
    Write-Output "=== Alcor market 9271 ==="
    $s | ConvertTo-Json -Depth 4 | Write-Output
} catch {
    Write-Output "Alcor 9271: not found or error: $($_)"
}

# Sample route checks for DMT token
$dmtId = 'dmt-defiminingtf'
$waxId = 'wax-eosio.token'
$probes = @(5,10)
foreach ($probe in $probes) {
    $raw = [math]::Floor([decimal]$probe * [decimal]1e8)
    Write-Output "\n--- Route: WAX -> DMT (probe $probe WAX, raw $raw) ---"
    try {
        $r = Invoke-RestMethod -TimeoutSec 60 "$api/api/route?input=$waxId&output=$dmtId&amount=$raw&inputPrecision=8&outputPrecision=8&slippage=2&maxHops=6"
        $r | ConvertTo-Json -Depth 6 | Write-Output
    } catch {
        Write-Output "Route WAX->DMT failed: $($_)"
        continue
    }

    # If alcor returned an output, probe reverse route using that amount
    if ($r.alcor -and $r.alcor.output) {
        $out = ($r.alcor.output -split ' ')[0]
        [decimal]$alcorOut = 0
        if ([decimal]::TryParse($out, [ref]$alcorOut)) {
            $alcorRaw = [math]::Floor($alcorOut * [math]::Pow(10,8))
            Write-Output "--- Reverse (Alcor) DMT -> WAX using raw $alcorRaw ---"
            try { $rev = Invoke-RestMethod -TimeoutSec 60 "$api/api/route?input=$dmtId&output=$waxId&amount=$alcorRaw&inputPrecision=8&outputPrecision=8&slippage=2&maxHops=6"; $rev | ConvertTo-Json -Depth 6 | Write-Output } catch { Write-Output "Reverse route failed: $($_)" }
        }
    }

    if ($r.neo -and $r.neo.output) {
        $out = ($r.neo.output -split ' ')[0]
        [decimal]$neoOut = 0
        if ([decimal]::TryParse($out, [ref]$neoOut)) {
            $neoRaw = [math]::Floor($neoOut * [math]::Pow(10,8))
            Write-Output "--- Reverse (Neo) DMT -> WAX using raw $neoRaw ---"
            try { $rev2 = Invoke-RestMethod -TimeoutSec 60 "$api/api/route?input=$dmtId&output=$waxId&amount=$neoRaw&inputPrecision=8&outputPrecision=8&slippage=2&maxHops=6"; $rev2 | ConvertTo-Json -Depth 6 | Write-Output } catch { Write-Output "Reverse route failed: $($_)" }
        }
    }
}

Write-Output "\nQuick checks complete."