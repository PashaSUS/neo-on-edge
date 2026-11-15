# Architecture

## Data Flow

1. `WaxShipReader` ingests watched WAX actions, with Hyperion for catch-up and fallback.
2. `ActionDispatch` normalizes DEX events into PostgreSQL.
3. Workers derive candles and market statistics.
4. The API exposes catalog, balance, market, trade, and route endpoints.
5. The frontend compares direct Alcor quotes with NeoOnEdge routes.
6. Swaps execute by transferring the input token to `swap.alcor` or `swap.neo` with a route memo.

## Routing

`NeoRouteQuoteService` searches recent constant-product AMM snapshots and Alcor order-book fills. It also asks Alcor for a live route for the exact request.

Historical Alcor V2/V3 trade averages are not executable quotes and are excluded from the NeoOnEdge graph. Concentrated-liquidity output depends on the current tick/liquidity ranges and the pool-specific fee.

A live Alcor route is converted to a `poolId@a2` step only when it contains one pool that `swap.neo` can reproduce exactly. Its contract minimum is capped by Alcor's returned `minReceived`. Multi-pool Alcor routes remain available through the direct Alcor route, which submits Alcor's original combined memo to `swap.alcor`.

## Token Exclusions

`route_excluded_tokens` stores contract/symbol pairs that must not pass through `swap.neo`. Exact matches are removed from the token catalog. The route graph also conservatively removes matching symbols because older indexed AMM rows do not include token contracts.

## Signing Boundary

`NeoOnEdge.ArbitrageExecutor` is a separate .NET process using `SUS.EOS.Sharp`. It:

- receives only internal allow-listed transfer requests,
- serializes the standard token transfer ABI locally,
- signs the binary transaction locally,
- broadcasts through the configured WAX RPC endpoint.

Before automatic execution, the API calls the executor's authenticated preflight. It verifies RPC access, account permission existence, direct signing-key authority, local transfer serialization, and signature-envelope creation. Transfer errors identify whether failure happened during chain-info lookup, serialization/signing, or transaction broadcast.

The web API knows the account and executor shared secret, but not the private key.
