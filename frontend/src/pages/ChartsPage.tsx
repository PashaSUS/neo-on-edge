import { useParams, useSearchParams } from 'react-router-dom'
import { KlineChartPanel } from '../components/charts'
import { Card, CardContent, PageHeader } from '../components/ui'

export default function ChartsPage() {
  const { src = 'd', id = '1' } = useParams<{ src: string; id: string }>()
  const [searchParams] = useSearchParams()
  const tokenA = searchParams.get('tokenA') ?? ''
  const tokenB = searchParams.get('tokenB') ?? ''

  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="Candlestick chart"
        title={`${src.toUpperCase()} - Pair ${id}`}
        description={tokenA && tokenB ? `${tokenA} / ${tokenB}` : 'NeoOnEdge candles with live SignalR updates.'}
      />

      <Card>
        <CardContent className="grid gap-4 pt-5">
          <KlineChartPanel
            neo={{ src, pairId: id }}
            tokenA={tokenA}
            tokenB={tokenB}
            defaultSource="neo"
            height={420}
          />
        </CardContent>
      </Card>
    </div>
  )
}
