import { cn } from '../../lib/utils'

export type RoutePreference = 'best' | 'alcor' | 'neo' | 'split'

const options: Array<{ value: RoutePreference; label: string; standby: string }> = [
  { value: 'best', label: 'Best price', standby: 'Auto' },
  { value: 'alcor', label: 'Alcor', standby: 'Router' },
  { value: 'neo', label: 'NeoOnEdge', standby: 'Support' },
  { value: 'split', label: 'Neo split', standby: 'Multi' },
]

export function RoutePreferenceControl({ value, onChange, hasAmount, loading, alcorAvailable, neoAvailable, splitAvailable }: {
  value: RoutePreference
  onChange: (value: RoutePreference) => void
  hasAmount: boolean
  loading: boolean
  alcorAvailable: boolean
  neoAvailable: boolean
  splitAvailable: boolean
}) {
  const status = (option: RoutePreference) => {
    if (option === 'best') return loading && hasAmount ? 'Checking' : 'Auto'
    if (!hasAmount) return options.find(item => item.value === option)?.standby ?? 'Ready'
    if (loading) return 'Checking'
    if (option === 'alcor') return alcorAvailable ? 'Ready' : 'No route'
    if (option === 'neo') return neoAvailable ? 'Ready' : 'No route'
    return splitAvailable ? 'Ready' : 'No route'
  }

  return (
    <div role="radiogroup" aria-label="Route preference" className="grid gap-2 sm:grid-cols-4">
      {options.map(option => {
        const selected = value === option.value
        const routeAvailable = option.value === 'alcor'
          ? alcorAvailable
          : option.value === 'neo'
            ? neoAvailable
            : option.value === 'split'
              ? splitAvailable
              : true
        const unavailable = hasAmount && !loading && option.value !== 'best' && !routeAvailable

        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={selected}
            className={cn(
              'rounded-lg border border-zinc-800 bg-zinc-900/70 px-3 py-2 text-left transition hover:border-teal-500 focus:border-teal-500 focus:outline-none focus:ring-2 focus:ring-teal-500/20',
              selected && 'border-cyan-300/70 bg-cyan-500/10 text-cyan-50',
              unavailable && !selected && 'text-zinc-500',
            )}
            onClick={() => onChange(option.value)}
          >
            <span className="block text-sm font-bold text-zinc-50">{option.label}</span>
            <span className={cn('mt-0.5 block text-xs text-zinc-500', selected && 'text-cyan-200/80', unavailable && 'text-amber-300/70')}>
              {status(option.value)}
            </span>
          </button>
        )
      })}
    </div>
  )
}