import { cn } from '../../lib/utils'
import { useState } from 'react'

export type RouteStep = {
  id: string
  source: string
  label: string
  fromSymbol?: string
  fromContract?: string
  fromLogoUrl?: string | null
  toSymbol?: string
  toContract?: string
  toLogoUrl?: string | null
}

const SOURCE_LABELS: Record<string, string> = {
  a2: 'Alcor v2',
  ad: 'Alcor order book',
  d: 'Defibox',
  t: 'Taco',
  a: 'ADEX',
  n: 'NeftyBlocks',
  wf: 'WaxFusion',
}

export function sourceLabel(source: string) {
  return SOURCE_LABELS[source] ?? source.toUpperCase()
}

export function parseRouteSteps(route: string | undefined): RouteStep[] {
  if (!route) return []

  return route
    .split('-')
    .map(part => part.trim())
    .filter(Boolean)
    .map((part, index) => {
      const [id = part, source = ''] = part.split('@')
      return {
        id,
        source,
        label: source ? `${sourceLabel(source)} pool ${id}` : `Step ${index + 1}`,
      }
    })
}

export function routeFromMemo(memo: string | undefined) {
  if (!memo) return ''
  const [, route = ''] = memo.split('|')
  return route
}

export function routeMinFromMemo(memo: string | undefined) {
  if (!memo) return ''
  const [, min = ''] = memo.split(',')
  return min
}

export function RouteSummary({
  contract,
  memo,
  output,
  minReceived,
  fee,
  feeDetail,
  priceImpact,
  steps,
  hops,
  receiver,
  className,
}: {
  contract: string
  memo: string
  output?: string
  minReceived?: string
  fee?: string
  feeDetail?: string
  priceImpact?: string
  steps: RouteStep[]
  hops: number
  receiver?: string
  className?: string
}) {
  const normalizedHops = Math.max(hops, steps.length)

  return (
    <div className={cn('grid gap-4 rounded-lg border border-zinc-800 bg-zinc-900/70 p-4', className)}>
      <div className="grid gap-3 sm:grid-cols-4">
        <SummaryMetric label="Destination" value={contract} />
        <SummaryMetric label="Hops" value={normalizedHops > 0 ? String(normalizedHops) : '-'} />
        <SummaryMetric label="Minimum received" value={minReceived || '-'} />
        <SummaryMetric label="Fees" value={fee || '-'} />
      </div>

      {(feeDetail || priceImpact) && (
        <div className="grid gap-2 rounded-lg border border-zinc-800 bg-zinc-950/60 p-3 text-xs text-zinc-400 sm:grid-cols-2">
          {feeDetail && <span>{feeDetail}</span>}
          {priceImpact && <span>Price impact: {priceImpact}</span>}
        </div>
      )}

      {output && (
        <div className="rounded-lg border border-zinc-800 bg-zinc-950/60 p-3">
          <span className="text-xs font-semibold text-zinc-400">Estimated received</span>
          <strong className="mt-1 block text-lg text-zinc-50">{output}</strong>
        </div>
      )}

      {steps.length > 0 && (
        <div className="grid gap-2">
          <span className="text-xs font-semibold text-zinc-400">Route steps</span>
          <div className="grid gap-2">
            {steps.map((step, index) => (
              <div key={`${step.id}-${step.source}-${index}`} className="route-step-card grid gap-3 rounded-lg border border-zinc-800 bg-zinc-950/60 px-3 py-3 sm:grid-cols-[1fr_auto] sm:items-center">
                <div className="min-w-0">
                  <span className="block text-xs font-semibold text-zinc-500">{index + 1}. {step.label}</span>
                  {step.fromSymbol && step.toSymbol ? (
                    <div className="mt-2 flex min-w-0 flex-wrap items-center gap-2">
                      <TokenPill symbol={step.fromSymbol} contract={step.fromContract} logoUrl={step.fromLogoUrl} />
                      <span className="route-step-arrow text-sm font-black text-teal-300" aria-hidden="true">-&gt;</span>
                      <TokenPill symbol={step.toSymbol} contract={step.toContract} logoUrl={step.toLogoUrl} />
                    </div>
                  ) : (
                    <span className="mt-1 block truncate text-sm font-semibold text-zinc-100">{step.label}</span>
                  )}
                </div>
                <code className="w-fit rounded-md bg-zinc-900 px-2 py-1 text-xs text-zinc-400">{step.source ? `${step.id}@${step.source}` : step.id}</code>
              </div>
            ))}
          </div>
        </div>
      )}

      {receiver && (
        <div className="text-xs text-zinc-500">Receiver: {receiver}</div>
      )}

      <details className="rounded-lg border border-zinc-800 bg-zinc-950/60 p-3">
        <summary className="cursor-pointer text-xs font-semibold text-zinc-400">Advanced memo</summary>
        <code className="mt-3 block break-all text-xs text-zinc-400">{memo || '-'}</code>
      </details>
    </div>
  )
}

function SummaryMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <span className="block text-xs font-semibold text-zinc-400">{label}</span>
      <strong className="mt-1 block truncate text-base text-zinc-50">{value}</strong>
    </div>
  )
}

function TokenPill({ symbol, contract, logoUrl }: { symbol: string; contract?: string; logoUrl?: string | null }) {
  const [imageFailed, setImageFailed] = useState(false)
  const initials = symbol.slice(0, 4).toUpperCase()
  const imageUrl = typeof logoUrl === 'string' && logoUrl.length > 0 && !imageFailed ? logoUrl : undefined
  const showImage = Boolean(imageUrl)

  return (
    <span className="flex min-w-0 items-center gap-2 rounded-full border border-zinc-800 bg-zinc-900/80 py-1 pl-1 pr-3">
      <span className="relative grid h-8 w-8 shrink-0 place-items-center overflow-hidden rounded-full border border-teal-400/25 bg-teal-500/15 text-[10px] font-black text-teal-100">
        {showImage && (
          <img
            src={imageUrl}
            alt=""
            className="h-full w-full object-cover"
            onError={() => setImageFailed(true)}
          />
        )}
        <span className={cn('absolute inset-0 grid place-items-center', showImage && 'opacity-0')}>{initials}</span>
      </span>
      <span className="min-w-0">
        <span className="block truncate text-sm font-black text-zinc-50">{symbol}</span>
        {contract && <span className="block max-w-28 truncate text-[11px] font-semibold text-zinc-500">{contract}</span>}
      </span>
    </span>
  )
}