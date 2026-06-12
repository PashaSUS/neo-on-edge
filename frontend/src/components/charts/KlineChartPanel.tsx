import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CandlestickSeries, createChart } from 'lightweight-charts'
import type { CandlestickData, IChartApi, ISeriesApi } from 'lightweight-charts'
import { api } from '../../api/neoEdgeApi'
import type { KlineBar } from '../../api/neoEdgeApi'
import { useSignalR } from '../../hooks/useSignalR'
import { getErrorMessage } from '../../lib/toastStore'
import { cn } from '../../lib/utils'
import { Button, LoadingMark } from '../ui'

type ChartSource = 'neo' | 'alcor'

const DURATIONS = ['1m', '5m', '15m', '1h', '4h', '1d', '1w']
const ALCOR_RESOLUTION: Record<string, number> = {
  '1m': 1,
  '5m': 5,
  '15m': 15,
  '1h': 60,
  '4h': 240,
  '1d': 1440,
  '1w': 10080,
}

export function KlineChartPanel({
  neo,
  tokenA = '',
  tokenB = '',
  defaultSource = 'neo',
  initialDuration = '1h',
  height = 420,
  compact = false,
  className,
}: {
  neo?: { src: string; pairId: string }
  tokenA?: string
  tokenB?: string
  defaultSource?: ChartSource
  initialDuration?: string
  height?: number
  compact?: boolean
  className?: string
}) {
  const chartRef = useRef<HTMLDivElement>(null)
  const chartApiRef = useRef<IChartApi | null>(null)
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const [duration, setDuration] = useState(initialDuration)
  const [source, setSource] = useState<ChartSource>(defaultSource)
  const canUseAlcor = tokenA.length > 0 && tokenB.length > 0
  const canUseNeo = Boolean(neo?.src && neo?.pairId)
  const queryEnabled = source === 'alcor' ? canUseAlcor : canUseNeo

  useEffect(() => {
    if (source === 'alcor' && !canUseAlcor) setSource(canUseNeo ? 'neo' : defaultSource)
    if (source === 'neo' && !canUseNeo) setSource(canUseAlcor ? 'alcor' : defaultSource)
  }, [canUseAlcor, canUseNeo, defaultSource, source])

  useEffect(() => {
    if (!chartRef.current) return

    const node = chartRef.current
    const chart = createChart(node, {
      width: node.clientWidth,
      height,
      layout: { background: { color: '#09090b' }, textColor: '#d4d4d8' },
      grid: { vertLines: { color: '#27272a' }, horzLines: { color: '#27272a' } },
      rightPriceScale: { borderColor: '#3f3f46' },
      timeScale: { borderColor: '#3f3f46' },
    })

    chartApiRef.current = chart
    seriesRef.current = chart.addSeries(CandlestickSeries, {
      upColor: '#2dd4bf',
      wickUpColor: '#2dd4bf',
      borderUpColor: '#2dd4bf',
      downColor: '#fb7185',
      wickDownColor: '#fb7185',
      borderDownColor: '#fb7185',
    })

    const resize = () => chart.applyOptions({ width: node.clientWidth, height })
    const observer = new ResizeObserver(resize)
    observer.observe(node)

    return () => {
      observer.disconnect()
      chart.remove()
      chartApiRef.current = null
      seriesRef.current = null
    }
  }, [height])

  const { data: bars, isLoading, isError, error, isFetching, refetch } = useQuery<KlineBar[]>({
    queryKey: ['klines', source, neo?.src, neo?.pairId, duration, tokenA, tokenB],
    queryFn: ({ signal }) => {
      if (source === 'alcor') {
        const resolution = ALCOR_RESOLUTION[duration] ?? 240
        const to = Date.now()
        const from = to - resolution * 60_000 * 500
        return api.alcorKlines(tokenA, tokenB, resolution, from, to, 500, signal)
      }

      return api.klines(neo?.src ?? '', neo?.pairId ?? '', duration, 500, undefined, signal)
    },
    enabled: queryEnabled,
    meta: { errorTitle: 'Klines failed to load', toast: false },
  })

  useEffect(() => {
    if (!seriesRef.current) return

    const mapped: CandlestickData[] = (bars ?? [])
      .slice()
      .sort((a, b) => a.bucketTs - b.bucketTs)
      .map(bar => ({
        time: Math.floor(bar.bucketTs / 1000) as CandlestickData['time'],
        open: Number(bar.open),
        high: Number(bar.high),
        low: Number(bar.low),
        close: Number(bar.close),
      }))

    seriesRef.current.setData(mapped)
    if (mapped.length > 0) chartApiRef.current?.timeScale().fitContent()
  }, [bars])

  useSignalR(
    'SubscribeKlines',
    'UnsubscribeKlines',
    'kline',
    [neo?.src ?? '', neo?.pairId ?? '', duration],
    (raw: KlineBar) => {
      if (source !== 'neo') return
      seriesRef.current?.update({
        time: Math.floor(raw.bucketTs / 1000) as CandlestickData['time'],
        open: Number(raw.open),
        high: Number(raw.high),
        low: Number(raw.low),
        close: Number(raw.close),
      })
    },
    source === 'neo' && canUseNeo,
  )

  return (
    <div className={cn('grid gap-4', className)}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex w-max max-w-full gap-1 overflow-x-auto rounded-lg border border-zinc-800 bg-zinc-900 p-1">
          {DURATIONS.map(item => (
            <button
              key={item}
              className={cn('h-8 min-w-11 rounded-md px-3 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white', duration === item && 'bg-zinc-800 text-white')}
              type="button"
              onClick={() => setDuration(item)}
            >
              {item}
            </button>
          ))}
        </div>

        <div className="flex items-center gap-2">
          <div className="flex w-max max-w-full gap-1 overflow-x-auto rounded-lg border border-zinc-800 bg-zinc-900 p-1">
            <button
              className={cn('h-8 rounded-md px-3 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white disabled:cursor-not-allowed disabled:opacity-40', source === 'neo' && 'bg-zinc-800 text-white')}
              type="button"
              onClick={() => setSource('neo')}
              disabled={!canUseNeo}
            >
              NeoOnEdge
            </button>
            <button
              className={cn('h-8 rounded-md px-3 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white disabled:cursor-not-allowed disabled:opacity-40', source === 'alcor' && 'bg-zinc-800 text-white')}
              type="button"
              onClick={() => setSource('alcor')}
              disabled={!canUseAlcor}
            >
              Alcor
            </button>
          </div>
          <Button variant="secondary" size="sm" type="button" onClick={() => refetch()} disabled={!queryEnabled || isFetching}>
            {isFetching ? 'Refreshing...' : 'Refresh'}
          </Button>
        </div>
      </div>

      {isLoading && (
        <div className="flex items-center gap-3 rounded-lg border border-zinc-800 bg-zinc-900/70 p-4 text-sm text-zinc-300">
          <LoadingMark className="h-6 w-6" label="Loading candles" />
          <span>Loading candles</span>
        </div>
      )}
      {isError && (
        <div className="rounded-lg border border-red-500/40 bg-red-950/20 p-4 text-sm text-red-100">
          {getErrorMessage(error)}
        </div>
      )}
      {!isLoading && !isError && queryEnabled && bars?.length === 0 && (
        <div className="rounded-lg border border-zinc-800 bg-zinc-900/70 p-4 text-sm text-zinc-400">
          No candles found for this pair yet.
        </div>
      )}
      {!queryEnabled && (
        <div className="rounded-lg border border-zinc-800 bg-zinc-900/70 p-4 text-sm text-zinc-400">
          Select a quoted route or token pair to load candles.
        </div>
      )}

      <div ref={chartRef} className={cn('overflow-hidden rounded-lg border border-zinc-800 bg-[#09090b]', compact ? 'min-h-72' : 'min-h-96')} />
    </div>
  )
}