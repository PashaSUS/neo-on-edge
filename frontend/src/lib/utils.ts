export function cn(...classes: Array<string | false | null | undefined>): string {
  return classes.filter(Boolean).join(' ')
}

export function endpointBase(value: string | undefined, fallback: string) {
  const trimmed = value?.trim()
  if (!trimmed) return fallback

  const withoutTrailingSlash = trimmed.replace(/\/+$/, '')
  if (!withoutTrailingSlash || withoutTrailingSlash === '/') return fallback

  if (withoutTrailingSlash.startsWith('http://') || withoutTrailingSlash.startsWith('https://')) {
    return withoutTrailingSlash
  }

  if (withoutTrailingSlash.startsWith('/')) {
    return withoutTrailingSlash
  }

  const host = withoutTrailingSlash.split(/[/?#]/, 1)[0]
  const path = withoutTrailingSlash.slice(host.length) || fallback
  if (/^(localhost|127\.0\.0\.1)(:\d+)?$/i.test(host)) {
    return `http://${host}${path}`
  }

  if (host.includes('.') || host.includes(':')) {
    return `https://${host}${path}`
  }

  return `/${withoutTrailingSlash}`
}
