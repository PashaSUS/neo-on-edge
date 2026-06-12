export type ToastType = 'success' | 'error' | 'info'

export type Toast = {
  id: string
  type: ToastType
  title: string
  message?: string
}

const listeners = new Set<() => void>()
let toasts: Toast[] = []

function emit() {
  listeners.forEach(listener => listener())
}

export function getToasts(): Toast[] {
  return toasts
}

export function subscribeToasts(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function dismissToast(id: string) {
  toasts = toasts.filter(toast => toast.id !== id)
  emit()
}

export function pushToast(input: Omit<Toast, 'id'>) {
  const id = `${Date.now()}-${Math.random().toString(36).slice(2)}`
  toasts = [{ ...input, id }, ...toasts].slice(0, 5)
  emit()

  window.setTimeout(() => dismissToast(id), input.type === 'error' ? 7000 : 4500)
}

export function getErrorMessage(error: unknown): string {
  if (error instanceof Error && error.message) return error.message
  if (typeof error === 'string') return error
  return 'Something went wrong. Please try again.'
}
