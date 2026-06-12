import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { Session } from '@wharfkit/session'
import { login as walletLogin, logout as walletLogout, restoreSession } from './wallet'

type WalletContextValue = {
  session: Session | null
  actor: string
  restoring: boolean
  login: () => Promise<Session>
  logout: () => Promise<void>
}

const WalletContext = createContext<WalletContextValue | null>(null)

export function WalletProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null)
  const [restoring, setRestoring] = useState(true)

  useEffect(() => {
    let active = true

    restoreSession()
      .then(restored => {
        if (active) setSession(restored)
      })
      .catch(() => {
        if (active) setSession(null)
      })
      .finally(() => {
        if (active) setRestoring(false)
      })

    return () => {
      active = false
    }
  }, [])

  const login = useCallback(async () => {
    const nextSession = await walletLogin()
    setSession(nextSession)
    return nextSession
  }, [])

  const logout = useCallback(async () => {
    await walletLogout()
    setSession(null)
  }, [])

  const value = useMemo<WalletContextValue>(() => ({
    session,
    actor: session?.actor?.toString() ?? '',
    restoring,
    login,
    logout,
  }), [login, logout, restoring, session])

  return <WalletContext.Provider value={value}>{children}</WalletContext.Provider>
}

export function useWallet() {
  const value = useContext(WalletContext)
  if (!value) throw new Error('useWallet must be used inside WalletProvider')
  return value
}