import { SessionKit, Session } from '@wharfkit/session'
import WebRenderer from '@wharfkit/web-renderer'
import { WalletPluginAnchor } from '@wharfkit/wallet-plugin-anchor'
import { WalletPluginCloudWallet } from '@wharfkit/wallet-plugin-cloudwallet'
import { WalletPluginWombat } from '@wharfkit/wallet-plugin-wombat'

export const sessionKit = new SessionKit({
  appName: 'NeoOnEdge',
  chains: [
    {
      id: '1064487b3cd1a897ce03ae5b6a865651747e2e152090f99c1d19d44e01aea5a4',
      url: 'https://wax.greymass.com',
    },
  ],
  ui: new WebRenderer(),
  walletPlugins: [
    new WalletPluginAnchor(),
    new WalletPluginCloudWallet(),
    new WalletPluginWombat(),
  ],
})

let _session: Session | null = null

export async function login(): Promise<Session> {
  const { session } = await sessionKit.login({ setAsDefault: true })
  _session = session
  return session
}

export async function restoreSession(): Promise<Session | null> {
  if (_session) return _session

  const session = await sessionKit.restore()
  _session = session ?? null
  return _session
}

export async function logout(): Promise<void> {
  if (_session) {
    await sessionKit.logout(_session)
  } else {
    await sessionKit.logout()
  }

  _session = null
}

export function getSession(): Session | null {
  return _session
}

export function getActor(): string | null {
  return _session?.actor?.toString() ?? null
}
