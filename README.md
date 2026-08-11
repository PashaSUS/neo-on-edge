# NeoOnEdge

WAX blockchain DEX aggregator — multi-hop swap router and full-stack analytics platform.

## Structure

```
neo-on-edge/
├── contract/           Rust smart contract (WAX WASM)
├── backend/            .NET 10 ASP.NET Core backend
│   └── src/
│       ├── NeoOnEdge.Core/       Models, EF Core DbContext, config
│       ├── NeoOnEdge.Indexer/    SHIP + Hyperion blockchain reader
│       ├── NeoOnEdge.Workers/    Klines + stats background services
│       └── NeoOnEdge.Api/        REST API + SignalR hub
├── frontend/           React 19 + Vite + Tailwind CSS v4
├── docker-compose.yml
├── .env.development.example
└── .env.production.example
```

## Contract

DEX sources supported: Alcor v2 (`a2`), Alcor DEX orderbook (`ad`), Defibox (`d`), Taco (`t`), Adex (`a`), NeftyBlocks (`n`), WaxFusion (`wf`).

```
# Build WASM + ABI with the required Rust-to-WASM 2.3.0 toolchain
.\scripts\build-contract.ps1
```

The build script verifies the sibling `rust-to-wasm` workspace is version `2.3.0` and runs that exact CLI source instead of relying on a potentially older globally installed `wax` command.

Contract deployment is intentionally separate from `deploy.bat`, which only configures and deploys the Docker stack.

## Backend

```
cd backend
dotnet build
dotnet run --project src/NeoOnEdge.Api
```

Requires PostgreSQL and Redis running. Configure via `appsettings.json` or the environment templates.

Token data is available from `GET /api/tokens`. The catalog is seeded from well-known defaults, refreshed from Alcor's WAX token list, and merged with indexed token pairs from PostgreSQL. Indexer activity publishes `tokens_update` through Redis; the API bridges that to SignalR so the frontend can refetch the catalog live.

Candles are available from locally computed NeoOnEdge bars through `GET /api/klines` and from Alcor through `GET /api/klines/alcor?tokenA=<id>&tokenB=<id>&resolution=<minutes>&from=<ms>&to=<ms>`. Wallet balances for the selected swap tokens use `GET /api/balances/{account}` and the configurable `WAX_RPC_ENDPOINT` nodeos endpoint.

The backend includes an optional arbitrage scanner. With `ARBITRAGE_SCANNER_ENABLED=true`, it scans WAX round trips such as WAX → token → WAX and compares Alcor and NeoOnEdge candidates. Discord-only operation remains the default. `ARBITRAGE_EXECUTION_ENABLED=true` enables two-leg automatic execution through the isolated .NET signer; read `docs/ARBITRAGE.md` before enabling it.

Routes exclude contract/symbol pairs stored in PostgreSQL table `route_excluded_tokens`. The startup bootstrap seeds known fee-on-transfer tokens that are unsafe for the `swap.neo` receiver. NeoOnEdge routing supplements indexed liquidity with exact live single-pool Alcor quotes. Multi-pool concentrated-liquidity routes execute directly through Alcor's original combined memo instead of being approximated as separate `swap.neo` hops.

The indexer can rotate through multiple SHIP websocket endpoints via `WAX_SHIP_ENDPOINTS`. Public SHIP endpoints change often, so `WAX_SHIP_ENDPOINT` can be set to a preferred provider and `WAX_SHIP_ENDPOINTS` can hold comma-separated fallbacks. When SHIP is unavailable, `HYPERION_FALLBACK_ENABLED=true` keeps the backend refreshing indexed actions through the configured Hyperion endpoint, with `HYPERION_REQUEST_TIMEOUT_SECONDS` preventing a slow provider from blocking the loop. Empty databases start from `HYPERION_INITIAL_LOOKBACK_HOURS` so recent route indexes seed quickly before deeper history is configured.

## Frontend

```
cd frontend
npm install
npm run dev        # dev server on :3177, proxying to VITE_DEV_API_TARGET or the local API on :5063
npm run build      # production build to dist/
npm run serve      # serve the production dist/ locally
```

## Docker

```
Copy-Item .env.development.example .env.development
# Replace placeholder secrets before starting the stack.
docker compose --env-file .env.development up -d
```

The recommended deployment helper asks whether to use development or production, writes the matching local env file, and deploys only the Docker stack:

```powershell
deploy.bat               # interactive environment selection
deploy.bat development   # uses .env.development
deploy.bat production    # uses .env.production
```

Development binds all published ports to `127.0.0.1` and configures browser API, SignalR, and CORS communication for localhost only. Production binds to `0.0.0.0` and prompts for public API, hub, and allowed-origin values. On its first production run, the helper imports values from a legacy `.env` when present.

Services:
- PostgreSQL 16 on internal port 5432
- pgAdmin at `PGADMIN_HOST_PORT` (`15175` by default); sign in with `PGADMIN_EMAIL` and `POSTGRES_PASSWORD`, then connect to host `postgres`
- Redis 7 on internal port 6379
- .NET API and SignalR on internal port 5000, exposed on `API_HOST_PORT` (`15174` by default)
- React production build served by `serve` on internal port 3000, exposed on `APP_HOST_PORT` (`15173` by default)

Configuration:
- `CONTRACT_ACCOUNT` controls the indexed/router contract account for backend and frontend containers. The default is `swap.neo`.
- `PUBLIC_API_BASE_URL` and `PUBLIC_HUB_URL` may be same-origin paths such as `/api` and `/hub`, or full URLs such as `https://api.yourdomain.com/api` and `https://api.yourdomain.com/hub`. Use full URLs when the API is exposed separately from the static `serve` frontend. If the website is loaded over HTTPS, the API/hub URLs must also be HTTPS. The full URL must resolve to the backend API, not to the static frontend app.
- `ALCOR_ENDPOINT` controls Alcor API calls. The backend still uses it for token/candle/arbitrage support, while the swap page builds it into the frontend so browser clients quote Alcor routes directly.
- `HOST_BIND_ADDRESS` is forced to `127.0.0.1` in development and defaults to `0.0.0.0` in production.
- `POSTGRES_PASSWORD` and `ADMIN_SECRET` are generated independently in `.env.development` and `.env.production`.
- `deploy.bat` runs a guided Docker setup that can generate secrets, ask for the Discord webhook, and choose website, API, and pgAdmin ports.
- Contract admin actions (`setfee`, `setfeewallet`, `adddiscard`, `remdiscard`) require `swap.neo` authorization.
- `runstep` is intentionally self-authorized and reached through contract inline actions with `eosio.code`; website users start swaps with token transfers to the contract. `madeonwoe` has no auth gate and can be called by anyone.
- Incoming callback transfers from known DEX/router contracts are ignored by `on_transfer`; route execution continues through `runstep` using balance-delta checks.

## Swap Memo Format

```
swap,<min_return_raw>,<TICKER>@<contract>|<PAIR_ID>@<src>[-<PAIR_ID>@<src>...]
```

Example (WAX → USDT via Defibox pair 12):
```
swap,0,USDT@usdt.alcor|12@d
```

Multi-hop (WAX → WAXE → USDT):
```
swap,0,USDT@usdt.alcor|5@d-12@d
```
