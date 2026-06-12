import { cn } from '../../lib/utils'
import { LoadingMark } from '../ui'

export function QuoteCard({ title, value, detail, fee, winner, selected, loading, badge, onSelect }: {
  title: string
  value: string
  detail: string
  fee?: string
  winner: boolean
  selected?: boolean
  loading?: boolean
  badge?: string
  onSelect?: () => void
}) {
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onSelect}
      className={cn(
        'grid gap-2 rounded-lg border border-zinc-800 bg-zinc-900/70 p-4 text-left transition hover:border-teal-500/70 hover:bg-zinc-900 focus:border-teal-400 focus:outline-none focus:ring-2 focus:ring-teal-500/20',
        winner && 'border-teal-400/60 bg-teal-500/10',
        selected && 'border-cyan-300/70 bg-cyan-500/10 shadow-[0_0_0_1px_rgba(103,232,249,0.16)]',
      )}
    >
      <span className="flex items-center justify-between gap-2">
        <span className="text-xs font-semibold text-zinc-400">{title}</span>
        {badge && <span className="rounded-full bg-zinc-800 px-2 py-0.5 text-[10px] font-bold uppercase tracking-normal text-zinc-300">{badge}</span>}
      </span>
      {loading && value === '-' ? <LoadingMark className="h-6 w-6" label={`Loading ${title}`} /> : <strong className="text-xl text-zinc-50">{value}</strong>}
      <small className="text-zinc-400">{detail}</small>
      {fee && <small className="font-semibold text-teal-300">{fee}</small>}
    </button>
  )
}