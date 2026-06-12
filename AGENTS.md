# Repository Notes

## Structure

- `contract/`: Rust Antelope contract that executes memo-defined swap routes.
- `backend/src/NeoOnEdge.Core/`: settings, EF Core models, and PostgreSQL context.
- `backend/src/NeoOnEdge.Indexer/`: SHIP and Hyperion action ingestion.
- `backend/src/NeoOnEdge.Workers/`: candles, statistics, and retention jobs.
- `backend/src/NeoOnEdge.Api/`: REST API, route search, and arbitrage scanner.
- `backend/src/NeoOnEdge.ArbitrageExecutor/`: isolated automatic-trade signer.
- `backend/src/SUS.EOS.Sharp/`: vendored Antelope transaction library.
- `frontend/`: React/Vite swap and analytics application.
- `scripts/`: deployment and diagnostic PowerShell scripts.

## Commands

```powershell
dotnet build backend\NeoOnEdge.slnx
npm --prefix frontend run build
docker compose config --quiet
docker compose build arbitrage-executor
```

Use `Get-ChildItem` and `Select-String` for file discovery. Do not assume `rg` is installed.

## Important Invariants

- API token identity is `symbol-contract`.
- `route_excluded_tokens` is the durable deny-list for unsafe fee-on-transfer tokens.
- Existing deployments use `EnsureCreated`, so additive schema changes require idempotent startup SQL.
- Live Alcor paths are accepted as `swap.neo` candidates only when the response is one sequential 100% route with complete pool details.
- The API never receives the arbitrage private key. Only the executor process receives it.
- Automatic buy and sell legs are separate transactions and are not atomic.
