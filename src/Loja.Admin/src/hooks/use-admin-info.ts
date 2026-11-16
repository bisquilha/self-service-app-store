import { useEffect, useState } from 'react'
import { api } from '@/lib/api'
import type { AdminInfo } from '@/lib/types'

type State = { info: AdminInfo | null; error: string | null; loading: boolean }

/** Quem está logado e a versão do servidor (GET /api/admin/info). Serve também de teste do login e do acesso de admin. */
export function useAdminInfo(): State {
  const [state, setState] = useState<State>({ info: null, error: null, loading: true })

  useEffect(() => {
    const controller = new AbortController()
    api<AdminInfo>('/info', { signal: controller.signal })
      .then((info) => setState({ info, error: null, loading: false }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setState({ info: null, error: error instanceof Error ? error.message : 'Falha ao carregar.', loading: false })
      })
    return () => controller.abort()
  }, [])

  return state
}
