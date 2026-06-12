import { useEffect, useMemo, useRef, useState } from 'react'
import { Input } from '../ui'
import { cn } from '../../lib/utils'
import type { Token } from './types'

export function TokenSelect({ label, value, tokens, onChange }: {
  label: string
  value: Token
  tokens: Token[]
  onChange: (token: Token) => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const onPointerDown = (event: PointerEvent) => {
      if (rootRef.current && event.target instanceof Node && !rootRef.current.contains(event.target)) {
        setOpen(false)
      }
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }

    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open])

  useEffect(() => {
    if (!open) setSearch('')
  }, [open])

  const filteredTokens = useMemo(() => {
    const needle = search.trim().toLowerCase()
    const list = needle
      ? tokens.filter(token =>
          token.symbol.toLowerCase().includes(needle) ||
          token.contract.toLowerCase().includes(needle) ||
          token.id.toLowerCase().includes(needle))
      : tokens

    return list.slice(0, 80)
  }, [search, tokens])

  const choose = (token: Token) => {
    onChange(token)
    setOpen(false)
  }

  return (
    <div ref={rootRef} className="relative w-56 max-w-[62vw]">
      <button
        type="button"
        aria-label={label}
        aria-expanded={open}
        className="flex h-11 w-full items-center justify-between gap-3 rounded-lg border border-zinc-800 bg-zinc-900 px-3 text-left outline-none transition hover:border-teal-500 focus:border-teal-500 focus:ring-2 focus:ring-teal-500/20"
        onClick={() => setOpen(current => !current)}
      >
        <span className="min-w-0">
          <span className="block truncate text-sm font-bold text-zinc-50">{value.symbol}</span>
          <span className="block truncate text-xs text-zinc-500">{value.contract}</span>
        </span>
        <span className="shrink-0 text-xs font-bold text-zinc-500" aria-hidden="true">v</span>
      </button>

      {open && (
        <div className="absolute right-0 z-40 mt-2 w-80 max-w-[calc(100vw-2rem)] rounded-lg border border-zinc-800 bg-zinc-950 p-2 shadow-2xl shadow-black/50">
          <Input
            autoFocus
            value={search}
            onChange={event => setSearch(event.target.value)}
            placeholder="Search symbol or contract"
            className="h-9"
          />
          <div className="mt-2 max-h-72 overflow-y-auto pr-1">
            {filteredTokens.map(token => (
              <button
                key={token.id}
                type="button"
                aria-selected={token.id === value.id}
                className={cn(
                  'flex w-full items-center justify-between gap-3 rounded-md px-3 py-2 text-left transition hover:bg-zinc-900',
                  token.id === value.id && 'bg-teal-500/10 text-teal-100',
                )}
                onClick={() => choose(token)}
              >
                <span className="min-w-0">
                  <span className="block truncate text-sm font-bold text-zinc-50">{token.symbol}</span>
                  <span className="block truncate text-xs text-zinc-500">{token.contract}</span>
                </span>
                <span className="shrink-0 rounded-full bg-zinc-900 px-2 py-1 text-[11px] font-bold uppercase tracking-normal text-zinc-400">
                  {token.source ?? 'token'}
                </span>
              </button>
            ))}
            {filteredTokens.length === 0 && (
              <div className="px-3 py-6 text-center text-sm text-zinc-500">No token matches</div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}