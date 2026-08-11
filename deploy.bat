@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0"
set "ROOT_ARG=%ROOT%"
if "%ROOT_ARG:~-1%"=="\" set "ROOT_ARG=%ROOT_ARG:~0,-1%"
set "CONFIG_SCRIPT=%ROOT%scripts\prepare-deploy-env.ps1"
set "CONFIG_CMD=%TEMP%\neoedge-deploy-%RANDOM%-%RANDOM%.cmd"
set "DEPLOY_ENVIRONMENT=%~1"

where powershell >nul 2>nul
if errorlevel 1 (
  echo ERROR: PowerShell was not found on PATH. It is required for guided deploy setup.
  exit /b 1
)

if not exist "%CONFIG_SCRIPT%" (
  echo ERROR: Deploy setup script was not found: %CONFIG_SCRIPT%
  exit /b 1
)

:select_environment
if not defined DEPLOY_ENVIRONMENT (
  echo.
  echo Select deployment environment:
  echo   1. Development ^(localhost only^)
  echo   2. Production
  set /p "DEPLOY_ENVIRONMENT=Environment [1]: "
  if not defined DEPLOY_ENVIRONMENT set "DEPLOY_ENVIRONMENT=development"
)

if /I "%DEPLOY_ENVIRONMENT%"=="1" set "DEPLOY_ENVIRONMENT=development"
if /I "%DEPLOY_ENVIRONMENT%"=="dev" set "DEPLOY_ENVIRONMENT=development"
if /I "%DEPLOY_ENVIRONMENT%"=="2" set "DEPLOY_ENVIRONMENT=production"
if /I "%DEPLOY_ENVIRONMENT%"=="prod" set "DEPLOY_ENVIRONMENT=production"
if /I "%DEPLOY_ENVIRONMENT%"=="development" set "DEPLOY_ENVIRONMENT=development"
if /I "%DEPLOY_ENVIRONMENT%"=="production" set "DEPLOY_ENVIRONMENT=production"

if /I not "%DEPLOY_ENVIRONMENT%"=="development" if /I not "%DEPLOY_ENVIRONMENT%"=="production" (
  echo ERROR: Choose development or production.
  set "DEPLOY_ENVIRONMENT="
  goto select_environment
)

call powershell -NoProfile -ExecutionPolicy Bypass -File "%CONFIG_SCRIPT%" -Root "%ROOT_ARG%" -Environment "%DEPLOY_ENVIRONMENT%" -OutCmd "%CONFIG_CMD%"
if errorlevel 1 exit /b 1
if not exist "%CONFIG_CMD%" (
  echo ERROR: Deploy setup did not write expected command file: %CONFIG_CMD%
  exit /b 1
)
call "%CONFIG_CMD%"
del /q "%CONFIG_CMD%" >nul 2>nul

echo.
echo == Deploying NeoOnEdge %DEPLOY_ENVIRONMENT% Docker stack ==
echo Environment file: %ENV_FILE%
echo.

where docker >nul 2>nul
if errorlevel 1 (
  echo ERROR: Docker was not found on PATH.
  exit /b 1
)

if not exist "%ENV_FILE%" (
  echo ERROR: Environment file was not found after guided setup: %ENV_FILE%
  exit /b 1
)

pushd "%ROOT%" || exit /b 1
call docker compose version >nul 2>nul
if errorlevel 1 (
  echo ERROR: Docker Compose v2 was not found. Install Docker Desktop or the docker compose plugin.
  exit /b 1
)
call docker compose --env-file "%ENV_FILE%" config --quiet
if errorlevel 1 exit /b 1
call docker compose --env-file "%ENV_FILE%" up -d --build --remove-orphans
if errorlevel 1 exit /b 1
popd

echo.
echo Docker stack deployed using %ENV_FILE%.
echo App URL: %APP_URL%
echo API URL: %API_URL%
echo pgAdmin URL: %PGADMIN_URL%
echo.
echo Usage:
echo   deploy.bat               Prompt for development or production
echo   deploy.bat development   Deploy with .env.development
echo   deploy.bat production    Deploy with .env.production
echo.
endlocal
