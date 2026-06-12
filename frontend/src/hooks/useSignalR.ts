import { useEffect, useRef } from 'react'
import { ensureConnected } from '../lib/signalr'

/**
 * Subscribes to a SignalR group and calls onMessage for each event.
 * Subscribes on mount, unsubscribes on unmount.
 */
export function useSignalR<T>(
  subscribeMethod: string, unsubscribeMethod: string,
  event: string, args: string[],
  onMessage: (data: T) => void,
  enabled = true,
) {
  const cbRef = useRef(onMessage)
  cbRef.current = onMessage

  useEffect(() => {
    if (!enabled) return

    let hub: Awaited<ReturnType<typeof ensureConnected>> | null = null
    let mounted = true

    ensureConnected().then(h => {
      if (!mounted) return
      hub = h
      hub.on(event, (raw: string) => {
        try { cbRef.current(JSON.parse(raw) as T) } catch { /* ignore */ }
      })
      hub.invoke(subscribeMethod, ...args).catch(console.warn)
    })

    return () => {
      mounted = false
      if (hub) {
        hub.invoke(unsubscribeMethod, ...args).catch(() => {})
        hub.off(event)
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subscribeMethod, unsubscribeMethod, event, enabled, ...args])
}
