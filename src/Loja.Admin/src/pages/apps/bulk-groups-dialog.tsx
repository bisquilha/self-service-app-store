import { useMemo, useState } from 'react'
import { GroupSelect, groupKey, groupLabel, isEveryone } from '@/components/group-select'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'
import type { BulkGroupsResult, GroupRef } from '@/lib/types'
import type { AppRow } from './apps-table'

export type BulkMode = 'add' | 'remove'

type Props = {
  mode: BulkMode | null
  apps: AppRow[]
  onOpenChange: (open: boolean) => void
  /** Depois de salvar: a tela recarrega, avisa com "Desfazer" e limpa a seleção. */
  onDone: (result: BulkGroupsResult, summary: string) => void
}

const sameKey = (a: GroupRef, b: GroupRef) => groupKey(a).toLowerCase() === groupKey(b).toLowerCase()

/** O aplicativo muda se receber algum grupo novo. Quem já é de "Todos" não recebe grupo específico; "Todos" troca os grupos por ele. */
function changesOnAdd(app: AppRow, add: GroupRef[]): boolean {
  if (add.some(isEveryone)) return !(app.groupRefs.length === 1 && isEveryone(app.groupRefs[0]))
  if (app.groupRefs.some(isEveryone)) return false
  return add.some((group) => !app.groupRefs.some((own) => sameKey(own, group)))
}

function list(names: string[], max = 4): string {
  return names.length <= max ? names.join(', ') : `${names.slice(0, max).join(', ')} e mais ${names.length - max}`
}

function Form({ mode, apps, close, onDone }: { mode: BulkMode; apps: AppRow[]; close: () => void; onDone: Props['onDone'] }) {
  const [adding, setAdding] = useState<GroupRef[]>([])
  const [chosen, setChosen] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Remover: os grupos que aparecem em algum dos aplicativos escolhidos, com a contagem. "Todos" não tem o que tirar.
  const removable = useMemo(() => {
    const byKey = new Map<string, { ref: GroupRef; count: number }>()
    for (const app of apps) {
      for (const group of app.groupRefs) {
        if (isEveryone(group)) continue
        const key = groupKey(group).toLowerCase()
        const entry = byKey.get(key)
        if (entry) entry.count++
        else byKey.set(key, { ref: group, count: 1 })
      }
    }
    return [...byKey.entries()].map(([key, value]) => ({ key, ...value })).sort((a, b) => a.ref.name.localeCompare(b.ref.name, 'pt-BR'))
  }, [apps])

  const everyoneApps = apps.filter((app) => app.groupRefs.some(isEveryone))
  const removeRefs = removable.filter((item) => chosen.has(item.key)).map((item) => item.ref)

  const willChange = mode === 'add' ? apps.filter((app) => changesOnAdd(app, adding)) : apps.filter((app) => app.groupRefs.some((own) => removeRefs.some((group) => sameKey(own, group))))
  const emptied = mode === 'remove' ? willChange.filter((app) => app.groupRefs.every((own) => removeRefs.some((group) => sameKey(own, group)))) : []
  const replaced = mode === 'add' && adding.some(isEveryone) ? apps.filter((app) => changesOnAdd(app, adding) && app.groupRefs.length > 0) : []
  const same = apps.length - willChange.length

  const noun = (n: number) => plural(n, 'aplicativo', 'aplicativos')
  const ready = mode === 'add' ? adding.length > 0 && willChange.length > 0 : removeRefs.length > 0 && willChange.length > 0 && emptied.length === 0

  async function save() {
    setSaving(true)
    setError(null)
    try {
      const result = await api<BulkGroupsResult>('/apps/bulk-groups', {
        method: 'POST',
        body: { appIds: apps.map((app) => app.id), add: mode === 'add' ? adding : undefined, remove: mode === 'remove' ? removeRefs : undefined },
      })
      const groups = mode === 'add' ? adding : removeRefs
      const what = groups.length === 1 ? `Grupo “${groupLabel(groups[0])}”` : plural(groups.length, 'grupo', 'grupos')
      const verb =
        mode === 'add'
          ? `${groups.length === 1 ? 'acrescentado' : 'acrescentados'} a ${noun(result.updated.length)}`
          : `${groups.length === 1 ? 'tirado' : 'tirados'} de ${noun(result.updated.length)}`
      const rest = result.unchanged.length ? ` ${result.unchanged.length === 1 ? '1 já estava assim' : `${result.unchanged.length} já estavam assim`}.` : ''
      onDone(result, result.updated.length ? `${what} ${verb}.${rest}` : `Nada mudou: ${noun(apps.length)} já ${apps.length === 1 ? 'estava' : 'estavam'} assim.`)
      close()
    } catch (e) {
      // Nada é salvo pela metade: o servidor recusa tudo e diz o motivo (grupo que não existe no AD, aplicativo sem grupo…).
      setError(messageOf(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>{mode === 'add' ? 'Adicionar grupo' : 'Remover grupo'}</DialogTitle>
        <DialogDescription>
          {mode === 'add' ? 'Acrescenta' : 'Tira'} grupos do AD em {noun(apps.length)} de uma vez. Ou tudo é salvo, ou nada muda.
        </DialogDescription>
      </DialogHeader>

      <p className="text-sm text-muted-foreground">
        {plural(apps.length, 'selecionado', 'selecionados')}: <span className="text-foreground">{list(apps.map((app) => app.title))}</span>
      </p>

      {mode === 'add' ? (
        <div className="grid gap-1.5">
          <h3 className="text-[12.5px] font-medium text-muted-foreground">Grupos a acrescentar</h3>
          <GroupSelect value={adding} onChange={setAdding} aria-label="Grupos a acrescentar" />
          {adding.length === 0 ? <p className="text-xs text-muted-foreground">Escolha ao menos um grupo.</p> : null}
        </div>
      ) : (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-[12.5px] font-medium text-muted-foreground">Grupos a tirar</legend>
          {removable.length === 0 ? (
            <p className="text-sm text-muted-foreground">Os aplicativos escolhidos são liberados para Todos os usuários e não têm grupo específico para tirar.</p>
          ) : (
            <ul className="grid max-h-56 gap-1 overflow-auto rounded-lg border p-2">
              {removable.map((item) => (
                <li key={item.key}>
                  <label className="flex items-center gap-2 rounded-md px-1.5 py-1 text-sm hover:bg-muted">
                    <Checkbox
                      checked={chosen.has(item.key)}
                      onCheckedChange={(checked) =>
                        setChosen((previous) => {
                          const next = new Set(previous)
                          if (checked === true) next.add(item.key)
                          else next.delete(item.key)
                          return next
                        })
                      }
                    />
                    <span className="min-w-0 flex-1 truncate font-medium">{item.ref.name}</span>
                    <span className="shrink-0 text-xs text-muted-foreground">
                      em {item.count} de {apps.length}
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          )}
          {everyoneApps.length > 0 && removable.length > 0 ? (
            <p className="text-xs text-muted-foreground">{plural(everyoneApps.length, 'aplicativo é liberado', 'aplicativos são liberados')} para Todos os usuários e não muda{everyoneApps.length === 1 ? '' : 'm'}.</p>
          ) : null}
        </fieldset>
      )}

      {emptied.length > 0 ? (
        <Alert variant="destructive">
          <AlertDescription>
            Isso deixaria {emptied.length === 1 ? 'o aplicativo' : 'os aplicativos'} {list(emptied.map((app) => `“${app.title}”`))} sem nenhum grupo, e ninguém mais {emptied.length === 1 ? 'o' : 'os'} veria na loja. Tire menos grupos ou tire estes aplicativos da seleção.
          </AlertDescription>
        </Alert>
      ) : null}

      {replaced.length > 0 ? (
        <p role="note" className="text-xs font-medium text-warning">
          “Todos os usuários” libera para qualquer pessoa e substitui os grupos de {noun(replaced.length)}.
        </p>
      ) : null}

      {(mode === 'add' ? adding.length > 0 : removeRefs.length > 0) && emptied.length === 0 ? (
        <p aria-live="polite" className="rounded-lg border bg-muted/40 p-2.5 text-[13px]">
          {willChange.length === 0
            ? mode === 'add'
              ? 'Nenhum aplicativo muda: todos já têm esse acesso.'
              : 'Nenhum aplicativo muda: nenhum deles usa o grupo escolhido.'
            : `${plural(willChange.length, 'aplicativo muda', 'aplicativos mudam')}${same > 0 ? `; ${same === 1 ? '1 não muda' : `${same} não mudam`}` : ''}.`}
        </p>
      ) : null}

      {error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}

      <DialogFooter>
        <Button variant="secondary" disabled={saving} onClick={close}>
          Cancelar
        </Button>
        <Button loading={saving} disabled={!ready} onClick={save}>
          {willChange.length === 0 ? (mode === 'add' ? 'Acrescentar' : 'Tirar') : mode === 'add' ? `Acrescentar a ${noun(willChange.length)}` : `Tirar de ${noun(willChange.length)}`}
        </Button>
      </DialogFooter>
    </>
  )
}

export function BulkGroupsDialog({ mode, apps, onOpenChange, onDone }: Props) {
  // O formulário nasce de novo a cada abertura (chave = modo) e continua montado durante a animação de fechar.
  const shown = useLastValue(mode)
  // Depois de salvar a seleção é limpa; o texto do diálogo continua com os aplicativos de antes até ele sumir.
  const shownApps = useLastValue(apps.length ? apps : null) ?? apps
  return (
    <Dialog open={mode !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">{shown ? <Form key={shown} mode={shown} apps={shownApps} close={() => onOpenChange(false)} onDone={onDone} /> : null}</DialogContent>
    </Dialog>
  )
}
