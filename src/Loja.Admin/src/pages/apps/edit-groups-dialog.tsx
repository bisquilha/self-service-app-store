import { useState } from 'react'
import { MinusIcon, PlusIcon } from 'lucide-react'
import { GroupSelect, groupKey, sameGroupSet } from '@/components/group-select'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { api, messageOf } from '@/lib/api'
import type { AdminApp, GroupRef } from '@/lib/types'

type Props = {
  app: AdminApp | null
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Depois de salvar: a tela recarrega a lista e avisa com "Desfazer". `before` são os grupos de antes. */
  onSaved: (app: AdminApp, before: GroupRef[]) => void
}

const label = (group: GroupRef) => (group.name === '*' && !group.sid ? 'Todos os usuários' : group.name)

/** O formulário fica dentro do conteúdo do diálogo: a cada abertura ele nasce de novo, com os grupos salvos. */
function EditForm({ app, close, onSaved }: { app: AdminApp; close: () => void; onSaved: Props['onSaved'] }) {
  const [groups, setGroups] = useState<GroupRef[]>(app.groupRefs)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const missing = (app.missingGroups ?? []).filter((name) => groups.some((group) => group.name.toLowerCase() === name.toLowerCase()))

  const before = new Set(app.groupRefs.map(groupKey))
  const after = new Set(groups.map(groupKey))
  const added = groups.filter((group) => !before.has(groupKey(group)))
  const removed = app.groupRefs.filter((group) => !after.has(groupKey(group)))

  async function save() {
    setSaving(true)
    setError(null)
    try {
      await api(`/apps/${app.id}`, {
        method: 'PUT',
        body: { groups: groups.map((group) => group.name), groupRefs: groups, enabled: app.enabled, autoUpdate: app.autoUpdate, allowUninstall: app.allowUninstall },
      })
      onSaved(app, app.groupRefs)
      close()
    } catch (e) {
      // O servidor recusa grupo que não existe no AD e diz qual é.
      setError(messageOf(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Grupos do AD</DialogTitle>
        <DialogDescription>
          Quem está nestes grupos vê <strong>{app.title}</strong> na loja e pode instalar. A TI (LOJA-Admins) vê todos os aplicativos.
        </DialogDescription>
      </DialogHeader>

      <div className="grid gap-3">
        <section aria-label="Antes" className="grid gap-1.5">
          <h3 className="text-[12.5px] font-medium text-muted-foreground">Antes (salvo hoje)</h3>
          <div className="flex flex-wrap gap-1.5">
            {app.groupRefs.map((group) => (
              <Chip key={groupKey(group)} tone={(app.missingGroups ?? []).some((name) => name.toLowerCase() === group.name.toLowerCase()) ? 'warning' : 'default'}>
                {label(group)}
              </Chip>
            ))}
          </div>
        </section>

        <section aria-label="Depois" className="grid gap-1.5">
          <h3 className="text-[12.5px] font-medium text-muted-foreground">Depois (o que será salvo)</h3>
          <GroupSelect value={groups} onChange={setGroups} missing={app.missingGroups ?? []} aria-label={`Grupos de ${app.title}`} />
          {missing.length ? (
            <p role="note" className="text-xs font-medium text-warning">
              Os grupos em âmbar não existem mais no AD, então não dão acesso a ninguém. Tire-os da lista.
            </p>
          ) : null}
          {groups.length === 0 ? <p className="text-xs text-muted-foreground">Escolha ao menos um grupo, ou "Todos os usuários".</p> : null}
        </section>

        {added.length + removed.length > 0 ? (
          <ul aria-label="O que muda" className="grid gap-1 rounded-lg border bg-muted/40 p-2.5 text-[13px]">
            {added.map((group) => (
              <li key={`mais-${groupKey(group)}`} className="flex items-center gap-1.5 text-success">
                <PlusIcon className="size-3.5 shrink-0" aria-hidden="true" />
                <span>
                  Passa a liberar para <strong>{label(group)}</strong>
                </span>
              </li>
            ))}
            {removed.map((group) => (
              <li key={`menos-${groupKey(group)}`} className="flex items-center gap-1.5 text-destructive">
                <MinusIcon className="size-3.5 shrink-0" aria-hidden="true" />
                <span>
                  Deixa de liberar para <strong>{label(group)}</strong>
                </span>
              </li>
            ))}
          </ul>
        ) : null}
      </div>

      {error ? (
        <Alert variant="destructive">
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      ) : null}

      <DialogFooter>
        <Button variant="secondary" disabled={saving} onClick={close}>
          Cancelar
        </Button>
        <Button loading={saving} disabled={groups.length === 0 || sameGroupSet(groups, app.groupRefs)} onClick={save}>
          Salvar grupos
        </Button>
      </DialogFooter>
    </>
  )
}

export function EditGroupsDialog({ app, open, onOpenChange, onSaved }: Props) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">{app ? <EditForm key={app.id} app={app} close={() => onOpenChange(false)} onSaved={onSaved} /> : null}</DialogContent>
    </Dialog>
  )
}
