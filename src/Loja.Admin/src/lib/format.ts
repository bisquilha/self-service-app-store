/** Português correto no singular e no plural: nada de "pedido(s)". `plural(1, 'pedido')` → "1 pedido". */
export function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
  return `${count.toLocaleString('pt-BR')} ${count === 1 ? singular : pluralForm}`
}

/** Tamanho de arquivo legível: 68,4 MB. */
export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  const digits = unit === 0 ? 0 : 1
  return `${value.toLocaleString('pt-BR', { maximumFractionDigits: digits, minimumFractionDigits: digits })} ${units[unit]}`
}

/** "21/10/2026 17:03". Aceita o texto ISO que o servidor manda (UTC). */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  return new Date(value).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' })
}

/** "CORP\ana.souza" → "ana.souza". */
export function shortLogin(name: string | null | undefined): string {
  if (!name) return '—'
  const slash = name.lastIndexOf('\\')
  return slash >= 0 ? name.slice(slash + 1) : name
}

/** "há 5 min", "há 3 h", "há 4 dias". Sem data: "nunca". */
export function formatAgo(value: string | null | undefined): string {
  if (!value) return 'nunca'
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60000)
  if (minutes < 1) return 'agora'
  if (minutes < 60) return `há ${minutes} min`
  const hours = Math.round(minutes / 60)
  if (hours < 48) return `há ${hours} h`
  return `há ${Math.round(hours / 24)} dias`
}

/** "Microsoft Windows 11 Pro, 64 bit, build 26200" → "Windows 11 Pro". */
export function shortOs(os: string | null | undefined): string {
  return (os ?? '—').replace(/,.*$/, '').replace('Microsoft ', '')
}

/** Dia local no formato aaaa-mm-dd (o que o filtro de datas do Histórico espera). `daysAgo` volta no tempo. */
export function localDay(daysAgo = 0): string {
  const date = new Date()
  date.setDate(date.getDate() - daysAgo)
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}
