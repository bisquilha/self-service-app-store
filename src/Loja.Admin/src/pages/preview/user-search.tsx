import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { SearchIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { api, messageOf } from '@/lib/api'
import type { AdUser } from '@/lib/types'
import { cn } from '@/lib/utils'

/** Linha de uma pessoa: nome, login e, quando há, departamento e cargo. Serve na busca e em "Você quis dizer". */
export function UserLine({ user }: { user: AdUser }) {
  return (
    <span className="grid min-w-0 gap-0.5">
      <span className="flex flex-wrap items-baseline gap-x-2">
        <strong className="font-medium">{user.displayName ?? user.login}</strong>
        <span className="font-mono text-xs text-muted-foreground">{user.login}</span>
      </span>
      {user.department || user.title ? <span className="truncate text-xs text-muted-foreground">{[user.department, user.title].filter(Boolean).join(' · ')}</span> : null}
    </span>
  )
}

/** Lista clicável de pessoas (usada em "Você quis dizer"). */
export function UserList({ users, onPick }: { users: AdUser[]; onPick: (user: AdUser) => void }) {
  return (
    <ul className="grid w-full max-w-xl divide-y overflow-hidden rounded-lg border bg-card text-left">
      {users.map((user) => (
        <li key={user.login}>
          <button type="button" onClick={() => onPick(user)} className="w-full px-3 py-2 text-left outline-none hover:bg-muted focus-visible:bg-muted focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset">
            <UserLine user={user} />
          </button>
        </li>
      ))}
    </ul>
  )
}

type Props = {
  /** Login mostrado no campo quando a tela abre ou a rota muda. */
  initial: string
  /** Chamado ao escolher uma sugestão ou apertar Enter / "Ver a loja" com o texto digitado. */
  onSubmit: (login: string) => void
}

/**
 * Campo de busca de pessoas do AD com sugestões (a partir de 2 letras, depois de uma pausa na digitação).
 * Padrão combobox: setas escolhem, Enter confirma, Esc fecha.
 */
export function UserSearch({ initial, onSubmit }: Props) {
  const [text, setText] = useState(initial)
  const [open, setOpen] = useState(false)
  const [results, setResults] = useState<AdUser[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [searching, setSearching] = useState(false)
  const [active, setActive] = useState(-1)
  const listId = useId()
  const wrapper = useRef<HTMLDivElement>(null)

  useEffect(() => {
    setText(initial)
    setOpen(false)
  }, [initial])

  useEffect(() => {
    const query = text.trim()
    if (!open || query.length < 2) {
      setResults(null)
      setError(null)
      setSearching(false)
      return
    }
    const controller = new AbortController()
    const timer = setTimeout(() => {
      setSearching(true)
      api<AdUser[]>(`/users?q=${encodeURIComponent(query)}`, { signal: controller.signal })
        .then((users) => {
          setResults(users)
          setError(null)
          setActive(-1)
        })
        .catch((failure: unknown) => {
          if (controller.signal.aborted) return
          setResults(null)
          setError(messageOf(failure))
        })
        .finally(() => {
          if (!controller.signal.aborted) setSearching(false)
        })
    }, 250)
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [text, open])

  const showList = open && text.trim().length >= 2 && (searching || results !== null || error !== null)

  function choose(login: string) {
    setText(login)
    setOpen(false)
    onSubmit(login)
  }

  function submit() {
    const value = text.trim()
    if (!value) return
    setOpen(false)
    onSubmit(value)
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    const count = results?.length ?? 0
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen(true)
      if (count) setActive((current) => (event.key === 'ArrowDown' ? (current + 1) % count : (current - 1 + count) % count))
    } else if (event.key === 'Enter') {
      event.preventDefault()
      if (showList && results && active >= 0 && results[active]) choose(results[active].login)
      else submit()
    } else if (event.key === 'Escape' && open) {
      event.preventDefault()
      setOpen(false)
    }
  }

  return (
    <div className="flex flex-wrap items-start gap-2">
      <div
        ref={wrapper}
        className="relative min-w-64 flex-1"
        onBlur={(event) => {
          if (!wrapper.current?.contains(event.relatedTarget as Node | null)) setOpen(false)
        }}
      >
        <SearchIcon className="pointer-events-none absolute top-2.5 left-3 size-4 text-muted-foreground" aria-hidden="true" />
        <Input
          type="search"
          role="combobox"
          aria-label="Nome, sobrenome ou login da pessoa"
          aria-expanded={showList}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={active >= 0 ? `${listId}-${active}` : undefined}
          autoComplete="off"
          placeholder="Digite nome, sobrenome ou login (ex.: ana, vidigal, maria.souza)"
          className="pl-9"
          value={text}
          onChange={(event) => {
            setText(event.target.value)
            setOpen(true)
          }}
          onKeyDown={onKeyDown}
        />
        <div
          id={listId}
          role="listbox"
          aria-label="Pessoas encontradas"
          hidden={!showList}
          className={cn('absolute top-full right-0 left-0 z-20 mt-1 max-h-80 overflow-auto rounded-lg border bg-popover text-popover-foreground shadow-md', !showList && 'hidden')}
        >
          {error ? (
            <p role="alert" className="px-3 py-2.5 text-sm text-destructive">
              {error}
            </p>
          ) : results && results.length > 0 ? (
            results.map((user, index) => (
              <div
                key={user.login}
                id={`${listId}-${index}`}
                role="option"
                aria-selected={index === active}
                // mousedown não tira o foco do campo, e o clique escolhe a pessoa
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => choose(user.login)}
                onMouseMove={() => setActive(index)}
                className={cn('cursor-pointer px-3 py-2', index === active && 'bg-muted')}
              >
                <UserLine user={user} />
              </div>
            ))
          ) : (
            <p className="px-3 py-2.5 text-sm text-muted-foreground">{searching ? 'Buscando…' : 'Ninguém encontrado com esse nome.'}</p>
          )}
        </div>
      </div>
      <Button onClick={submit}>Ver a loja</Button>
    </div>
  )
}
