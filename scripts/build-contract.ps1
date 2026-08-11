[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$requiredVersion = "2.3.0"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$contractRoot = Join-Path $repositoryRoot "contract"
$rustToWasmRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "..\rust-to-wasm\rust-to-wasm"))

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "build-output\contract"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$frameworkManifest = Join-Path $rustToWasmRoot "Cargo.toml"
$cliManifest = Join-Path $rustToWasmRoot "eosio-cli\Cargo.toml"
if (!(Test-Path -LiteralPath $frameworkManifest) -or !(Test-Path -LiteralPath $cliManifest)) {
    throw "Rust-to-WASM was not found at the expected sibling path '$rustToWasmRoot'."
}

$frameworkConfig = Get-Content -LiteralPath $frameworkManifest -Raw
$versionPattern = '(?ms)^\[workspace\.package\]\s+.*?^version\s*=\s*"' + [regex]::Escape($requiredVersion) + '"\s*$'
if ($frameworkConfig -notmatch $versionPattern) {
    throw "NeoOnEdge requires Rust-to-WASM $requiredVersion, but '$frameworkManifest' does not declare that workspace version."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cliTargetDirectory = Join-Path $contractRoot "target\rust-to-wasm-cli"
$cliExecutable = Join-Path $cliTargetDirectory "release\wax.exe"

Push-Location $contractRoot
try {
    & cargo build --locked --release --manifest-path $cliManifest --bin wax --target-dir $cliTargetDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Rust-to-WASM $requiredVersion CLI build failed with exit code $LASTEXITCODE."
    }

    & $cliExecutable build neoedge --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Rust-to-WASM $requiredVersion contract build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
