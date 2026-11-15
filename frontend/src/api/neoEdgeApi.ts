import { endpointBase } from '../lib/utils'

const BASE = endpointBase(import.meta.env.VITE_API_BASE_URL, '/api')
const ALCOR_BASE = endpointBase(import.meta.env.VITE_ALCOR_ENDPOINT, 'https://wax.alcor.exchange')

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly payload?: unknown,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

export type Trade = {
  trxId: string; src: string; pairId: string; maker: string
  amountIn: number; amountOut: number; codeIn: string; codeOut: string
  updatedAtTime: number
}

export type Market = {
  src: string; pairId: string; tokenA: string; tokenB: string
  volumeA: number; volumeB: number; tradeCount: number; lastTrade: number
}

export type KlineBar = {
  bucketTs: number; open: number; high: number; low: number; close: number
  volumeA: number; volumeB: number; tradeCount: number
}

export type TokenBalance = {
  id: string
  contract: string
  symbol: string
  amount: number
  asset: string
  error?: string | null
}

export type PortfolioTrade = Trade & { type: 'amm' | 'v3' }

export type LimitOrder = {
  type: 'fill' | 'close'
  trxId: string
  src: string
  mode: string
  owner: string
  askCode: string
  bidCode: string
  amountAsk: number
  amountBid: number
  marketId: number
  updatedAtTime: number
}

export type TokenInfo = {
  id: string
  contract: string
  symbol: string
  precision: number
  source: string
  logoUrl?: string | null
  usdPrice?: number | null
  popularityScore: number
  isTrusted: boolean
}

export type OwnedTokenInfo = TokenInfo & {
  balanceAmount: number
  balanceAsset: string
  usdValue?: number | null
}

export type AlcorRoute = {
  route: number[]
  memo: string
  receiver?: string
  input: string
  output: string
  minReceived: string
  maxSent: string
  priceImpact: string
  protocolFeeBps?: number
  liquidityProviderFeeBps?: number
  totalFeeBps?: number
  pools?: Record<string, { fee: number }>
  swaps: Array<{
    input: string
    output: string
    route: number[]
    percent: number
    memo: string
    maxSent: string
    minReceived: string
  }>
}

export type NeoRoute = {
  src: string
  pairId: string
  output: string
  minReceived: string
  memo: string
  receiver: string
  contractAccount: string
  protocolFeeBps?: number
  liquidityProviderFeeBps?: number
  totalFeeBps?: number
  estimatedProtocolFee?: string
  steps?: Array<{
    src: string
    pairId: string
    fromSymbol: string
    toSymbol: string
  }>
}

export type SplitRouteStep = {
  src: string
  pairId: string
  fromSymbol: string
  toSymbol: string
}

export type SplitRouteLeg = {
  source: 'neo'
  label: string
  contract: string
  receiver: string
  memo: string
  inputRaw: number
  inputAmount: string
  output: string
  minReceived: string
  hops: number
  steps: SplitRouteStep[]
  protocolFeeBps?: number
  liquidityProviderFeeBps?: number
  totalFeeBps?: number
  estimatedProtocolFee?: string
}

export type SplitRoute = {
  output: string
  minReceived: string
  receiver: string
  legs: SplitRouteLeg[]
  totalFeeBps?: number
  execution: 'multi-action'
}

export type SwapRoute = {
  alcor: AlcorRoute | null
  neo: NeoRoute | null
  split: SplitRoute | null
  neoReason?: string | null
  splitReason?: string | null
  best: 'alcor' | 'neo' | 'split' | 'none'
}

export type RouteRequest = {
  input: string
  output: string
  amount: number
  receiver?: string
  inputPrecision?: number
  outputPrecision?: number
  slippage?: number
  maxHops?: number
  fresh?: boolean
}

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(`${BASE}${path}`, { signal })
  if (!r.ok) throw await toApiError(r)
  return r.json() as Promise<T>
}

async function getAbsolute<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { cache: 'no-store', signal })
  if (!r.ok) throw await toApiError(r)
  return r.json() as Promise<T>
}

async function toApiError(response: Response): Promise<ApiError> {
  const text = await response.text().catch(() => '')
  let payload: unknown
  let message = text || `HTTP ${response.status}`

  if (text) {
    try {
      payload = JSON.parse(text)
      if (payload && typeof payload === 'object' && 'message' in payload) {
        message = String((payload as { message: unknown }).message)
      }
    } catch {
      message = text
    }
  }

  return new ApiError(response.status, message, payload)
}

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') search.set(key, String(value))
  }
  return search.toString()
}

function formatRawTokenAmount(rawAmount: number, precision = 8) {
  const safePrecision = Math.max(0, Math.min(18, precision))
  return (rawAmount / 10 ** safePrecision).toFixed(safePrecision)
}

export const api = {
  tokens: (q?: string, limit = 500) =>
    get<TokenInfo[]>(`/tokens?${qs({ q, limit })}`),

  ownedTokens: (account: string, limit = 200) =>
    get<OwnedTokenInfo[]>(`/tokens/owned/${encodeURIComponent(account)}?${qs({ limit })}`),

  trades: (src: string, pairId: string, limit = 100, before?: number) =>
    get<Trade[]>(`/trades?src=${src}&pairId=${encodeURIComponent(pairId)}&limit=${limit}${before ? `&before=${before}` : ''}`),

  klines: (src: string, pairId: string, duration = '1h', limit = 500, before?: number, signal?: AbortSignal) =>
    get<KlineBar[]>(`/klines?src=${src}&pairId=${encodeURIComponent(pairId)}&duration=${duration}&limit=${limit}${before ? `&before=${before}` : ''}`, signal),

  alcorKlines: (tokenA: string, tokenB: string, resolution = 240, from?: number, to?: number, limit = 500, signal?: AbortSignal) =>
    get<KlineBar[]>(`/klines/alcor?${qs({ tokenA, tokenB, resolution, from, to, limit })}`, signal),

  markets: (src?: string) =>
    get<Market[]>(`/markets${src ? `?src=${src}` : ''}`),

  marketStats: (src: string, pairId: string) =>
    get<Market>(`/markets/${src}/${encodeURIComponent(pairId)}/stats`),

  portfolio: (account: string, limit = 100, before?: number) =>
    get<PortfolioTrade[]>(`/portfolio/${account}?limit=${limit}${before ? `&before=${before}` : ''}`),

  limitOrders: (account: string, limit = 100) =>
    get<LimitOrder[]>(`/portfolio/${account}/limit-orders?limit=${limit}`),

  balances: (account: string, tokens: string[]) =>
    get<TokenBalance[]>(`/balances/${encodeURIComponent(account)}?${qs({ tokens: tokens.join(',') })}`),

  route: (request: RouteRequest, signal?: AbortSignal) =>
    get<SwapRoute>(`/route?${qs({
      input: request.input,
      output: request.output,
      amount: request.amount,
      receiver: request.receiver,
      inputPrecision: request.inputPrecision,
      outputPrecision: request.outputPrecision,
      slippage: request.slippage ?? 1,
      maxHops: request.maxHops ?? 3,
      fresh: request.fresh ? true : undefined,
    })}`, signal),

  alcorRoute: async (request: RouteRequest, signal?: AbortSignal) => {
    const receiver = request.receiver ?? ''
    const route = await getAbsolute<AlcorRoute>(`${ALCOR_BASE}/api/v2/swapRouter/getRoute?${qs({
      trade_type: 'EXACT_INPUT',
      input: request.input,
      output: request.output,
      amount: formatRawTokenAmount(request.amount, request.inputPrecision ?? 8),
      slippage: request.slippage ?? 1,
      v2: true,
      includePoolDetails: true,
      receiver,
      maxHops: request.maxHops ?? 3,
    })}`, signal)

    return { ...route, receiver }
  },

  sessionToken: () =>
    fetch(`${BASE}/socket-session-token`, { method: 'POST' }).then(async r => {
      if (!r.ok) throw await toApiError(r)
      return r.json() as Promise<{ token: string }>
    }),
}
