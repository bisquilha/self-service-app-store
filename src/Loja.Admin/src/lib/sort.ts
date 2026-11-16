import { useCallback, useMemo, useState } from 'react'

export type SortDirection = 'asc' | 'desc'
export type SortState = { key: string; direction: SortDirection }
type Value = string | number | boolean | null | undefined

const collator = new Intl.Collator('pt-BR', { numeric: true, sensitivity: 'base' })

function compare(a: Value, b: Value): number {
  // Vazio vai sempre para o fim, qualquer que seja o sentido.
  const aEmpty = a === null || a === undefined || a === ''
  const bEmpty = b === null || b === undefined || b === ''
  if (aEmpty || bEmpty) return aEmpty === bEmpty ? 0 : aEmpty ? 1 : -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return collator.compare(String(a), String(b))
}

/**
 * Ordenação de tabela: `accessors` diz como ler cada coluna. Clicar de novo na mesma coluna inverte o sentido.
 * Texto compara em português e com números "naturais" (PC-9 antes de PC-10).
 */
export function useSort<T>(rows: T[], accessors: Record<string, (row: T) => Value>, initial: SortState | null = null) {
  const [sort, setSort] = useState<SortState | null>(initial)

  const toggle = useCallback((key: string) => {
    setSort((current) => (current?.key === key ? { key, direction: current.direction === 'asc' ? 'desc' : 'asc' } : { key, direction: 'asc' }))
  }, [])

  const sorted = useMemo(() => {
    const read = sort ? accessors[sort.key] : undefined
    if (!sort || !read) return rows
    const factor = sort.direction === 'asc' ? 1 : -1
    return [...rows].sort((x, y) => {
      const a = read(x)
      const b = read(y)
      const emptyA = a === null || a === undefined || a === ''
      const emptyB = b === null || b === undefined || b === ''
      return emptyA || emptyB ? compare(a, b) : factor * compare(a, b)
    })
  }, [rows, sort, accessors])

  return { sorted, sort, toggle }
}
