import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api, Market, TokenInfo } from '../api/neoEdgeApi'
import { EmptyState, ErrorPanel, LoadingPanel } from '../components/QueryState'
import { Badge, Button, Card, CardContent, Input, MetricCard, PageHeader, Table, Td, Th } from '../components/ui'

function fmtVol(v: number): string {
  if (v >= 1e6) return `${(v / 1e6).toFixed(2)}M`
  if (v >= 1e3) return `${(v / 1e3).toFixed(2)}K`
  return v.toFixed(2)
}

export default function MarketsPage() {
  const [search, setSearch] = useState('')

  const { data, isLoading, isFetching, isError, error, refetch } = useQuery<Market[]>({
    queryKey: ['markets'],
    queryFn: () => api.markets(),
    refetchInterval: 30_000,
    meta: { errorTitle: 'Markets failed to load' },
  })

  const tokenQuery = useQuery<TokenInfo[]>({
    queryKey: ['tokens'],
    queryFn: () => api.tokens(undefined, 5000),
    staleTime: 5 * 60_000,
    meta: { errorTitle: 'Token list failed', toast: false },
  })

  const markets = data ?? []
  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase()
    if (!needle) return markets
    return markets.filter(m =>
      `${m.tokenA}/${m.tokenB}`.toLowerCase().includes(needle) ||
      m.src.toLowerCase().includes(needle) ||
      m.pairId.toLowerCase().includes(needle),
    )
  }, [markets, search])

  const totals = useMemo(() => ({
    markets: markets.length,
    trades: markets.reduce((sum, m) => sum + Number(m.tradeCount || 0), 0),
    volume: markets.reduce((sum, m) => sum + Number(m.volumeA || 0), 0),
  }), [markets])

  const tokenIdBySymbol = useMemo(() => {
    const map = new Map<string, string>()
    for (const token of tokenQuery.data ?? []) {
      const symbol = token.symbol.toUpperCase()
      if (!map.has(symbol)) map.set(symbol, token.id)
    }
    return map
  }, [tokenQuery.data])

  const chartLink = (market: Market) => {
    const tokenA = tokenIdBySymbol.get(market.tokenA.toUpperCase())
    const tokenB = tokenIdBySymbol.get(market.tokenB.toUpperCase())
    const query = tokenA && tokenB
      ? `?tokenA=${encodeURIComponent(tokenA)}&tokenB=${encodeURIComponent(tokenB)}`
      : ''

    return `/charts/${market.src}/${encodeURIComponent(market.pairId)}${query}`
  }

  if (isLoading) return <LoadingPanel title="Loading markets" />
  if (isError) return <ErrorPanel error={error} onRetry={() => refetch()} retrying={isFetching} />

  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="Indexed liquidity"
        title="Markets"
        description="Live pairs aggregated from AMM and Alcor liquidity events."
        action={<Badge variant={isFetching ? 'warning' : 'success'}>{isFetching ? 'Refreshing' : 'Live'}</Badge>}
      />

      <div className="grid gap-4 md:grid-cols-3">
        <MetricCard label="Markets" value={totals.markets} />
        <MetricCard label="24h trades" value={totals.trades.toLocaleString()} />
        <MetricCard label="Volume A" value={fmtVol(totals.volume)} />
      </div>

      <Card>
        <CardContent className="grid gap-4 pt-5">
          <div className="flex items-center justify-between gap-3 max-sm:flex-col max-sm:items-stretch">
            <Input className="max-w-sm max-sm:max-w-none" value={search} onChange={e => setSearch(e.target.value)} placeholder="Filter pair, DEX, or id" />
            <Button variant="secondary" type="button" onClick={() => refetch()} disabled={isFetching}>
              {isFetching ? 'Refreshing...' : 'Refresh'}
            </Button>
          </div>

          {filtered.length === 0 ? (
            <EmptyState title="No markets found" message="Try a different filter or wait for the indexer to ingest trades." />
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <thead>
                  <tr>
                    <Th>Pair</Th>
                    <Th>DEX</Th>
                    <Th>24h volume A</Th>
                    <Th>24h volume B</Th>
                    <Th>Trades</Th>
                    <Th>Actions</Th>
                  </tr>
                </thead>
                <tbody>
                  {filtered.map((m: Market) => (
                    <tr key={`${m.src}-${m.pairId}`} className="hover:bg-zinc-900/70">
                      <Td><strong className="block text-zinc-50">{m.tokenA}/{m.tokenB}</strong><small className="text-zinc-500">Pair {m.pairId}</small></Td>
                      <Td><Badge>{m.src}</Badge></Td>
                      <Td>{fmtVol(Number(m.volumeA))} {m.tokenA}</Td>
                      <Td>{fmtVol(Number(m.volumeB))} {m.tokenB}</Td>
                      <Td>{Number(m.tradeCount).toLocaleString()}</Td>
                      <Td>
                        <div className="flex gap-3">
                          <Link className="text-sm font-bold text-cyan-300 hover:text-cyan-200" to={chartLink(m)}>Chart</Link>
                          <Link className="text-sm font-bold text-cyan-300 hover:text-cyan-200" to={`/trades/${m.src}/${m.pairId}`}>Trades</Link>
                        </div>
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
