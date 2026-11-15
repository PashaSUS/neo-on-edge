# Arbitrage Runbook

## Quote-Only Mode

```dotenv
ARBITRAGE_SCANNER_ENABLED=true
ARBITRAGE_EXECUTION_ENABLED=false
ARBITRAGE_DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
```

This scans WAX round trips and reports opportunities without signing transactions.

## Automatic Mode

Use a dedicated account funded only with the amount intended for the scanner:

```dotenv
ARBITRAGE_SCANNER_ENABLED=true
ARBITRAGE_EXECUTION_ENABLED=true
ARBITRAGE_ACCOUNT=youraccount
ARBITRAGE_PERMISSION=active
ARBITRAGE_PRIVATE_KEY=PVT_K1_...
ARBITRAGE_EXECUTOR_SECRET=generate-a-long-random-secret
ARBITRAGE_MAX_EXECUTION_WAX=1
```

`deploy.bat` prompts for these values and hides private-key input. Start with a small execution cap.

If your RPC provider is inconsistent, the executor can use a dedicated comma-separated fallback list via `WAX_RPC_ENDPOINTS`. This affects only the isolated signer container; the main API still uses `WAX_RPC_ENDPOINT`.

## Execution Sequence

1. Select an alert-safe opportunity below the execution cap.
2. Acquire a per-token Redis cooldown lock.
3. Record the intermediate-token balance.
4. Sign and broadcast the buy transfer.
5. Wait for the actual balance increase.
6. Obtain a fresh sell quote for the received amount.
7. Sign and broadcast the unwind transfer.

Split quotes are never automatically executed.

The scanner compares executable direct Alcor quotes with NeoOnEdge and split candidates. `GET /api/arbitrage/status` reports the number of comparable quotes, how often NeoOnEdge produced more output, and the best observed improvement from the latest scan. These measurements demonstrate quote improvement; they do not guarantee final execution output.

Before submitting a trade, the API runs the executor preflight. A failed preflight leaves quote scanning active but skips automatic execution. The status response includes the latest preflight stage and error. Transfer failures identify one of `get_info`, `serialize_and_sign`, or `push_transaction`; chain rejection text and the selected RPC endpoint are retained in backend logs.

## Risk

The two legs are separate blockchain transactions. Price movement, RPC delay, resource exhaustion, token behavior, or router failure can leave the account holding the intermediate token. The scanner attempts a fresh unwind route, but it cannot guarantee profit or atomicity.

The executor has no public Docker port and accepts only transfers to the configured `swap.neo` account and `swap.alcor`.
