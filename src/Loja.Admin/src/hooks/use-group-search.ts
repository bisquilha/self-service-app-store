import { useEffect, useRef, useState } from 'react'
import { api, messageOf } from '@/lib/api'
import type { AdGroup, GroupRef } from '@/lib/types'

type SearchState = { groups: AdGroup[]; loading: boolean; error: string | null }

/**
 * Busca de grupos do AD (GET /api/admin/groups) para os seletores. Por padrão só os grupos da loja (LOJA-*);
 * `showAll` procura em todo o domínio. Espera a pessoa parar de digitar; a lista inicial (sem texto) sai na hora.
 * `seen` guarda tudo o que as buscas já devolveram, para o grupo escolhido manter SID e nome certos depois que a lista muda.
 */
export function useGroupSearch() {
  const [query, setQuery] = useState('')
  const [showAll, setShowAll] = useState(false)
  const [state, setState] = useState<SearchState>({ groups: [], loading: true, error: null })
  const seen = useRef(new Map<string, GroupRef>())

  useEffect(() => {
    const controller = new AbortController()
    const text = query.trim()
    setState((previous) => ({ ...previous, loading: true, error: null }))
    const timer = setTimeout(
      () => {
        api<AdGroup[]>(`/groups?q=${encodeURIComponent(text)}&all=${showAll}`, { signal: controller.signal })
          .then((groups) => {
            for (const group of groups) seen.current.set(group.sid, { name: group.name, sid: group.sid })
            setState({ groups, loading: false, error: null })
          })
          .catch((error: unknown) => {
            if (controller.signal.aborted) return
            setState({ groups: [], loading: false, error: messageOf(error, 'Não foi possível consultar o AD agora.') })
          })
      },
      text ? 250 : 0,
    )
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [query, showAll])

  return { query, setQuery, showAll, setShowAll, state, seen }
}
