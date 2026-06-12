import type { TokenBalance } from '../../api/neoEdgeApi'
import { LoadingMark } from '../ui'
import type { Token } from './types'

export function BalanceText({ actor, balance, loading, token }: { actor: string; balance: TokenBalance | undefined; loading: boolean; token: Token }) {
  if (!actor) return <span>Balance: -</span>
  if (loading && !balance) return <span className="inline-flex items-center gap-1.5">Balance: <LoadingMark className="h-3.5 w-3.5" label="Loading balance" /></span>
  if (balance?.error) return <span className="text-amber-300">Balance unavailable</span>

  const amount = Number(balance?.amount ?? 0)
  return <span>Balance: {amount.toLocaleString(undefined, { maximumFractionDigits: Math.min(token.precision, 8) })} {token.symbol}</span>
}