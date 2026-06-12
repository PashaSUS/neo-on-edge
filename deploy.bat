@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "ROOT_ARG=%ROOT%"
if "%ROOT_ARG:~-1%"=="\" set "ROOT_ARG=%ROOT_ARG:~0,-1%"
set "OUT=%ROOT%build-output"
set "CONTRACT_DIR=%ROOT%contract"
set "CONTRACT_OUT=%OUT%\contract"
set "REQUESTED_CONTRACT_ACCOUNT=%~1"
set "CONFIG_SCRIPT=%ROOT%scripts\prepare-deploy-env.ps1"
set "CONFIG_CMD=%TEMP%\neoedge-deploy-%RANDOM%-%RANDOM%.cmd"
set "CONTRACT_CRATE=neoedge"
set "CONTRACT_WASM=neoedge.wasm"
set "CONTRACT_ABI=neoedge.abi"
set "LOCAL_WAX_CLI=%ROOT%..\rust-to-wasm\rust-to-wasm\eosio-cli\Cargo.toml"

where powershell >nul 2>nul
if errorlevel 1 (
  echo ERROR: PowerShell was not found on PATH. It is required for guided deploy setup.
  exit /b 1
)

if not exist "%CONFIG_SCRIPT%" (
  echo ERROR: Deploy setup script was not found: %CONFIG_SCRIPT%
  exit /b 1
)

if "%REQUESTED_CONTRACT_ACCOUNT%"=="" (
  call powershell -NoProfile -ExecutionPolicy Bypass -File "%CONFIG_SCRIPT%" -Root "%ROOT_ARG%" -OutCmd "%CONFIG_CMD%"
) else (
  call powershell -NoProfile -ExecutionPolicy Bypass -File "%CONFIG_SCRIPT%" -Root "%ROOT_ARG%" -RequestedContractAccount "%REQUESTED_CONTRACT_ACCOUNT%" -OutCmd "%CONFIG_CMD%"
)
if errorlevel 1 exit /b 1
if not exist "%CONFIG_CMD%" (
  echo ERROR: Deploy setup did not write expected command file: %CONFIG_CMD%
  exit /b 1
)
call "%CONFIG_CMD%"
del /q "%CONFIG_CMD%" >nul 2>nul

if "%CONTRACT_ACCOUNT%"=="" set "CONTRACT_ACCOUNT=swap.neo"
if "%DEPLOY_CONTRACT%"=="" set "DEPLOY_CONTRACT=false"
if "%APP_HOST_PORT%"=="" set "APP_HOST_PORT=15173"
if "%API_HOST_PORT%"=="" set "API_HOST_PORT=15174"

echo.
echo == NeoOnEdge deployment build ==
echo Output: %OUT%
echo Contract account: %CONTRACT_ACCOUNT%
echo.

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%CONTRACT_OUT%" || exit /b 1

echo == Building smart contract and ABI ==
pushd "%CONTRACT_DIR%" || exit /b 1
if not defined WAX_CLI_MANIFEST set "WAX_CLI_MANIFEST=%LOCAL_WAX_CLI%"
if exist "%WAX_CLI_MANIFEST%" (
  call cargo run --quiet --manifest-path "%WAX_CLI_MANIFEST%" -- build "%CONTRACT_CRATE%" --output "%CONTRACT_OUT%"
) else (
  where wax >nul 2>nul
  if not errorlevel 1 (
    call wax build "%CONTRACT_CRATE%" --output "%CONTRACT_OUT%"
  ) else (
    echo ERROR: wax CLI was not found and WAX_CLI_MANIFEST does not point to eosio-cli\Cargo.toml.
    echo Install it with: cargo install --path path\to\rust-to-wasm\eosio-cli
    exit /b 1
  )
)
if errorlevel 1 exit /b 1
popd

set "WASM=%CONTRACT_OUT%\%CONTRACT_WASM%"
if not exist "%WASM%" (
  echo ERROR: Expected contract wasm was not found: %WASM%
  exit /b 1
)
if not exist "%CONTRACT_OUT%\%CONTRACT_ABI%" (
  echo ERROR: Expected contract ABI was not generated: %CONTRACT_OUT%\%CONTRACT_ABI%
  exit /b 1
)
copy /y "%WASM%" "%OUT%\%CONTRACT_WASM%" >nul
copy /y "%CONTRACT_OUT%\%CONTRACT_ABI%" "%OUT%\%CONTRACT_ABI%" >nul

if /I "%DEPLOY_CONTRACT%"=="true" (
  echo == Deploying contract to %CONTRACT_ACCOUNT% ==
  where cleos >nul 2>nul
  if errorlevel 1 (
    echo ERROR: cleos was not found on PATH. Cannot deploy contract.
    exit /b 1
  )
  if /I "%IMPORT_CONTRACT_PRIVATE_KEY%"=="true" (
    echo == Importing contract private key into cleos wallet %CLEOS_WALLET% ==
    call powershell -NoProfile -ExecutionPolicy Bypass -Command "$key = Read-Host 'Contract private key (hidden)' -AsSecureString; if ($key.Length -eq 0) { Write-Error 'Contract private key was empty'; exit 1 }; $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($key); try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr); & cleos wallet import --name $env:CLEOS_WALLET --private-key $plain; exit $LASTEXITCODE } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }"
    if errorlevel 1 exit /b 1
  )
  cleos set contract "%CONTRACT_ACCOUNT%" "%CONTRACT_OUT%" %CONTRACT_WASM% %CONTRACT_ABI%
  if errorlevel 1 exit /b 1
  if /I "%SET_CODE_PERMISSION%"=="true" (
    echo == Ensuring %CONTRACT_ACCOUNT%@eosio.code can authorize inline actions ==
    cleos set account permission "%CONTRACT_ACCOUNT%" active --add-code -p "%CONTRACT_ACCOUNT%@active"
    if errorlevel 1 exit /b 1
  )
) else (
  echo Contract deploy skipped. Set DEPLOY_CONTRACT=true to run cleos set contract for %CONTRACT_ACCOUNT%.
)

echo == Deploying Docker stack ==
where docker >nul 2>nul
if errorlevel 1 (
  echo ERROR: Docker was not found on PATH.
  exit /b 1
)

if not exist "%ROOT%.env" (
  echo ERROR: .env was not found after guided setup.
  exit /b 1
)

pushd "%ROOT%" || exit /b 1
call docker compose version >nul 2>nul
if errorlevel 1 (
  echo ERROR: Docker Compose v2 was not found. Install Docker Desktop or the docker compose plugin.
  exit /b 1
)
call docker compose up -d --build --remove-orphans
if errorlevel 1 exit /b 1
popd

echo.
echo Contract output ready:
echo   %CONTRACT_OUT%
echo Docker stack deployed with CONTRACT_ACCOUNT=%CONTRACT_ACCOUNT%
echo App URL: http://localhost:%APP_HOST_PORT%
echo API URL: http://localhost:%API_HOST_PORT%/api
echo.
echo Usage:
echo   deploy.bat              Build contract artifacts and deploy Docker stack using swap.neo
echo   deploy.bat test.pasha   Build contract artifacts and deploy Docker stack using test.pasha
echo   set DEPLOY_CONTRACT=true ^&^& deploy.bat
echo.
endlocal
