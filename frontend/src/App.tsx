import { createBrowserRouter, Navigate, NavLink, Outlet, RouterProvider } from 'react-router-dom'
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ToastViewport } from './components/ToastViewport'
import { ApiError } from './api/neoEdgeApi'
import { getErrorMessage, pushToast } from './lib/toastStore'
import { cn } from './lib/utils'
import { WalletProvider } from './lib/walletContext'
import SwapPage from './pages/SwapPage'
import MarketsPage from './pages/MarketsPage'
import ChartsPage from './pages/ChartsPage'
import TradesPage from './pages/TradesPage'
import PortfolioPage from './pages/PortfolioPage'

const qc = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (query.meta?.toast === false) return
      pushToast({
        type: 'error',
        title: typeof query.meta?.errorTitle === 'string' ? query.meta.errorTitle : 'Data request failed',
        message: getErrorMessage(error),
      })
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      if (mutation.meta?.toast === false) return
      pushToast({
        type: 'error',
        title: typeof mutation.meta?.errorTitle === 'string' ? mutation.meta.errorTitle : 'Action failed',
        message: getErrorMessage(error),
      })
    },
  }),
  defaultOptions: {
    queries: {
      retry: (failureCount, error) =>
        !(error instanceof ApiError && error.status === 429) && failureCount < 2,
      staleTime: 20_000,
      refetchOnWindowFocus: false,
    },
    mutations: {
      retry: 0,
    },
  },
})

function AppShell() {
  return (
    <div className="flex min-h-screen flex-col bg-zinc-950 text-zinc-100">
      <header className="sticky top-0 z-20 flex items-center justify-between gap-6 border-b border-zinc-800 bg-zinc-950/90 px-6 py-4 backdrop-blur max-sm:flex-col max-sm:items-start">
        <div className="grid gap-1">
          <NavLink to="/swap" className="w-max text-xl font-black text-white">NeoOnEdge</NavLink>
          <p className="m-0 text-sm text-zinc-400">A fork of Wax on Edge.</p>
        </div>
        <nav className="flex gap-1 rounded-lg border border-zinc-800 bg-zinc-900 p-1 max-sm:w-full" aria-label="Main navigation">
          <NavLink to="/swap" className={({ isActive }) => cn('inline-flex h-9 items-center justify-center rounded-md px-4 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white max-sm:flex-1', isActive && 'bg-zinc-800 text-white')}>Swap</NavLink>
          <NavLink to="/markets" className={({ isActive }) => cn('inline-flex h-9 items-center justify-center rounded-md px-4 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white max-sm:flex-1', isActive && 'bg-zinc-800 text-white')}>Markets</NavLink>
          <NavLink to="/portfolio" className={({ isActive }) => cn('inline-flex h-9 items-center justify-center rounded-md px-4 text-sm font-bold text-zinc-400 hover:bg-zinc-800 hover:text-white max-sm:flex-1', isActive && 'bg-zinc-800 text-white')}>Portfolio</NavLink>
        </nav>
      </header>

      <main className="mx-auto w-[min(1180px,calc(100%-2rem))] flex-1 py-6 sm:py-10">
        <Outlet />
      </main>

      <footer className="border-t border-zinc-800 bg-zinc-950/90 px-6 py-4 text-center text-sm text-zinc-400">
        NeoOnEdge is a fork of Wax on Edge. Grateful to Wapaca, the initial creator of the Wax on Edge system.
      </footer>
      <ToastViewport />
    </div>
  )
}

const router = createBrowserRouter([
  {
    path: '/',
    element: <AppShell />,
    children: [
      { index: true, element: <Navigate to="/swap" replace /> },
      { path: 'swap', element: <SwapPage /> },
      { path: 'swap/:fromId/:toId', element: <SwapPage /> },
      { path: 'markets', element: <MarketsPage /> },
      { path: 'charts/:src/:id', element: <ChartsPage /> },
      { path: 'trades/:src/:id', element: <TradesPage /> },
      { path: 'portfolio', element: <PortfolioPage /> },
      { path: '*', element: <Navigate to="/swap" replace /> },
    ],
  },
])

export default function App() {
  return (
    <QueryClientProvider client={qc}>
      <WalletProvider>
        <RouterProvider router={router} />
      </WalletProvider>
    </QueryClientProvider>
  )
}
