import { useEffect, useState } from 'react'
import { dismissToast, getToasts, subscribeToasts, Toast } from '../lib/toastStore'
import { cn } from '../lib/utils'

const labels: Record<Toast['type'], string> = {
  success: 'Success',
  error: 'Error',
  info: 'Info',
}

export function ToastViewport() {
  const [items, setItems] = useState<Toast[]>(getToasts())

  useEffect(() => subscribeToasts(() => setItems(getToasts())), [])

  return (
    <div className="fixed bottom-4 left-4 right-4 z-50 grid gap-3 sm:left-auto sm:w-96" aria-live="polite" aria-relevant="additions removals">
      {items.map(toast => (
        <div
          key={toast.id}
          className={cn(
            'grid grid-cols-[auto_1fr_auto] items-start gap-3 rounded-lg border border-zinc-800 bg-zinc-950/95 p-4 shadow-2xl',
            toast.type === 'error' && 'border-red-500/45',
            toast.type === 'success' && 'border-teal-500/45',
          )}
          role={toast.type === 'error' ? 'alert' : 'status'}
        >
          <div className="rounded-md bg-zinc-800 px-2 py-1 text-[0.68rem] font-black uppercase text-zinc-200">{labels[toast.type]}</div>
          <div>
            <strong className="block text-sm text-white">{toast.title}</strong>
            {toast.message && <p className="mt-1 text-sm text-zinc-300">{toast.message}</p>}
          </div>
          <button className="grid h-7 w-7 place-items-center rounded-md text-zinc-400 hover:bg-zinc-800 hover:text-white" type="button" onClick={() => dismissToast(toast.id)} aria-label="Dismiss notification">
            x
          </button>
        </div>
      ))}
    </div>
  )
}
