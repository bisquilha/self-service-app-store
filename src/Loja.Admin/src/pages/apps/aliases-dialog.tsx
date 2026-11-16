import { useEffect, useState } from 'react'
import { PlusIcon } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useApi } from '@/hooks/use-api'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'
import type { AdminApp, ProgramName } from '@/lib/types'

const MAX_NAMES = 10

/** Mesma regra do servidor: sem diferenciar maiúsculas nem espaços repetidos. */
const normalize = (name: string) => name.trim().replace(/\s+/g, ' ')
const keyOf = (name: string) => normalize(name).toLowerCase()

type Props = {
  app: AdminApp | null
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Depois de salvar: a tela recarrega e avisa com "Desfazer". `before` são os nomes de antes. */
  onSaved: (app: AdminApp, before: string[]) => void
}

function Form({ app, close, onSaved }: { app: AdminApp; close: () => void; onSaved: Props['onSaved'] }) {
  const before = app.aliases ?? []
  const [names, setNames] = useState<string[]>(before)
  const [draft, setDraft] = useState('')
  const [search, setSearch] = useState(app.title)
  const [debounced, setDebounced] = useState(app.title)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(search.trim()), 300)
    return () => clearTimeout(timer)
  }, [search])
  const found = useApi<ProgramName[]>(debounced.length >= 2 ? `/programs?q=${encodeURIComponent(debounced)}` : null)

  const has = (name: string) => names.some((existing) => keyOf(existing) === keyOf(name))
  const unchanged = names.length === before.length && names.every((name) => before.some((old) => keyOf(old) === keyOf(name)))

  function add(raw: string) {
    const name = normalize(raw)
    setNotice(null)
    if (!name) return
    if (name.length > 200) return setNotice('O nome passa de 200 letras.')
    if (has(name)) return setNotice('Este nome já está na lista.')
    if (names.length >= MAX_NAMES) return setNotice(`No máximo ${MAX_NAMES} nomes por aplicativo.`)
    setNames((current) => [...current, name])
    setDraft('')
  }

  async function save() {
    setSaving(true)
    setError(null)
    try {
      await api(`/apps/${app.id}/aliases`, { method: 'PUT', body: { aliases: names } })
      onSaved(app, before)
      close()
    } catch (e) {
      setError(messageOf(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Nomes no Windows</DialogTitle>
        <DialogDescription>
          Quando um computador tem um programa com <strong>exatamente</strong> um destes nomes em “Programas e Recursos”, a loja mostra “Já instalado no computador · Fora da loja” para <strong>{app.title}</strong>.
          É só informação: isso não libera atualizar nem desinstalar pela loja.
        </DialogDescription>
      </DialogHeader>

      <section aria-label="Nomes cadastrados" className="grid gap-1.5">
        <h3 className="text-[12.5px] font-medium text-muted-foreground">Nomes cadastrados</h3>
        {names.length > 0 ? (
          <ul className="flex flex-wrap gap-1.5">
            {names.map((name) => (
              <li key={keyOf(name)} className="max-w-full">
                <Chip onRemove={() => setNames((current) => current.filter((n) => n !== name))} removeLabel={`Remover o nome ${name}`}>
                  {name}
                </Chip>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-sm text-muted-foreground">Nenhum nome: a loja não procura este aplicativo fora da loja.</p>
        )}
        <form
          className="flex items-end gap-2"
          onSubmit={(event) => {
            event.preventDefault()
            add(draft)
          }}
        >
          <div className="grid flex-1 gap-1">
            <Label htmlFor="novo-nome" className="text-[12.5px] font-normal text-muted-foreground">
              Novo nome (como aparece em Programas e Recursos)
            </Label>
            <Input id="novo-nome" value={draft} maxLength={200} onChange={(event) => setDraft(event.target.value)} placeholder={`Ex.: ${app.title}`} />
          </div>
          <Button type="submit" variant="secondary" disabled={!draft.trim()}>
            <PlusIcon aria-hidden="true" />
            Adicionar
          </Button>
        </form>
        {notice ? (
          <p role="status" className="text-xs font-medium text-warning">
            {notice}
          </p>
        ) : null}
      </section>

      <section aria-label="Programas encontrados nas máquinas" className="grid gap-1.5">
        <h3 className="text-[12.5px] font-medium text-muted-foreground">Programas parecidos nas máquinas</h3>
        <Input type="search" aria-label="Buscar entre os programas das máquinas" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Buscar pelo nome do programa…" />
        <div className="max-h-48 overflow-auto rounded-lg border">
          {found.loading && !found.data ? (
            <p role="status" className="p-3 text-sm text-muted-foreground">
              Buscando…
            </p>
          ) : found.error ? (
            <p role="alert" className="p-3 text-sm text-destructive">
              {found.error}
            </p>
          ) : (found.data ?? []).length === 0 ? (
            <p className="p-3 text-sm text-muted-foreground">
              {debounced.length < 2 ? 'Digite ao menos 2 letras.' : 'Nenhum programa com esse texto nas máquinas que já enviaram a lista de programas do Windows.'}
            </p>
          ) : (
            <ul className="divide-y">
              {(found.data ?? []).map((program) => (
                <li key={program.name} className="flex items-center gap-2 px-3 py-1.5">
                  <span className="min-w-0 flex-1 truncate" title={program.name}>
                    {program.name}
                  </span>
                  <span className="shrink-0 text-xs text-muted-foreground">{plural(program.machines, 'máquina')}</span>
                  <Button type="button" variant="ghost" size="xs" disabled={has(program.name)} onClick={() => add(program.name)} aria-label={`Usar o nome ${program.name}`}>
                    {has(program.name) ? 'Na lista' : 'Usar este nome'}
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </div>
        <p className="text-xs text-muted-foreground">A busca olha só as máquinas que já enviaram a lista de programas. Programas instalados só para um usuário e programas portáteis não aparecem.</p>
      </section>

      {error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}

      <DialogFooter>
        <Button variant="secondary" disabled={saving} onClick={close}>
          Cancelar
        </Button>
        <Button loading={saving} disabled={unchanged} onClick={save}>
          Salvar nomes
        </Button>
      </DialogFooter>
    </>
  )
}

export function AliasesDialog({ app, open, onOpenChange, onSaved }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-xl">{app ? <Form key={app.id} app={app} close={() => onOpenChange(false)} onSaved={onSaved} /> : null}</DialogContent>
    </Dialog>
  )
}
