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
└── .env.example
```

## Contract

DEX sources supported: Alcor v2 (`a2`), Alcor DEX orderbook (`ad`), Defibox (`d`), Taco (`t`), Adex (`a`), NeftyBlocks (`n`), WaxFusion (`wf`).

```
# Build WASM + ABI
deploy.bat

# Optional on-chain deploy to the configured account
set DEPLOY_CONTRACT=true && deploy.bat
```

The first argument to `deploy.bat` is the contract account. It now defaults to `swap.neo` for production; pass `test.pasha` when you want the local/testing target.

## Backend

```
cd backend
dotnet build
dotnet run --project src/NeoOnEdge.Api
```

Requires PostgreSQL and Redis running. Configure via `appsettings.json` or environment variables (see `.env.example`).

Token data is available from `GET /api/tokens`. The catalog is seeded from well-known defaults, refreshed from Alcor's WAX token list, and merged with indexed token pairs from PostgreSQL. Indexer activity publishes `tokens_update` through Redis; the API bridges that to SignalR so the frontend can refetch the catalog live.

Candles are available from locally computed NeoOnEdge bars through `GET /api/klines` and from Alcor through `GET /api/klines/alcor?tokenA=<id>&tokenB=<id>&resolution=<minutes>&from=<ms>&to=<ms>`. Wallet balances for the selected swap tokens use `GET /api/balances/{account}` and the configurable `WAX_RPC_ENDPOINT` nodeos endpoint.

The backend also includes an optional dry-run arbitrage monitor. When `ARBITRAGE_SCANNER_ENABLED=true` and `ARBITRAGE_DISCORD_WEBHOOK_URL` is set, it scans WAX round trips such as WAX → token → WAX, compares Alcor and NeoOnEdge quote candidates for each leg, and posts top quote-only opportunities to Discord. It never stores opportunity rows, signs transactions, or executes swaps. The default calculation cap is `ARBITRAGE_MAX_INPUT_WAX=100`, with configurable probe sizes in `ARBITRAGE_PROBE_WAX_AMOUNTS`.

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
cp .env.example .env   # fill in passwords
docker compose up -d
```

Or use the deployment helper, which builds the contract locally and deploys the rest of the stack with Docker Compose:

```powershell
deploy.bat              # uses swap.neo
deploy.bat test.pasha   # testing account override
```

Services:
- PostgreSQL 16 on internal port 5432
- Redis 7 on internal port 6379
- .NET API and SignalR on internal port 5000, exposed on `API_HOST_PORT` (`15174` by default)
- React production build served by `serve` on internal port 3000, exposed on `APP_HOST_PORT` (`15173` by default)

Configuration:
- `CONTRACT_ACCOUNT` controls the indexed/router contract account for backend and frontend containers. The default is `swap.neo`.
- `PUBLIC_API_BASE_URL` and `PUBLIC_HUB_URL` may be same-origin paths such as `/api` and `/hub`, or full URLs such as `https://api.yourdomain.com/api` and `https://api.yourdomain.com/hub`. Use full URLs when the API is exposed separately from the static `serve` frontend. If the website is loaded over HTTPS, the API/hub URLs must also be HTTPS. The full URL must resolve to the backend API, not to the static frontend app.
- `ALCOR_ENDPOINT` controls Alcor API calls. The backend still uses it for token/candle/arbitrage support, while the swap page builds it into the frontend so browser clients quote Alcor routes directly.
- `DEPLOY_CONTRACT=true` makes `deploy.bat` run `cleos set contract` after building `neoedge.wasm` and `neoedge.abi`.
- `POSTGRES_PASSWORD` and `ADMIN_SECRET` should be generated locally in `.env`; `deploy.bat` no longer falls back to weak test defaults.
- `deploy.bat` runs a guided setup that can generate missing secrets, ask for the Discord webhook, choose the website/API ports, optionally import a contract private key into an unlocked cleos wallet, and ensure the contract account has `eosio.code` permission for inline swaps.
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
