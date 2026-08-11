import type { TokenInfo } from '../../api/neoEdgeApi'

export type Token = Pick<TokenInfo, 'contract' | 'symbol' | 'precision' | 'id'> & {
  source?: string
  logoUrl?: string | null
  popularityScore?: number
  isTrusted?: boolean
  balanceAmount?: number
  balanceAsset?: string
  usdValue?: number | null
  isOwned?: boolean
}
