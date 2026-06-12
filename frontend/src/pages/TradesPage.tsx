import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, Trade } from '../api/neoEdgeApi'
import { EmptyState, ErrorPanel, LoadingPanel } from '../components/QueryState'
import { Button, Card, CardContent, PageHeader, Table, Td, Th } from '../components/ui'
import { useSignalR } from '../hooks/useSignalR'

function fmtTime(ms: number): string {
  return new Date(ms).toLocaleTimeString()
}

export default function TradesPage() {
  const { src = 'd', id = '1' } = useParams<{ src: string; id: string }>()
  const [live, setLive] = useState<Trade[]>([])

  const { data, isLoading, isError, error, isFetching, refetch } = useQuery<Trade[]>({
    queryKey: ['trades', src, id],
    queryFn: () => api.trades(src, id, 50),
    meta: { errorTitle: 'Trades failed to load' },
  })

  useSignalR(
    'SubscribeTrades', 'UnsubscribeTrades', 'trade', [src, id],
    (t: Trade) => setLive(prev => [t, ...prev].slice(0, 100))
  )

  const rows = [...live, ...(data ?? [])].slice(0, 100)

  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="Tape"
        title={`${src.toUpperCase()} - Pair ${id} trades`}
        description={`${live.length} live updates in this session.`}
        action={<Button variant="secondary" type="button" onClick={() => refetch()} disabled={isFetching}>{isFetching ? 'Refreshing...' : 'Refresh'}</Button>}
      />

      <Card>
        <CardContent className="pt-5">
          {isLoading && <LoadingPanel title="Loading trades" />}
          {isError && <ErrorPanel error={error} onRetry={() => refetch()} retrying={isFetching} />}
          {!isLoading && !isError && rows.length === 0 && <EmptyState title="No trades found" message="No indexed trades are available for this pair yet." />}
          {rows.length > 0 && (
            <div className="overflow-x-auto">
              <Table>
                <thead>
                  <tr>
                    <Th>Time</Th>
                    <Th>Account</Th>
                    <Th>Amount in</Th>
                    <Th>Amount out</Th>
                    <Th>Tx</Th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((t, i) => (
                    <tr key={`${t.trxId}-${i}`} className="hover:bg-zinc-900/70">
                      <Td><small className="text-zinc-500">{fmtTime(t.updatedAtTime)}</small></Td>
                      <Td><code className="text-xs text-zinc-300">{t.maker}</code></Td>
                      <Td>{Number(t.amountIn).toFixed(4)} {t.codeIn}</Td>
                      <Td className="text-emerald-300">{Number(t.amountOut).toFixed(4)} {t.codeOut}</Td>
                      <Td>
                        <a className="font-bold text-cyan-300 hover:text-cyan-200" href={`https://waxblock.io/transaction/${t.trxId}`} target="_blank" rel="noreferrer">
                          {t.trxId.slice(0, 8)}...
                        </a>
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
