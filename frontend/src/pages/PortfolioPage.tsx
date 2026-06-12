import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, LimitOrder, PortfolioTrade } from '../api/neoEdgeApi'
import { EmptyState, ErrorPanel, LoadingPanel } from '../components/QueryState'
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Input, PageHeader, Table, Td, Th } from '../components/ui'
import { useWallet } from '../lib/walletContext'
import { pushToast } from '../lib/toastStore'

export default function PortfolioPage() {
  const { actor, restoring, login, logout } = useWallet()
  const [search, setSearch] = useState('')

  const effectiveAccount = search || actor

  const swapsQuery = useQuery<PortfolioTrade[]>({
    queryKey: ['portfolio-swaps', effectiveAccount],
    queryFn: () => api.portfolio(effectiveAccount),
    enabled: !!effectiveAccount,
    meta: { errorTitle: 'Portfolio swaps failed to load' },
  })

  const ordersQuery = useQuery<LimitOrder[]>({
    queryKey: ['portfolio-limit-orders', effectiveAccount],
    queryFn: () => api.limitOrders(effectiveAccount),
    enabled: !!effectiveAccount,
    meta: { errorTitle: 'Limit orders failed to load' },
  })

  const connectMutation = useMutation({
    mutationFn: login,
    meta: { errorTitle: 'Wallet connection failed' },
    onSuccess: s => {
      const nextActor = s.actor.toString()
      setSearch('')
      pushToast({ type: 'success', title: 'Wallet connected', message: nextActor })
    },
  })

  const disconnectMutation = useMutation({
    mutationFn: async () => {
      const actorName = actor
      await logout()
      return actorName
    },
    meta: { errorTitle: 'Wallet disconnect failed' },
    onSuccess: actorName => {
      pushToast({ type: 'info', title: 'Wallet disconnected', message: actorName })
    },
  })

  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="Account activity"
        title="Portfolio"
        description="Review routed swaps and Alcor limit-order events for a WAX account."
        action={actor
          ? <Button variant="secondary" onClick={() => disconnectMutation.mutate()} disabled={disconnectMutation.isPending}>Disconnect {actor}</Button>
          : <Button onClick={() => connectMutation.mutate()} disabled={connectMutation.isPending || restoring}>{restoring ? 'Restoring...' : connectMutation.isPending ? 'Connecting...' : 'Connect wallet'}</Button>}
      />

      <Card>
        <CardContent className="flex items-center justify-between gap-3 pt-5 max-sm:flex-col max-sm:items-stretch">
          <Input className="max-w-sm max-sm:max-w-none" placeholder="Search by WAX account" value={search} onChange={e => setSearch(e.target.value)} />
          <Badge variant={effectiveAccount ? 'success' : 'muted'}>{effectiveAccount || 'No account selected'}</Badge>
        </CardContent>
      </Card>

      {!effectiveAccount && <EmptyState title="Choose an account" message="Connect a wallet or enter an account name to load portfolio activity." />}

      {effectiveAccount && (
        <div className="grid gap-5 xl:grid-cols-2">
          <Card>
            <CardHeader>
              <div>
                <p className="mb-2 text-xs font-bold uppercase text-teal-300">Swaps</p>
                <CardTitle>Swap history</CardTitle>
              </div>
              <Button variant="secondary" onClick={() => swapsQuery.refetch()} disabled={swapsQuery.isFetching}>{swapsQuery.isFetching ? 'Refreshing...' : 'Refresh'}</Button>
            </CardHeader>
            <CardContent>
              {swapsQuery.isLoading && <LoadingPanel title="Loading swaps" />}
              {swapsQuery.isError && <ErrorPanel error={swapsQuery.error} onRetry={() => swapsQuery.refetch()} retrying={swapsQuery.isFetching} />}
              {swapsQuery.data && swapsQuery.data.length === 0 && <EmptyState title="No swaps found" message="This account has no indexed swap activity yet." />}
              {swapsQuery.data && swapsQuery.data.length > 0 && (
                <div className="overflow-x-auto">
                  <Table>
                    <thead>
                      <tr>
                        <Th>Time</Th>
                        <Th>DEX</Th>
                        <Th>In</Th>
                        <Th>Out</Th>
                        <Th>Tx</Th>
                      </tr>
                    </thead>
                    <tbody>
                      {swapsQuery.data.map((t, i) => (
                        <tr key={`${t.trxId}-${i}`} className="hover:bg-zinc-900/70">
                          <Td><small className="text-zinc-500">{new Date(t.updatedAtTime).toLocaleString()}</small></Td>
                          <Td><Badge>{t.src}</Badge></Td>
                          <Td>{Number(t.amountIn).toFixed(4)} {t.codeIn}</Td>
                          <Td className="text-emerald-300">{Number(t.amountOut).toFixed(4)} {t.codeOut}</Td>
                          <Td><a className="font-bold text-cyan-300 hover:text-cyan-200" href={`https://waxblock.io/transaction/${t.trxId}`} target="_blank" rel="noreferrer">{t.trxId.slice(0, 8)}...</a></Td>
                        </tr>
                      ))}
                    </tbody>
                  </Table>
                </div>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <div>
                <p className="mb-2 text-xs font-bold uppercase text-teal-300">Orders</p>
                <CardTitle>Limit orders</CardTitle>
              </div>
              <Button variant="secondary" onClick={() => ordersQuery.refetch()} disabled={ordersQuery.isFetching}>{ordersQuery.isFetching ? 'Refreshing...' : 'Refresh'}</Button>
            </CardHeader>
            <CardContent>
              {ordersQuery.isLoading && <LoadingPanel title="Loading limit orders" />}
              {ordersQuery.isError && <ErrorPanel error={ordersQuery.error} onRetry={() => ordersQuery.refetch()} retrying={ordersQuery.isFetching} />}
              {ordersQuery.data && ordersQuery.data.length === 0 && <EmptyState title="No limit orders" message="No indexed Alcor limit-order fills or closes were found." />}
              {ordersQuery.data && ordersQuery.data.length > 0 && (
                <div className="overflow-x-auto">
                  <Table>
                    <thead>
                      <tr>
                        <Th>Type</Th>
                        <Th>Market</Th>
                        <Th>Ask</Th>
                        <Th>Bid</Th>
                        <Th>Time</Th>
                      </tr>
                    </thead>
                    <tbody>
                      {ordersQuery.data.map((order, i) => (
                        <tr key={`${order.trxId}-${i}`} className="hover:bg-zinc-900/70">
                          <Td><Badge variant="muted">{order.type}</Badge></Td>
                          <Td>{order.marketId}</Td>
                          <Td>{Number(order.amountAsk).toFixed(4)} {order.askCode}</Td>
                          <Td className="text-emerald-300">{Number(order.amountBid).toFixed(4)} {order.bidCode}</Td>
                          <Td><small className="text-zinc-500">{new Date(order.updatedAtTime).toLocaleString()}</small></Td>
                        </tr>
                      ))}
                    </tbody>
                  </Table>
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  )
}
