import { useCallback, useEffect, useState } from 'react'
import { api } from '@/lib/api'

type State<T> = { data: T | null; error: string | null; loading: boolean }

/**
 * Carrega dados de GET /api/admin{path}. Cancela a chamada ao sair da tela e guarda o último dado enquanto recarrega,
 * para a tela não piscar. `reload()` busca de novo (depois de salvar ou apagar algo).
 * Com `path` nulo não busca nada (a tela ainda não sabe o que pedir) e devolve `data: null` sem `loading`.
 */
export function useApi<T>(path: string | null) {
  const [state, setState] = useState<State<T>>({ data: null, error: null, loading: path !== null })
  const [round, setRound] = useState(0)

  useEffect(() => {
    if (path === null) {
      setState({ data: null, error: null, loading: false })
      return
    }
    const controller = new AbortController()
    setState((previous) => ({ ...previous, error: null, loading: true }))
    api<T>(path, { signal: controller.signal })
      .then((data) => setState({ data, error: null, loading: false }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setState((previous) => ({ data: previous.data, error: error instanceof Error ? error.message : 'Falha ao carregar.', loading: false }))
      })
    return () => controller.abort()
  }, [path, round])

  const reload = useCallback(() => setRound((n) => n + 1), [])
  return { ...state, reload }
}
