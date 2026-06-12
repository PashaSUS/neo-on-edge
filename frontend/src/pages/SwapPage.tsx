import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/neoEdgeApi'
import type { AlcorRoute, SplitRouteLeg, SwapRoute, TokenBalance } from '../api/neoEdgeApi'
import { KlineChartPanel } from '../components/charts'
import { BalanceText, parseRouteSteps, QuoteCard, routeFromMemo, RoutePreferenceControl, RouteSummary, sourceLabel, TokenSelect } from '../components/swap'
import type { RoutePreference, Token } from '../components/swap'
import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Eyebrow, Input, Label, PageHeader, Select } from '../components/ui'
import { useSignalR } from '../hooks/useSignalR'
import { useWallet } from '../lib/walletContext'
import { getErrorMessage, pushToast } from '../lib/toastStore'
import { cn } from '../lib/utils'

const WAX_TOKEN: Token = { contract: 'eosio.token', symbol: 'WAX', precision: 8, id: 'wax-eosio.token', popularityScore: 10_000, isTrusted: true, logoUrl: 'https://assets.coingecko.com/coins/images/1372/small/WAX_Coin_Tickers_P_512px.png' }
const USDT_TOKEN: Token = { contract: 'usdt.alcor', symbol: 'USDT', precision: 6, id: 'usdt-usdt.alcor', popularityScore: 9_500, isTrusted: true, logoUrl: 'https://assets.coingecko.com/coins/images/325/small/Tether.png' }
const FGL_TOKEN: Token = { contract: 'fgltokenmgmt', symbol: 'FGL', precision: 4, id: 'fgl-fgltokenmgmt', popularityScore: 8_500, isTrusted: true }

const KNOWN_TOKENS = [WAX_TOKEN, USDT_TOKEN, FGL_TOKEN]
const DEFAULT_CONTRACT_ACCOUNT = import.meta.env.VITE_CONTRACT_ACCOUNT || 'swap.neo'
const TOKEN_REFRESH_MIN_INTERVAL_MS = 5 * 60_000
const MAX_REASONABLE_NEO_ALCOR_MULTIPLIER = 2
const MAX_REASONABLE_SPLIT_NEO_MULTIPLIER = 1.75
const ROUTE_LABELS: Record<RoutePreference, string> = {
  best: 'Best price',
  alcor: 'Alcor router',
  neo: 'NeoOnEdge',
  split: 'Neo split',
}

type RouteSource = Exclude<RoutePreference, 'best'>

type ExecutableRoute = {
  source: RouteSource
  label: string
  contract: string
  memo: string
  receiver: string
  output: string
  minReceived: string
  legs?: SplitRouteLeg[]
}

type DisplayToken = {
  symbol: string
  contract?: string
  logoUrl?: string | null
}

function toRawAmount(value: string, precision: number): number {
  const parsed = Number(value)
  if (!Number.isFinite(parsed) || parsed <= 0) return 0
  return Math.round(parsed * 10 ** precision)
}

function formatTokenAmount(value: string, symbol: string) {
  const amount = value.trim()
  return amount ? `${amount} ${symbol}` : ''
}

function routeForSource(quote: SwapRoute | undefined, source: RouteSource, receiver: string): ExecutableRoute | null {
  if (!quote) return null

  if (source === 'alcor' && quote.alcor) {
    return {
      source,
      label: ROUTE_LABELS.alcor,
      contract: 'swap.alcor',
      memo: quote.alcor.memo,
      receiver: quote.alcor.receiver || receiver,
      output: quote.alcor.output,
      minReceived: quote.alcor.minReceived,
    }
  }

  if (source === 'neo' && quote.neo) {
    return {
      source,
      label: ROUTE_LABELS.neo,
      contract: quote.neo.contractAccount || DEFAULT_CONTRACT_ACCOUNT,
      memo: quote.neo.memo,
      receiver: quote.neo.receiver || receiver,
      output: quote.neo.output,
      minReceived: quote.neo.minReceived,
    }
  }

  if (source === 'split' && quote.split) {
    return {
      source,
      label: ROUTE_LABELS.split,
      contract: 'Multiple routes',
      memo: 'Multi-action split route',
      receiver: quote.split.receiver || receiver,
      output: quote.split.output,
      minReceived: quote.split.minReceived,
      legs: quote.split.legs,
    }
  }

  return null
}

function routeChoice(quote: SwapRoute | undefined, preference: RoutePreference, receiver: string): ExecutableRoute | null {
  if (!quote) return null

  if (preference === 'best') {
    return quote.best === 'alcor' || quote.best === 'neo' || quote.best === 'split'
      ? routeForSource(quote, quote.best, receiver)
      : null
  }

  return routeForSource(quote, preference, receiver)
}

function displayAmount(value: string | undefined) {
  return value?.trim().split(/\s+/)[0] ?? ''
}

function routeOutputNumber(value: string | undefined) {
  const amount = displayAmount(value)
  const parsed = Number(amount)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0
}

function bestRouteSource(quote: Pick<SwapRoute, 'alcor' | 'neo' | 'split'>): SwapRoute['best'] {
  const outputs: Array<{ source: SwapRoute['best']; output: number }> = [
    { source: 'alcor', output: routeOutputNumber(quote.alcor?.output) },
    { source: 'neo', output: routeOutputNumber(quote.neo?.output) },
    { source: 'split', output: routeOutputNumber(quote.split?.output) },
  ]
  const best = outputs.sort((a, b) => b.output - a.output)[0]
  return best && best.output > 0 ? best.source : 'none'
}

function combineRouteQuotes(neoQuote: SwapRoute | undefined, alcorRoute: AlcorRoute | undefined): SwapRoute | undefined {
  if (!neoQuote && !alcorRoute) return undefined

  let neo = neoQuote?.neo ?? null
  let split = neoQuote?.split ?? null
  let neoReason = neoQuote?.neoReason ?? null
  let splitReason = neoQuote?.splitReason ?? null
  const alcor = alcorRoute ?? null
  const alcorOutput = routeOutputNumber(alcor?.output)
  const neoOutput = routeOutputNumber(neo?.output)
  const splitOutput = routeOutputNumber(split?.output)

  if (alcorOutput > 0) {
    if (neoOutput > alcorOutput * MAX_REASONABLE_NEO_ALCOR_MULTIPLIER) {
      neo = null
      neoReason = 'Hidden by safety guardrail versus Alcor.'
    }
    if (splitOutput > alcorOutput * MAX_REASONABLE_NEO_ALCOR_MULTIPLIER) {
      split = null
      splitReason = 'Hidden by safety guardrail versus Alcor.'
    }
  }

  const filteredNeoOutput = routeOutputNumber(neo?.output)
  if (split && filteredNeoOutput > 0 && routeOutputNumber(split.output) > filteredNeoOutput * MAX_REASONABLE_SPLIT_NEO_MULTIPLIER) {
    split = null
    splitReason = 'Hidden by safety guardrail versus direct NeoOnEdge.'
  }

  const combined = { alcor, neo, split }
  return { ...combined, neoReason, splitReason, best: bestRouteSource(combined) }
}

function formatFeePercent(bps: number | null | undefined) {
  const value = Number(bps ?? 0)
  if (!Number.isFinite(value) || value <= 0) return '0.00%'

  return `${(value / 100).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}%`
}

function normalizeAlcorFeeBps(fee: number | undefined) {
  const value = Number(fee ?? 0)
  if (!Number.isFinite(value) || value <= 0) return 0

  const percent = value > 100 ? value / 10_000 : value / 100
  return Math.round(percent * 100)
}

function estimateAlcorFeeBps(route: SwapRoute['alcor']) {
  if (!route) return 0
  if (typeof route.totalFeeBps === 'number') return route.totalFeeBps

  const pools = route.pools ?? {}
  const poolIds = route.route.length > 0 ? route.route.map(String) : Object.keys(pools)
  return poolIds.reduce((sum, poolId) => sum + normalizeAlcorFeeBps(pools[poolId]?.fee), 0)
}

function alcorFeeSummary(route: SwapRoute['alcor']) {
  if (!route) return null

  const totalBps = estimateAlcorFeeBps(route)
  return {
    label: `~${formatFeePercent(totalBps)}`,
    detail: `LP fees ${formatFeePercent(totalBps)}`,
  }
}

function neoFeeSummary(route: SwapRoute['neo'], outputSymbol: string) {
  if (!route) return null

  const lpBps = route.liquidityProviderFeeBps ?? 0
  const protocolBps = route.protocolFeeBps ?? 0
  const totalBps = route.totalFeeBps ?? lpBps + protocolBps
  const protocolAmount = displayAmount(route.estimatedProtocolFee)
  const protocolAmountText = Number(protocolAmount) > 0 ? ` - Neo fee ${protocolAmount} ${outputSymbol}` : ''

  return {
    label: `~${formatFeePercent(totalBps)}`,
    detail: `LP ${formatFeePercent(lpBps)} + Neo ${formatFeePercent(protocolBps)}${protocolAmountText}`,
  }
}

function splitFeeSummary(route: SwapRoute['split']) {
  if (!route) return null

  const totalBps = route.totalFeeBps ?? 0
  const detail = route.legs
    .map(leg => `${leg.label} ${formatFeePercent(leg.totalFeeBps ?? 0)}`)
    .join(' + ')

  return {
    label: `~${formatFeePercent(totalBps)}`,
    detail: detail || 'Weighted route fees',
  }
}

function trimInputAmount(value: string) {
  if (!value.includes('.')) return value
  return value.replace(/(\.\d*?[1-9])0+$/, '$1').replace(/\.0+$/, '')
}

function balanceInputAmount(balance: TokenBalance | undefined, token: Token) {
  if (!balance || balance.error) return ''

  const assetAmount = balance.asset?.trim().split(/\s+/)[0]
  if (assetAmount && Number(assetAmount) > 0) return trimInputAmount(assetAmount)
  if (Number(balance.amount) > 0) return trimInputAmount(balance.amount.toFixed(token.precision))
  return ''
}

function formatRawAmount(rawAmount: number, precision: number) {
  const scale = 10 ** precision
  const whole = Math.trunc(rawAmount / scale)
  const fraction = Math.trunc(rawAmount % scale).toString().padStart(precision, '0')
  return precision > 0 ? `${whole}.${fraction}` : String(whole)
}

function tokenSymbolFromId(id: string) {
  const assetSymbol = assetSymbolFromString(id)
  if (assetSymbol) return assetSymbol

  const dash = id.indexOf('-')
  return (dash > 0 ? id.slice(0, dash) : id).toUpperCase()
}

function tokenContractFromId(id: string) {
  const dash = id.indexOf('-')
  return dash > 0 ? id.slice(dash + 1) : undefined
}

function assetSymbolFromString(value: string) {
  const parts = value.trim().split(/\s+/)
  if (parts.length < 2) return ''

  const amount = Number(parts[0])
  return Number.isFinite(amount) ? parts[parts.length - 1]?.toUpperCase() ?? '' : ''
}

function tokenDisplayFromId(tokens: Token[], tokenId: string | undefined): DisplayToken {
  const assetSymbol = tokenId ? assetSymbolFromString(tokenId) : ''
  if (assetSymbol) return tokenDisplayFromSymbol(tokens, assetSymbol)

  const known = findToken(tokens, tokenId)
  if (known) return { symbol: known.symbol, contract: known.contract, logoUrl: known.logoUrl }

  const fallbackId = tokenId ?? ''
  return {
    symbol: tokenSymbolFromId(fallbackId),
    contract: tokenContractFromId(fallbackId),
  }
}

function tokenDisplayFromSymbol(tokens: Token[], symbol: string | undefined): DisplayToken {
  const normalized = (symbol ?? '').toUpperCase()
  const known = tokens.find(token => token.symbol.toUpperCase() === normalized)
  return {
    symbol: known?.symbol ?? normalized,
    contract: known?.contract,
    logoUrl: known?.logoUrl,
  }
}

function alcorRouteSteps(quote: SwapRoute | undefined, tokens: Token[]) {
  const swaps = quote?.alcor?.swaps ?? []
  if (swaps.length > 0) {
    return swaps.map((swap, index) => {
      const from = tokenDisplayFromId(tokens, swap.input)
      const to = tokenDisplayFromId(tokens, swap.output)
      const poolIds = swap.route.length > 0 ? swap.route.join(', ') : quote?.alcor?.route?.[index]

      return {
        id: String(poolIds || index + 1),
        source: 'a2',
        label: `${sourceLabel('a2')} ${swap.percent}%`,
        fromSymbol: from.symbol,
        fromContract: from.contract,
        fromLogoUrl: from.logoUrl,
        toSymbol: to.symbol,
        toContract: to.contract,
        toLogoUrl: to.logoUrl,
      }
    })
  }

  const routeIds = quote?.alcor?.route?.length
    ? quote.alcor.route
    : []

  return routeIds.map(id => ({
    id: String(id),
    source: 'a2',
    label: `${sourceLabel('a2')} pool ${id}`,
  }))
}

function neoRouteSteps(quote: SwapRoute | undefined, tokens: Token[], fromToken: Token, toToken: Token) {
  const steps = quote?.neo?.steps ?? []
  if (steps.length > 0) {
    return steps.map(step => {
      const from = tokenDisplayFromSymbol(tokens, step.fromSymbol)
      const to = tokenDisplayFromSymbol(tokens, step.toSymbol)

      return {
        id: step.pairId,
        source: step.src,
        label: `${sourceLabel(step.src)} pool ${step.pairId}`,
        fromSymbol: from.symbol,
        fromContract: from.contract,
        fromLogoUrl: from.logoUrl,
        toSymbol: to.symbol,
        toContract: to.contract,
        toLogoUrl: to.logoUrl,
      }
    })
  }

  const legacySteps = parseRouteSteps(routeFromMemo(quote?.neo?.memo))
  if (legacySteps.length === 1) {
    return legacySteps.map(step => ({
      ...step,
      fromSymbol: fromToken.symbol,
      fromContract: fromToken.contract,
      fromLogoUrl: fromToken.logoUrl,
      toSymbol: toToken.symbol,
      toContract: toToken.contract,
      toLogoUrl: toToken.logoUrl,
    }))
  }

  return legacySteps
}

function splitRouteSteps(quote: SwapRoute | undefined, tokens: Token[]) {
  return quote?.split?.legs.flatMap((leg, legIndex) => {
    const steps = leg.steps.length > 0
      ? leg.steps
      : [{ src: 'split', pairId: String(legIndex + 1), fromSymbol: '', toSymbol: '' }]

    return steps.map((step, stepIndex) => {
      const from = tokenDisplayFromSymbol(tokens, step.fromSymbol || tokenSymbolFromId(leg.inputAmount))
      const to = tokenDisplayFromSymbol(tokens, step.toSymbol || tokenSymbolFromId(leg.output))

      return {
        id: `${legIndex + 1}.${stepIndex + 1}-${step.pairId}`,
        source: step.src,
        label: `${leg.label} leg ${legIndex + 1}`,
        fromSymbol: from.symbol,
        fromContract: from.contract,
        fromLogoUrl: from.logoUrl,
        toSymbol: to.symbol,
        toContract: to.contract,
        toLogoUrl: to.logoUrl,
      }
    })
  }) ?? []
}

function routeHopCount(quote: SwapRoute | undefined, source: RouteSource, steps: unknown[]) {
  if (source === 'split') {
    return quote?.split?.legs.reduce((sum, leg) => sum + Math.max(1, leg.hops), 0) ?? Math.max(1, steps.length)
  }

  if (source === 'alcor') {
    return Math.max(quote?.alcor?.route?.length ?? 0, quote?.alcor?.swaps?.length ?? 0, steps.length)
  }

  return Math.max(1, steps.length)
}

function findToken(tokens: Token[], id: string | undefined) {
  if (!id) return undefined
  return tokens.find(token => token.id === id) ?? KNOWN_TOKENS.find(token => token.id === id)
}

function compareTokens(a: Token, b: Token) {
  if (a.id === WAX_TOKEN.id) return -1
  if (b.id === WAX_TOKEN.id) return 1

  const popularity = (b.popularityScore ?? 0) - (a.popularityScore ?? 0)
  if (popularity !== 0) return popularity

  if (!!a.isTrusted !== !!b.isTrusted) return a.isTrusted ? -1 : 1
  return a.symbol.localeCompare(b.symbol) || a.contract.localeCompare(b.contract)
}

export default function SwapPage() {
  const queryClient = useQueryClient()
  const lastTokenRefreshAt = useRef(Date.now())
  const navigate = useNavigate()
  const { fromId, toId } = useParams<{ fromId?: string; toId?: string }>()
  const { actor, session, restoring, login } = useWallet()
  const [fromToken, setFromToken] = useState(() => findToken(KNOWN_TOKENS, fromId) ?? WAX_TOKEN)
  const [toToken, setToToken] = useState(() => findToken(KNOWN_TOKENS, toId) ?? USDT_TOKEN)
  const [amount, setAmount] = useState('')
  const [minReturn, setMinReturn] = useState('')
  const [route, setRoute] = useState('')
  const [slippage, setSlippage] = useState(1)
  const [maxHops, setMaxHops] = useState(3)
  const [showChart, setShowChart] = useState(false)
  const [routePreference, setRoutePreferenceState] = useState<RoutePreference>('best')

  const setRoutePreference = useCallback((preference: RoutePreference) => {
    setRoutePreferenceState(preference)
  }, [])

  const rawAmount = useMemo(() => toRawAmount(amount, fromToken.precision), [amount, fromToken.precision])
  const manualMinReturnRaw = useMemo(() => toRawAmount(minReturn, toToken.precision), [minReturn, toToken.precision])
  const quoteReceiver = actor || DEFAULT_CONTRACT_ACCOUNT
  const manualMemo = `swap,${manualMinReturnRaw},${toToken.symbol}@${toToken.contract}|${route}`

  const tokenQuery = useQuery({
    queryKey: ['tokens'],
    queryFn: () => api.tokens(undefined, 5000),
    staleTime: 5 * 60_000,
    placeholderData: keepPreviousData,
    meta: { errorTitle: 'Token list failed' },
  })

  useSignalR<Partial<Token>>('SubscribeTokens', 'UnsubscribeTokens', 'tokens', [], token => {
    if (!token.contract || !token.symbol) return
    const now = Date.now()
    if (now - lastTokenRefreshAt.current < TOKEN_REFRESH_MIN_INTERVAL_MS) return

    lastTokenRefreshAt.current = now
    queryClient.invalidateQueries({ queryKey: ['tokens'] })
  })

  const tokenOptions = useMemo(() => {
    const merged = new Map<string, Token>()
    for (const token of KNOWN_TOKENS) merged.set(token.id, token)
    for (const token of tokenQuery.data ?? []) {
      if (token.contract && token.symbol) merged.set(token.id, token)
    }
    return [...merged.values()].sort(compareTokens)
  }, [tokenQuery.data])

  useEffect(() => {
    const nextFrom = findToken(tokenOptions, fromId)
    const nextTo = findToken(tokenOptions, toId)

    setFromToken(current => nextFrom && nextFrom.id !== current.id ? nextFrom : current)
    setToToken(current => nextTo && nextTo.id !== current.id ? nextTo : current)
  }, [fromId, toId, tokenOptions])

  const writeTokenRoute = useCallback((nextFrom: Token, nextTo: Token) => {
    navigate(`/swap/${encodeURIComponent(nextFrom.id)}/${encodeURIComponent(nextTo.id)}`, { replace: true })
  }, [navigate])

  const chooseFromToken = useCallback((token: Token) => {
    setFromToken(token)
    writeTokenRoute(token, toToken)
  }, [toToken, writeTokenRoute])

  const chooseToToken = useCallback((token: Token) => {
    setToToken(token)
    writeTokenRoute(fromToken, token)
  }, [fromToken, writeTokenRoute])

  const flipTokens = useCallback(() => {
    setFromToken(toToken)
    setToToken(fromToken)
    setMinReturn('')
    setRoute('')
    writeTokenRoute(toToken, fromToken)
  }, [fromToken, toToken, writeTokenRoute])

  const balanceTokenIds = useMemo(() => [...new Set([fromToken.id, toToken.id])], [fromToken.id, toToken.id])

  const balancesQuery = useQuery<TokenBalance[]>({
    queryKey: ['balances', actor, balanceTokenIds],
    queryFn: () => api.balances(actor, balanceTokenIds),
    enabled: Boolean(actor) && balanceTokenIds.length > 0,
    refetchInterval: actor ? 20_000 : false,
    meta: { errorTitle: 'Balances failed to load', toast: false },
  })

  const balanceByTokenId = useMemo(() => new Map((balancesQuery.data ?? []).map(balance => [balance.id, balance])), [balancesQuery.data])
  const fromBalance = balanceByTokenId.get(fromToken.id)
  const toBalance = balanceByTokenId.get(toToken.id)
  const maxInputAmount = useMemo(() => balanceInputAmount(fromBalance, fromToken), [fromBalance, fromToken])
  const canUseMaxAmount = Boolean(actor && maxInputAmount && Number(maxInputAmount) > 0)

  const fillMaxAmount = useCallback(() => {
    if (maxInputAmount) setAmount(maxInputAmount)
  }, [maxInputAmount])

  const neoQuoteQuery = useQuery({
    queryKey: ['swap-route', fromToken.id, toToken.id, rawAmount, quoteReceiver, slippage, maxHops],
    queryFn: () => api.route({
      input: fromToken.id,
      output: toToken.id,
      amount: rawAmount,
      receiver: quoteReceiver,
      inputPrecision: fromToken.precision,
      outputPrecision: toToken.precision,
      slippage,
      maxHops,
    }),
    enabled: rawAmount > 0 && fromToken.id !== toToken.id,
    refetchInterval: rawAmount > 0 && fromToken.id !== toToken.id ? 12_000 : false,
    meta: { errorTitle: 'Route quote failed' },
  })

  const alcorRouteQuery = useQuery({
    queryKey: ['alcor-route', fromToken.id, toToken.id, rawAmount, quoteReceiver, slippage, maxHops, fromToken.precision],
    queryFn: () => api.alcorRoute({
      input: fromToken.id,
      output: toToken.id,
      amount: rawAmount,
      receiver: quoteReceiver,
      inputPrecision: fromToken.precision,
      outputPrecision: toToken.precision,
      slippage,
      maxHops,
    }),
    enabled: rawAmount > 0 && fromToken.id !== toToken.id,
    refetchInterval: rawAmount > 0 && fromToken.id !== toToken.id ? 12_000 : false,
    meta: { errorTitle: 'Alcor route failed', toast: false },
  })

  const combinedQuote = useMemo(
    () => combineRouteQuotes(neoQuoteQuery.data, alcorRouteQuery.data),
    [alcorRouteQuery.data, neoQuoteQuery.data],
  )
  const quoteQuery = {
    data: combinedQuote,
    error: neoQuoteQuery.error,
    isError: neoQuoteQuery.isError,
    isFetching: neoQuoteQuery.isFetching || alcorRouteQuery.isFetching,
  }

  const selectedRoute = routeChoice(quoteQuery.data, routePreference, quoteReceiver)
  const hasManualRoute = route.trim().length > 0
  const canSubmitRoute = Boolean(selectedRoute) || hasManualRoute
  const alcorAvailable = Boolean(quoteQuery.data?.alcor)
  const neoAvailable = Boolean(quoteQuery.data?.neo)
  const splitAvailable = Boolean(quoteQuery.data?.split)
  const routeIsLoading = quoteQuery.isFetching && rawAmount > 0
  const receivePreview = displayAmount(selectedRoute?.output)
  const finalContract = selectedRoute?.contract ?? DEFAULT_CONTRACT_ACCOUNT
  const finalMemo = selectedRoute?.memo ?? manualMemo
  const manualRouteSteps = useMemo(() => parseRouteSteps(route), [route])
  const alcorSteps = useMemo(() => alcorRouteSteps(quoteQuery.data, tokenOptions), [quoteQuery.data, tokenOptions])
  const neoSteps = useMemo(() => neoRouteSteps(quoteQuery.data, tokenOptions, fromToken, toToken), [fromToken, quoteQuery.data, toToken, tokenOptions])
  const splitSteps = useMemo(() => splitRouteSteps(quoteQuery.data, tokenOptions), [quoteQuery.data, tokenOptions])
  const selectedRouteSteps = useMemo(() => {
    if (!selectedRoute) return manualRouteSteps
    if (selectedRoute.source === 'split') return splitSteps
    return selectedRoute.source === 'alcor'
      ? alcorSteps
      : neoSteps
  }, [alcorSteps, manualRouteSteps, neoSteps, selectedRoute, splitSteps])
  const selectedHopCount = selectedRoute
    ? routeHopCount(quoteQuery.data, selectedRoute.source, selectedRouteSteps)
    : manualRouteSteps.length
  const selectedMinReceived = selectedRoute?.minReceived ?? (formatTokenAmount(minReturn, toToken.symbol) || `0 ${toToken.symbol}`)
  const selectedOutput = selectedRoute?.output ?? undefined
  const alcorFees = useMemo(() => alcorFeeSummary(quoteQuery.data?.alcor ?? null), [quoteQuery.data?.alcor])
  const neoFees = useMemo(() => neoFeeSummary(quoteQuery.data?.neo ?? null, toToken.symbol), [quoteQuery.data?.neo, toToken.symbol])
  const splitFees = useMemo(() => splitFeeSummary(quoteQuery.data?.split ?? null), [quoteQuery.data?.split])
  const selectedFees = selectedRoute?.source === 'alcor'
    ? alcorFees
    : selectedRoute?.source === 'neo'
      ? neoFees
      : selectedRoute?.source === 'split'
        ? splitFees
        : null
  const selectedPriceImpact = selectedRoute?.source === 'alcor'
    ? quoteQuery.data?.alcor?.priceImpact
    : undefined
  const neoNoRouteDetail = quoteQuery.data?.neoReason || 'No route'
  const splitNoRouteDetail = quoteQuery.data?.splitReason || 'No route'
  const manualRouteHint = manualRouteSteps.length > 0
    ? `${manualRouteSteps.length} hop${manualRouteSteps.length === 1 ? '' : 's'}: ${manualRouteSteps.map(step => step.label).join(' -> ')}`
    : 'Example: 5@d means Defibox pool 5.'

  const connectMutation = useMutation({
    mutationFn: login,
    meta: { errorTitle: 'Wallet connection failed' },
    onSuccess: session => {
      const nextActor = session.actor.toString()
      pushToast({ type: 'success', title: 'Wallet connected', message: nextActor })
    },
  })

  const swapMutation = useMutation({
    mutationFn: async () => {
      if (!session) throw new Error('Connect your wallet before swapping.')
      if (rawAmount <= 0) throw new Error('Enter a valid input amount.')
      if (!selectedRoute && !hasManualRoute) throw new Error(`No executable ${ROUTE_LABELS[routePreference]} route yet. Enter a manual ${DEFAULT_CONTRACT_ACCOUNT} route or wait for the quote.`)

      const actorName = session.actor.toString()
      if (selectedRoute?.source === 'alcor' && selectedRoute.receiver !== actorName) {
        await queryClient.invalidateQueries({ queryKey: ['alcor-route'] })
        throw new Error('Refreshing Alcor route for your wallet. Submit again after the quote updates.')
      }

      if (selectedRoute?.source === 'split') {
        if (!selectedRoute.legs?.length) throw new Error('Split route has no executable legs yet.')
        if (selectedRoute.legs.some(leg => leg.receiver !== actorName)) {
          await queryClient.invalidateQueries({ queryKey: ['swap-route'] })
          throw new Error('Refreshing split route for your wallet. Submit again after the quote updates.')
        }

        await session.transact({
          actions: selectedRoute.legs.map(leg => ({
            account: fromToken.contract as `${string}`,
            name: 'transfer',
            authorization: [{ actor: session.actor, permission: session.permission }],
            data: {
              from: actorName,
              to: leg.contract as `${string}`,
              quantity: `${formatRawAmount(leg.inputRaw, fromToken.precision)} ${fromToken.symbol}`,
              memo: leg.memo,
            },
          })),
        })
        return
      }

      if (finalContract === DEFAULT_CONTRACT_ACCOUNT && !finalMemo.startsWith('swap,')) {
        throw new Error(`${DEFAULT_CONTRACT_ACCOUNT} swaps require a memo that starts with swap,`)
      }

      const amtStr = Number(amount).toFixed(fromToken.precision)
      await session.transact({
        action: {
          account: fromToken.contract as `${string}`,
          name: 'transfer',
          authorization: [{ actor: session.actor, permission: session.permission }],
          data: {
            from: session.actor.toString(),
            to: finalContract,
            quantity: `${amtStr} ${fromToken.symbol}`,
            memo: finalMemo,
          },
        },
      })
    },
    meta: { errorTitle: 'Swap failed' },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['balances', actor] })
      pushToast({ type: 'success', title: 'Swap submitted', message: `Transfer sent to ${finalContract}` })
    },
  })

  const routePreferenceLabel = ROUTE_LABELS[routePreference]
  const quoteLabel = quoteQuery.isFetching && !quoteQuery.data
    ? 'Finding routes...'
    : selectedRoute
      ? `Selected route: ${selectedRoute.label}`
      : rawAmount > 0
        ? `${routePreferenceLabel} unavailable`
        : 'Quote preview'
  const quoteDetail = quoteQuery.isError
    ? getErrorMessage(quoteQuery.error)
    : selectedRoute?.source === 'split'
      ? `Execution sends multiple ${DEFAULT_CONTRACT_ACCOUNT} transfer actions in one wallet transaction.`
    : selectedRoute?.source === 'neo'
      ? `Execution supports ${DEFAULT_CONTRACT_ACCOUNT}.`
      : selectedRoute?.source === 'alcor'
        ? 'Execution uses Alcor router.'
        : actor
          ? 'Quotes refresh automatically while the form is ready.'
          : 'Preview receiver uses the configured contract until a wallet is connected.'

  return (
    <div className="grid gap-5">
      <PageHeader
        eyebrow="WAX liquidity router"
        title="Swap with route comparison"
        description="Quotes compare Alcor's router with NeoOnEdge indexed liquidity, while the final route stays under your control."
        action={(
          <div className="flex flex-wrap items-center justify-end gap-2">
            <Button variant="secondary" type="button" onClick={() => setShowChart(current => !current)}>
              {showChart ? 'Hide chart' : 'Show chart'}
            </Button>
            <Badge variant={tokenQuery.isError ? 'warning' : 'success'}>{tokenOptions.length} tokens available</Badge>
          </div>
        )}
      />

      <div className="grid gap-5 lg:grid-cols-[0.95fr_1.05fr]">
        <Card>
          <CardHeader>
            <div>
              <Eyebrow>Trade ticket</Eyebrow>
              <CardTitle>Instant swap</CardTitle>
            </div>
            <Button variant="secondary" type="button" onClick={() => connectMutation.mutate()} disabled={connectMutation.isPending || restoring}>
              {actor ? actor : restoring ? 'Restoring...' : connectMutation.isPending ? 'Connecting...' : 'Connect wallet'}
            </Button>
          </CardHeader>

          <CardContent className="grid gap-4">
            <div className="rounded-lg border border-zinc-800 bg-zinc-900/70 p-4">
              <div className="flex items-center justify-between gap-3">
                <Label className="mb-0">From</Label>
                <TokenSelect label="From token" value={fromToken} tokens={tokenOptions} onChange={chooseFromToken} />
              </div>
              <div className="mt-3 flex items-center gap-3">
                <Input className="h-14 min-w-0 flex-1 border-0 bg-transparent px-0 text-3xl font-black focus:ring-0" placeholder="0.0" value={amount} onChange={e => setAmount(e.target.value)} inputMode="decimal" />
                <Button className="shrink-0" variant="secondary" size="sm" type="button" onClick={fillMaxAmount} disabled={!canUseMaxAmount || (balancesQuery.isFetching && !fromBalance)}>
                  Max
                </Button>
              </div>
              <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-xs text-zinc-500">
                <span>Contract: {fromToken.contract}</span>
                <BalanceText actor={actor} balance={fromBalance} loading={balancesQuery.isFetching} token={fromToken} />
              </div>
            </div>

            <div className="-my-2 flex justify-center">
              <Button className="rounded-full px-4" variant="secondary" size="sm" type="button" onClick={flipTokens} disabled={fromToken.id === toToken.id} aria-label="Flip from and to tokens">
                Flip
              </Button>
            </div>

            <div className="rounded-lg border border-zinc-800 bg-zinc-900/70 p-4">
              <div className="flex items-center justify-between gap-3">
                <Label className="mb-0">To</Label>
                <TokenSelect label="To token" value={toToken} tokens={tokenOptions} onChange={chooseToToken} />
              </div>
              <div className="mt-3 flex min-h-14 items-end justify-between gap-3 border-0 bg-transparent px-0">
                <strong className={cn('min-w-0 truncate text-3xl font-black text-zinc-50', !receivePreview && 'text-zinc-600')}>
                  {receivePreview || '0.0'}
                </strong>
                <span className="shrink-0 pb-1 text-sm font-bold text-zinc-500">{toToken.symbol}</span>
              </div>
              <div className="mt-2 flex flex-wrap items-center justify-between gap-2 text-xs text-zinc-500">
                <span>Contract: {toToken.contract}</span>
                <BalanceText actor={actor} balance={toBalance} loading={balancesQuery.isFetching} token={toToken} />
              </div>
            </div>

            {tokenQuery.isError && <p className="text-sm text-amber-200">Using fallback tokens: {getErrorMessage(tokenQuery.error)}</p>}
            {tokenQuery.isFetching && <p className="text-xs text-zinc-500">Refreshing token catalog...</p>}

            <div>
              <Label>Route preference</Label>
              <RoutePreferenceControl
                value={routePreference}
                onChange={setRoutePreference}
                hasAmount={rawAmount > 0}
                loading={routeIsLoading}
                alcorAvailable={alcorAvailable}
                neoAvailable={neoAvailable}
                splitAvailable={splitAvailable}
              />
            </div>

            <div className="grid gap-3 sm:grid-cols-2">
              <div>
                <Label>Slippage</Label>
                <Select value={slippage} onChange={e => setSlippage(Number(e.target.value))}>
                  <option value={0.25}>0.25%</option>
                  <option value={0.5}>0.5%</option>
                  <option value={1}>1%</option>
                  <option value={2}>2%</option>
                  <option value={3}>3%</option>
                  <option value={5}>5%</option>
                  <option value={10}>10%</option>
                </Select>
              </div>
              <div>
                <Label>Max hops</Label>
                <Select value={maxHops} onChange={e => setMaxHops(Number(e.target.value))}>
                  <option value={1}>1</option>
                  <option value={2}>2</option>
                  <option value={3}>3</option>
                </Select>
              </div>
            </div>

            <details className="rounded-lg border border-zinc-800 bg-zinc-900/50 p-4">
              <summary className="cursor-pointer text-sm font-semibold text-zinc-200">Manual {DEFAULT_CONTRACT_ACCOUNT} route fallback</summary>
              <div className="mt-4 grid gap-3 sm:grid-cols-2">
                <div>
                  <Label>Minimum received ({toToken.symbol})</Label>
                  <Input placeholder="0.0" value={minReturn} onChange={e => setMinReturn(e.target.value)} inputMode="decimal" />
                </div>
                <div>
                  <Label>Route steps</Label>
                  <Input className="font-mono" placeholder="5@d" value={route} onChange={e => setRoute(e.target.value)} />
                </div>
              </div>
              <p className="mt-3 text-xs text-zinc-500">{manualRouteHint}</p>
            </details>

            <Button className="w-full" type="button" onClick={() => swapMutation.mutate()} disabled={swapMutation.isPending || rawAmount <= 0 || !canSubmitRoute}>
              {swapMutation.isPending ? 'Submitting...' : selectedRoute ? `Swap via ${selectedRoute.label}` : hasManualRoute ? `Swap via ${finalContract}` : 'Waiting for route'}
            </Button>
            {swapMutation.isError && <p className="text-sm text-red-300">{getErrorMessage(swapMutation.error)}</p>}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <div className="flex items-center gap-3">
              <span className={cn('h-3 w-3 rounded-full bg-zinc-600 ring-4 ring-zinc-600/20', quoteQuery.isFetching && 'bg-amber-400 ring-amber-400/20', selectedRoute && !quoteQuery.isFetching && 'bg-teal-400 ring-teal-400/20')} />
              <div>
                <CardTitle className="text-base">{quoteLabel}</CardTitle>
                <p className="mt-1 text-sm text-zinc-400">{quoteDetail}</p>
              </div>
            </div>
          </CardHeader>

          <CardContent className="grid gap-4">
            <div className="grid gap-3 xl:grid-cols-3">
              <QuoteCard
                title="Alcor router"
                winner={quoteQuery.data?.best === 'alcor'}
                selected={selectedRoute?.source === 'alcor'}
                loading={routeIsLoading && !quoteQuery.data?.alcor}
                badge={selectedRoute?.source === 'alcor' ? 'Selected' : quoteQuery.data?.best === 'alcor' ? 'Best' : undefined}
                value={quoteQuery.data?.alcor?.output ?? '-'}
                detail={quoteQuery.data?.alcor ? `${routeHopCount(quoteQuery.data, 'alcor', alcorSteps)} hop route - Min ${quoteQuery.data.alcor.minReceived}` : routeIsLoading ? 'Checking route' : 'No route'}
                fee={alcorFees?.label}
                onSelect={() => setRoutePreference('alcor')}
              />
              <QuoteCard
                title="NeoOnEdge"
                winner={quoteQuery.data?.best === 'neo'}
                selected={selectedRoute?.source === 'neo'}
                loading={routeIsLoading && !quoteQuery.data?.neo}
                badge={selectedRoute?.source === 'neo' ? 'Selected' : quoteQuery.data?.best === 'neo' ? 'Best' : undefined}
                value={quoteQuery.data?.neo?.output ?? '-'}
                detail={quoteQuery.data?.neo ? `${routeHopCount(quoteQuery.data, 'neo', neoSteps)} hop route - ${fromToken.symbol} to ${toToken.symbol}` : routeIsLoading ? 'Checking route' : neoNoRouteDetail}
                fee={neoFees?.label}
                onSelect={() => setRoutePreference('neo')}
              />
              <QuoteCard
                title="Neo split"
                winner={quoteQuery.data?.best === 'split'}
                selected={selectedRoute?.source === 'split'}
                loading={routeIsLoading && !quoteQuery.data?.split}
                badge={selectedRoute?.source === 'split' ? 'Selected' : quoteQuery.data?.best === 'split' ? 'Best' : undefined}
                value={quoteQuery.data?.split?.output ?? '-'}
                detail={quoteQuery.data?.split ? `${quoteQuery.data.split.legs.length} legs - Min ${quoteQuery.data.split.minReceived}` : routeIsLoading ? 'Checking split' : splitNoRouteDetail}
                fee={splitFees?.label}
                onSelect={() => setRoutePreference('split')}
              />
            </div>

            <RouteSummary
              contract={finalContract}
              memo={finalMemo}
              output={selectedOutput}
              minReceived={selectedMinReceived}
              fee={selectedFees?.label}
              feeDetail={selectedFees?.detail}
              priceImpact={selectedPriceImpact}
              steps={selectedRouteSteps}
              hops={selectedHopCount}
              receiver={selectedRoute?.receiver ?? quoteReceiver}
            />
          </CardContent>
        </Card>

        {showChart && (
          <Card className="lg:col-span-2">
            <CardHeader>
              <div>
                <Eyebrow>Pair chart</Eyebrow>
                <CardTitle>{fromToken.symbol} / {toToken.symbol}</CardTitle>
              </div>
              <Badge variant="muted">{fromToken.id} - {toToken.id}</Badge>
            </CardHeader>
            <CardContent>
              <KlineChartPanel
                neo={quoteQuery.data?.neo ? { src: quoteQuery.data.neo.src, pairId: quoteQuery.data.neo.pairId } : undefined}
                tokenA={fromToken.id}
                tokenB={toToken.id}
                defaultSource="alcor"
                height={320}
                compact
              />
            </CardContent>
          </Card>
        )}
      </div>
    </div>
  )
}
