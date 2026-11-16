import { useRef, useState } from 'react'
import { toast } from 'sonner'
import { SearchIcon, XIcon } from 'lucide-react'
import { GroupSelect } from '@/components/group-select'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'
import type { AdminApp, GroupRef, PackageResult } from '@/lib/types'

type Source = 'community' | 'internal'
type Results = { state: 'idle' } | { state: 'loading' } | { state: 'ok'; items: PackageResult[] } | { state: 'error'; message: string }

/** Um aplicativo escolhido para entrar na loja, com os grupos dele e o erro da última tentativa. */
type Chosen = { item: PackageResult; groups: GroupRef[]; error?: string }

type Props = {
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Depois de adicionar: a tela recarrega a lista (o diálogo continua aberto para adicionar outros). */
  onAdded: () => void
}

const keyOf = (item: PackageResult) => `${item.source}:${item.packageId}`
const groupNames = (groups: GroupRef[]) => groups.map((group) => (group.name === '*' && !group.sid ? 'todos os usuários' : group.name))

function AddForm({ onAdded }: { onAdded: () => void }) {
  const [source, setSource] = useState<Source>('community')
  const [term, setTerm] = useState('')
  const [results, setResults] = useState<Results>({ state: 'idle' })
  // Vários aplicativos de uma vez. Os grupos valem POR APLICATIVO, a menos que a pessoa marque "o mesmo grupo para todos".
  const [chosen, setChosen] = useState<Record<string, Chosen>>({})
  const [sameGroup, setSameGroup] = useState(false)
  const [shared, setShared] = useState<GroupRef[]>([])
  const [showErrors, setShowErrors] = useState(false)
  const [adding, setAdding] = useState(false)
  const round = useRef(0)

  const list = Object.entries(chosen)
  const groupsOf = (entry: Chosen) => (sameGroup ? shared : entry.groups)
  const ready = list.filter(([, entry]) => groupsOf(entry).length > 0)

  async function search(from: Source = source, text: string = term) {
    const query = text.trim()
    if (!query && from === 'community') {
      setResults({ state: 'error', message: 'Digite o nome do programa. Por exemplo: 7zip, chrome ou notepad++.' })
      return
    }
    const mine = ++round.current
    setResults({ state: 'loading' })
    try {
      const items = await api<PackageResult[]>(`/search?q=${encodeURIComponent(query)}&source=${from}`)
      if (mine === round.current) setResults({ state: 'ok', items })
    } catch (e) {
      if (mine === round.current) setResults({ state: 'error', message: messageOf(e) })
    }
  }

  function changeSource(next: Source) {
    setSource(next)
    setResults({ state: 'idle' })
    // A pasta interna é pequena: já mostra tudo o que há nela.
    if (next === 'internal') void search(next, term)
  }

  function toggle(item: PackageResult, on: boolean) {
    setChosen((current) => {
      const next = { ...current }
      if (on) next[keyOf(item)] = { item, groups: [] }
      else delete next[keyOf(item)]
      return next
    })
  }

  function setGroups(key: string, groups: GroupRef[]) {
    setChosen((current) => (current[key] ? { ...current, [key]: { ...current[key], groups, error: undefined } } : current))
  }

  async function addAll() {
    setShowErrors(true)
    if (ready.length === 0) return
    setAdding(true)
    let added = 0
    const failed: string[] = []
    for (const [key, entry] of ready) {
      const groups = groupsOf(entry)
      try {
        await api<AdminApp>('/apps', {
          method: 'POST',
          body: { packageId: entry.item.packageId, source: entry.item.source, groups: groups.map((group) => group.name), groupRefs: groups },
        })
        added++
        setChosen((current) => {
          const next = { ...current }
          delete next[key]
          return next
        })
        setResults((current) =>
          current.state === 'ok' ? { state: 'ok', items: current.items.map((item) => (keyOf(item) === key ? { ...item, inStore: true } : item)) } : current,
        )
      } catch (e) {
        failed.push(entry.item.title)
        setChosen((current) => (current[key] ? { ...current, [key]: { ...current[key], error: messageOf(e) } } : current))
      }
    }
    setAdding(false)
    if (added > 0) {
      onAdded()
      toast.success(`${plural(added, 'aplicativo adicionado', 'aplicativos adicionados')} à loja.`)
    }
    if (failed.length > 0) toast.error(`Não foi possível adicionar: ${failed.join(', ')}. Veja a mensagem em cada um.`)
    if (added > 0 && failed.length === 0 && list.length === ready.length) setShowErrors(false)
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Adicionar aplicativos</DialogTitle>
        <DialogDescription>
          Busque no Chocolatey (internet) ou na pasta interna do NAS, marque os programas e escolha quem pode instalar cada um. Nome, versão e ícone vêm do repositório e se atualizam sozinhos.
        </DialogDescription>
      </DialogHeader>

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(event) => {
          event.preventDefault()
          void search()
        }}
      >
        <div className="grid gap-1.5">
          <Label htmlFor="add-origem">Origem</Label>
          <Select value={source} onValueChange={(value) => changeSource(value as Source)}>
            <SelectTrigger id="add-origem" className="w-52">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="community">Internet (Chocolatey)</SelectItem>
              <SelectItem value="internal">Interno (pasta no NAS)</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div className="grid min-w-56 flex-1 gap-1.5">
          <Label htmlFor="add-busca">Nome do programa</Label>
          <Input id="add-busca" type="search" value={term} onChange={(event) => setTerm(event.target.value)} placeholder="Por exemplo: 7zip, chrome, notepad++" />
        </div>
        <Button type="submit" variant="secondary" loading={results.state === 'loading'}>
          <SearchIcon aria-hidden="true" />
          Buscar
        </Button>
      </form>

      <div aria-live="polite">
        {results.state === 'loading' ? (
          <div className="grid gap-2" aria-busy="true">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : null}
        {results.state === 'error' ? (
          <Alert variant="destructive">
            <AlertDescription>{results.message}</AlertDescription>
          </Alert>
        ) : null}
        {results.state === 'ok' && results.items.length === 0 ? (
          <div className="rounded-lg border">
            <EmptyState icon={SearchIcon} title="Nenhum pacote encontrado" description="Confira o nome e tente de novo. O Chocolatey procura por nome e por id do pacote." />
          </div>
        ) : null}
        {results.state === 'ok' && results.items.length > 0 ? (
          <div className="max-h-64 overflow-auto rounded-lg border">
            <Table>
              <TableHeader className="sticky top-0 z-10">
                <TableRow className="hover:bg-transparent">
                  <TableHead className="w-10">
                    <span className="sr-only">Marcar</span>
                  </TableHead>
                  <TableHead>Programa</TableHead>
                  <TableHead>Versão</TableHead>
                  <TableHead>Downloads</TableHead>
                  <TableHead>Aprovado</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {results.items.map((item) => {
                  const on = keyOf(item) in chosen
                  return (
                    <TableRow key={keyOf(item)} data-state={on ? 'selected' : undefined}>
                      <TableCell>
                        {item.inStore ? (
                          <Badge variant="muted" className="-ml-1">
                            Na loja
                          </Badge>
                        ) : (
                          <Checkbox checked={on} onCheckedChange={(checked) => toggle(item, checked === true)} aria-label={`Marcar ${item.title}`} />
                        )}
                      </TableCell>
                      <TableCell className="min-w-64 whitespace-normal">
                        <strong>{item.title}</strong>
                        <div className="font-mono text-xs text-muted-foreground">{item.packageId}</div>
                        {item.description ? (
                          <div className="line-clamp-2 text-xs text-muted-foreground" title={item.description}>
                            {item.description}
                          </div>
                        ) : null}
                      </TableCell>
                      <TableCell className="font-mono text-[12.5px]">{item.version ?? '—'}</TableCell>
                      <TableCell>{item.source === 'internal' ? '—' : item.downloadCount.toLocaleString('pt-BR')}</TableCell>
                      <TableCell>{item.source === 'internal' ? '—' : item.isApproved ? <Badge variant="success">Sim</Badge> : <Badge variant="warning">Não</Badge>}</TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </div>
        ) : null}
        {results.state === 'ok' && results.items.length > 0 ? <p className="mt-1.5 text-xs text-muted-foreground">{plural(results.items.length, 'pacote encontrado', 'pacotes encontrados')}.</p> : null}
      </div>

      {list.length > 0 ? (
        <section aria-label="Quem pode instalar" className="grid gap-3 rounded-lg border bg-muted/40 p-3">
          <h3 className="font-medium">Quem pode instalar ({plural(list.length, 'aplicativo marcado', 'aplicativos marcados')})</h3>

          {list.length > 1 ? (
            <label className="flex items-center gap-2 text-sm">
              <Checkbox checked={sameGroup} onCheckedChange={(checked) => setSameGroup(checked === true)} />
              Aplicar o mesmo grupo a todos os selecionados
            </label>
          ) : null}

          {sameGroup ? (
            <div className="grid gap-1.5">
              <GroupSelect value={shared} onChange={setShared} aria-label="Grupos que podem instalar todos os aplicativos marcados" />
              {showErrors && shared.length === 0 ? (
                <p role="alert" className="text-xs font-medium text-destructive">
                  Escolha ao menos um grupo, ou “Todos os usuários”.
                </p>
              ) : null}
            </div>
          ) : null}

          <ul className="grid gap-2.5">
            {list.map(([key, entry]) => {
              const groups = groupsOf(entry)
              const empty = showErrors && groups.length === 0
              return (
                <li key={key} className="grid gap-1.5 rounded-lg border bg-card p-2.5">
                  <div className="flex items-start justify-between gap-2">
                    <p>
                      <strong>{entry.item.title}</strong>
                      {entry.item.version ? <span className="text-muted-foreground"> (versão {entry.item.version})</span> : null}
                    </p>
                    <Button variant="ghost" size="icon-xs" aria-label={`Tirar ${entry.item.title} da seleção`} onClick={() => toggle(entry.item, false)}>
                      <XIcon aria-hidden="true" />
                    </Button>
                  </div>
                  {sameGroup ? (
                    <p className="text-xs text-muted-foreground">{groups.length ? `Quem pode instalar: ${groupNames(groups).join(', ')}.` : 'Sem grupo ainda.'}</p>
                  ) : (
                    <>
                      <GroupSelect value={entry.groups} onChange={(next) => setGroups(key, next)} aria-label={`Grupos que podem instalar ${entry.item.title}`} />
                      {empty ? (
                        <p role="alert" className="text-xs font-medium text-destructive">
                          Escolha ao menos um grupo, ou “Todos os usuários”.
                        </p>
                      ) : null}
                    </>
                  )}
                  {entry.error ? (
                    <p role="alert" className="text-xs font-medium text-destructive">
                      {entry.error}
                    </p>
                  ) : null}
                </li>
              )
            })}
          </ul>

          <div className="flex flex-wrap items-center justify-between gap-3">
            <span className="text-xs text-muted-foreground">
              {ready.length === list.length
                ? list.length === 1
                  ? 'Grupo definido.'
                  : 'Todos têm grupo definido.'
                : ready.length === 0
                  ? `${plural(list.length, 'aplicativo ainda sem grupo', 'aplicativos ainda sem grupo')}. Escolha o grupo de cada um para poder adicionar.`
                  : `${plural(list.length - ready.length, 'aplicativo ainda sem grupo', 'aplicativos ainda sem grupo')}: ${ready.length === 1 ? 'só o que tem grupo será adicionado' : 'só os que têm grupo serão adicionados'}.`}
            </span>
            <Button loading={adding} onClick={addAll}>
              {list.length === 1 ? 'Adicionar à loja' : `Adicionar ${ready.length} de ${list.length}`}
            </Button>
          </div>
        </section>
      ) : null}
    </>
  )
}

export function AddAppDialog({ open, onOpenChange, onAdded }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-3xl">{open ? <AddForm onAdded={onAdded} /> : null}</DialogContent>
    </Dialog>
  )
}
