import { getErrorMessage } from '../lib/toastStore'
import { Button, Card, LoadingMark } from './ui'

export function LoadingPanel({ title = 'Loading market data' }: { title?: string }) {
  return (
    <Card className="overflow-hidden p-5">
      <div className="flex items-center gap-4">
        <LoadingMark className="h-9 w-9 shrink-0" label={title} />
        <div>
          <strong className="text-zinc-50">{title}</strong>
          <p className="mt-1 text-sm text-zinc-400">Syncing indexed WAX activity.</p>
        </div>
      </div>
      <div className="mt-4 grid grid-cols-3 gap-1.5" aria-hidden="true">
        <span className="h-1 rounded-full bg-teal-300/70 animate-pulse" />
        <span className="h-1 rounded-full bg-cyan-300/50 animate-pulse [animation-delay:120ms]" />
        <span className="h-1 rounded-full bg-emerald-300/40 animate-pulse [animation-delay:240ms]" />
      </div>
    </Card>
  )
}

export function ErrorPanel({ error, onRetry, retrying }: { error: unknown; onRetry?: () => void; retrying?: boolean }) {
  return (
    <Card className="flex items-center justify-between gap-4 border-red-500/40 bg-red-950/20 p-5 max-sm:flex-col max-sm:items-stretch">
      <div>
        <strong className="text-red-100">Could not load this view</strong>
        <p className="mt-1 text-sm text-red-200/80">{getErrorMessage(error)}</p>
      </div>
      {onRetry && (
        <Button variant="secondary" type="button" onClick={onRetry} disabled={retrying}>
          {retrying ? 'Retrying...' : 'Retry'}
        </Button>
      )}
    </Card>
  )
}

export function EmptyState({ title, message }: { title: string; message: string }) {
  return (
    <Card className="p-6">
      <strong className="text-zinc-50">{title}</strong>
      <p className="mt-1 text-sm text-zinc-400">{message}</p>
    </Card>
  )
}
