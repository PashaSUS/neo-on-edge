import type { ComponentProps, ReactNode } from 'react'
import { cn } from '../lib/utils'

export function Button({ className, variant = 'default', size = 'default', ...props }: ComponentProps<'button'> & {
  variant?: 'default' | 'secondary' | 'ghost' | 'destructive'
  size?: 'default' | 'sm'
}) {
  return (
    <button
      className={cn(
        'inline-flex items-center justify-center rounded-lg font-semibold transition-colors disabled:pointer-events-none disabled:opacity-50',
        size === 'sm' ? 'h-8 px-3 text-xs' : 'h-10 px-4 text-sm',
        variant === 'default' && 'bg-teal-500 text-zinc-950 hover:bg-teal-400',
        variant === 'secondary' && 'border border-zinc-700 bg-zinc-900 text-zinc-100 hover:border-teal-500 hover:text-white',
        variant === 'ghost' && 'text-zinc-300 hover:bg-zinc-800 hover:text-white',
        variant === 'destructive' && 'bg-red-500 text-white hover:bg-red-400',
        className,
      )}
      {...props}
    />
  )
}

export function LoadingMark({ className, label = 'Loading' }: { className?: string; label?: string }) {
  return (
    <span role="status" aria-label={label} className={cn('relative inline-grid h-6 w-6 place-items-center', className)}>
      <span className="absolute inset-0 rounded-full border border-zinc-700 border-t-teal-300 animate-spin" />
      <span className="absolute inset-1 rounded-full border border-zinc-800 border-b-cyan-300 animate-[spin_1.35s_linear_infinite_reverse]" />
      <span className="h-1.5 w-1.5 rounded-full bg-teal-300 shadow-[0_0_12px_rgba(45,212,191,0.75)] animate-pulse" />
    </span>
  )
}

export function Card({ className, ...props }: ComponentProps<'section'>) {
  return <section className={cn('rounded-lg border border-zinc-800 bg-zinc-950/80 shadow-[0_18px_50px_rgba(0,0,0,0.22)]', className)} {...props} />
}

export function CardHeader({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('flex items-start justify-between gap-4 p-5', className)} {...props} />
}

export function CardContent({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('p-5 pt-0', className)} {...props} />
}

export function CardTitle({ className, ...props }: ComponentProps<'h2'>) {
  return <h2 className={cn('m-0 text-xl font-bold text-zinc-50', className)} {...props} />
}

export function CardDescription({ className, ...props }: ComponentProps<'p'>) {
  return <p className={cn('mt-1 text-sm text-zinc-400', className)} {...props} />
}

export function PageHeader({ eyebrow, title, description, action }: {
  eyebrow?: string
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <Card className="bg-zinc-950/85">
      <CardHeader className="items-center max-sm:flex-col max-sm:items-stretch">
        <div>
          {eyebrow && <Eyebrow>{eyebrow}</Eyebrow>}
          <h1 className="m-0 text-2xl font-bold text-zinc-50">{title}</h1>
          {description && <p className="mt-2 max-w-2xl text-sm text-zinc-400">{description}</p>}
        </div>
        {action}
      </CardHeader>
    </Card>
  )
}

export function Eyebrow({ className, ...props }: ComponentProps<'p'>) {
  return <p className={cn('mb-2 text-xs font-bold uppercase tracking-normal text-teal-300', className)} {...props} />
}

export function Input({ className, ...props }: ComponentProps<'input'>) {
  return (
    <input
      className={cn('h-10 rounded-lg border border-zinc-800 bg-zinc-900 px-3 text-sm text-zinc-50 outline-none transition focus:border-teal-500 focus:ring-2 focus:ring-teal-500/20', className)}
      {...props}
    />
  )
}

export function Select({ className, ...props }: ComponentProps<'select'>) {
  return (
    <select
      className={cn('h-10 rounded-lg border border-zinc-800 bg-zinc-900 px-3 text-sm text-zinc-50 outline-none transition focus:border-teal-500 focus:ring-2 focus:ring-teal-500/20', className)}
      {...props}
    />
  )
}

export function Label({ className, ...props }: ComponentProps<'label'>) {
  return <label className={cn('mb-2 block text-xs font-semibold text-zinc-400', className)} {...props} />
}

export function Badge({ className, variant = 'default', ...props }: ComponentProps<'span'> & { variant?: 'default' | 'success' | 'muted' | 'warning' }) {
  return (
    <span
      className={cn(
        'inline-flex h-6 items-center rounded-full px-2.5 text-xs font-bold uppercase tracking-normal',
        variant === 'default' && 'bg-blue-500/15 text-blue-200',
        variant === 'success' && 'bg-teal-500/15 text-teal-200',
        variant === 'muted' && 'bg-zinc-800 text-zinc-300',
        variant === 'warning' && 'bg-amber-500/15 text-amber-200',
        className,
      )}
      {...props}
    />
  )
}

export function MetricCard({ label, value }: { label: string; value: ReactNode }) {
  return (
    <Card className="p-4">
      <span className="text-xs font-semibold text-zinc-400">{label}</span>
      <strong className="mt-1 block text-2xl font-bold text-zinc-50">{value}</strong>
    </Card>
  )
}

export function Table({ className, ...props }: ComponentProps<'table'>) {
  return <table className={cn('w-full min-w-full border-collapse text-left text-sm', className)} {...props} />
}

export function Th({ className, ...props }: ComponentProps<'th'>) {
  return <th className={cn('border-b border-zinc-800 px-3 pb-3 text-xs font-bold uppercase tracking-normal text-zinc-400', className)} {...props} />
}

export function Td({ className, ...props }: ComponentProps<'td'>) {
  return <td className={cn('border-b border-zinc-800/70 px-3 py-3 align-middle text-zinc-200', className)} {...props} />
}
