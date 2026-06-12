# Architecture

## Data Flow

1. `WaxShipReader` ingests watched WAX actions, with Hyperion for catch-up and fallback.
2. `ActionDispatch` normalizes DEX events into PostgreSQL.
3. Workers derive candles and market statistics.
4. The API exposes catalog, balance, market, trade, and route endpoints.
5. The frontend compares direct Alcor quotes with NeoOnEdge routes.
6. Swaps execute by transferring the input token to `swap.alcor` or `swap.neo` with a route memo.

## Routing

`NeoRouteQuoteService` searches recent AMM snapshots, Alcor v3 trades, and Alcor order-book fills. It also asks Alcor for a live route for the exact request.

The live lookup fixes an important gap in the historical graph: Alcor edges previously required at least two indexed trades in the last 12 hours and represented only the observed direction. Quiet pools could therefore disappear despite having useful rates. A live Alcor route is converted to `poolId@a2` steps only when it is a single sequential path that `swap.neo` can reproduce.

## Token Exclusions

`route_excluded_tokens` stores contract/symbol pairs that must not pass through `swap.neo`. Exact matches are removed from the token catalog. The route graph also conservatively removes matching symbols because older indexed AMM rows do not include token contracts.

## Signing Boundary

`NeoOnEdge.ArbitrageExecutor` is a separate .NET process using `SUS.EOS.Sharp`. It:

- receives only internal allow-listed transfer requests,
- converts transfer JSON to binary through `abi_json_to_bin`,
- signs the binary transaction locally,
- broadcasts through the configured WAX RPC endpoint.

The web API knows the account and executor shared secret, but not the private key.
