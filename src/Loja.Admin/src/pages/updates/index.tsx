import { Fragment, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ChevronRightIcon, CircleCheckIcon, RefreshCwIcon } from 'lucide-react'
import { AppIcon } from '@/components/app-icon'
import { PageHeader } from '@/components/shell/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { useApi } from '@/hooks/use-api'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'
import type { UpdateApp, UpdateReach, UpgradeAllResult } from '@/lib/types'

const REACH: Record<UpdateReach, { label: string; variant: 'success' | 'muted' | 'warning' }> = {
  ready: { label: 'Online: entra agora', variant: 'success' },
  offline: { label: 'Offline: fica para depois', variant: 'muted' },
  blocked: { label: 'Servidor: a loja não age', variant: 'muted' },
  duplicate: { label: 'Nome repetido no Tactical', variant: 'warning' },
  missing: { label: 'Não está no Tactical', variant: 'warning' },
  unknown: { label: 'Tactical não respondeu', variant: 'warning' },
}

/** "1.0 (2), 1.1 (1)": quantas máquinas estão em cada versão antiga. */
function versionsOf(app: UpdateApp): { version: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const machine of app.machines) counts.set(machine.installedVersion, (counts.get(machine.installedVersion) ?? 0) + 1)
  return [...counts.entries()].map(([version, count]) => ({ version, count })).sort((a, b) => b.count - a.count)
}

const readyOf = (app: UpdateApp) => app.machines.filter((machine) => machine.reach === 'ready')

type Skipped = { title: string; messages: string[] }

/** Confirmação com o alcance explícito: quais máquinas entram agora e quais ficam de fora, e por quê. */
function UpgradeAllDialog({ app, onClose, onDone }: { app: UpdateApp | null; onClose: () => void; onDone: (app: UpdateApp, result: UpgradeAllResult) => void }) {
  const shown = useLastValue(app)
  const [pending, setPending] = useState(false)

  async function run() {
    if (!shown) return
    setPending(true)
    try {
      const result = await api<UpgradeAllResult>(`/apps/${shown.appId}/upgrade-all`, { method: 'POST' })
      onDone(shown, result)
      onClose()
    } catch (error) {
      toast.error(messageOf(error))
    } finally {
      setPending(false)
    }
  }

  const ready = shown ? readyOf(shown) : []
  const left = shown ? shown.machines.filter((machine) => machine.reach !== 'ready') : []

  return (
    <Dialog open={app !== null} onOpenChange={(open) => (!open && !pending ? onClose() : undefined)}>
      <DialogContent showCloseButton={false} className="sm:max-w-lg">
        {shown ? (
          <>
            <DialogHeader>
              <DialogTitle>Atualizar {shown.title} em todas as máquinas</DialogTitle>
              <DialogDescription>
                {ready.length > 0
                  ? `Envia a atualização de ${[...new Set(ready.map((machine) => machine.installedVersion))].join(', ')} para ${shown.latestVersion} às máquinas online abaixo.`
                  : `Nenhuma máquina desta lista está online agora, então nada seria enviado.`}{' '}
                Cada máquina faz uma ação por vez; as que estiverem ocupadas ficam de fora desta rodada.
              </DialogDescription>
            </DialogHeader>

            <section aria-label="Máquinas que entram agora" className="grid gap-1">
              <h3 className="text-[12.5px] font-medium text-muted-foreground">Entram agora ({ready.length})</h3>
              {ready.length > 0 ? (
                <ul className="flex max-h-32 flex-wrap gap-1.5 overflow-auto">
                  {ready.map((machine) => (
                    <li key={machine.hostname}>
                      <Badge variant="success" className="font-mono">
                        {machine.hostname} · {machine.installedVersion}
                      </Badge>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-sm text-muted-foreground">Nenhuma máquina ao alcance agora.</p>
              )}
            </section>

            {left.length > 0 ? (
              <section aria-label="Máquinas que ficam de fora" className="grid gap-1">
                <h3 className="text-[12.5px] font-medium text-muted-foreground">Ficam de fora ({left.length})</h3>
                <ul className="grid max-h-36 gap-1 overflow-auto text-sm">
                  {left.map((machine) => (
                    <li key={machine.hostname} className="flex flex-wrap items-center gap-x-2">
                      <span className="font-mono">{machine.hostname}</span>
                      <span className="text-muted-foreground">{REACH[machine.reach].label}</span>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}

            <p className="text-xs text-muted-foreground">
              A loja nunca age em servidores. Fica registrado na auditoria quem pediu e quantas atualizações foram enviadas.
            </p>

            <DialogFooter>
              <Button variant="secondary" disabled={pending} onClick={onClose}>
                Cancelar
              </Button>
              <Button loading={pending} disabled={ready.length === 0} onClick={() => void run()}>
                {ready.length === 0 ? 'Atualizar' : `Atualizar ${plural(ready.length, 'máquina', 'máquinas')}`}
              </Button>
            </DialogFooter>
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

/**
 * Atualizações disponíveis: aplicativos com máquinas em versão menor que a da loja. Os números vêm do último inventário de cada máquina.
 * "Atualizar em todas" age só nas máquinas da lista (e só nas que passam nas travas: online, estação, sem outra ação rodando).
 */
export function UpdatesPage() {
  const updates = useApi<UpdateApp[]>('/updates')
  const [open, setOpen] = useState<Set<number>>(new Set())
  const [target, setTarget] = useState<UpdateApp | null>(null)
  const [skipped, setSkipped] = useState<Skipped | null>(null)
  const shownSkipped = useLastValue(skipped)

  const rows = updates.data ?? []
  const totalMachines = useMemo(() => rows.reduce((sum, app) => sum + app.machines.length, 0), [rows])
  const failed = updates.error && !updates.data

  function toggle(id: number) {
    setOpen((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function done(app: UpdateApp, result: UpgradeAllResult) {
    toast(`${plural(result.started, 'atualização enviada', 'atualizações enviadas')}, ${plural(result.skipped, 'máquina pulada', 'máquinas puladas')}.`, { duration: 7000 })
    if (result.messages.length) setTimeout(() => setSkipped({ title: app.title, messages: result.messages }), 300)
    updates.reload()
  }

  return (
    <div className="grid gap-4">
      <PageHeader
        title="Atualizações"
        actions={
          <Button variant="secondary" onClick={updates.reload} loading={updates.loading && updates.data !== null}>
            <RefreshCwIcon aria-hidden="true" />
            Atualizar lista
          </Button>
        }
      />

      <p className="max-w-3xl text-sm text-muted-foreground">
        Aplicativos com máquinas em versão menor que a da loja, segundo a última verificação de cada máquina. “Atualizar em todas” envia o pedido só para as máquinas desta lista, e só para as que estiverem online (estações, nunca servidores).
      </p>

      {failed ? (
        <div className="rounded-lg border bg-card">
          <ErrorState message={updates.error!} onRetry={updates.reload} />
        </div>
      ) : (
        <>
          <p aria-live="polite" className="text-sm text-muted-foreground">
            {updates.data
              ? rows.length === 0
                ? 'Nenhuma atualização pendente.'
                : `${plural(rows.length, 'aplicativo')} com ${plural(totalMachines, 'máquina atrasada', 'máquinas atrasadas')}`
              : 'Carregando…'}
          </p>
          <div className="overflow-hidden rounded-lg border bg-card">
            <Table aria-busy={updates.loading && !updates.data}>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <TableHead className="w-10">
                    <span className="sr-only">Detalhes</span>
                  </TableHead>
                  <TableHead>Aplicativo</TableHead>
                  <TableHead>Instalada</TableHead>
                  <TableHead>Versão da loja</TableHead>
                  <TableHead>Máquinas atrasadas</TableHead>
                  <TableHead className="w-48">
                    <span className="sr-only">Ação</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              {updates.loading && !updates.data ? (
                <TableSkeleton columns={6} rows={4} />
              ) : (
                <TableBody>
                  {rows.map((app) => {
                    const expanded = open.has(app.appId)
                    const ready = readyOf(app).length
                    return (
                      <Fragment key={app.appId}>
                        <TableRow>
                          <TableCell className="pr-0">
                            <Button
                              variant="ghost"
                              size="icon-xs"
                              aria-expanded={expanded}
                              aria-label={`${expanded ? 'Esconder' : 'Ver'} as máquinas atrasadas de ${app.title}`}
                              onClick={() => toggle(app.appId)}
                            >
                              <ChevronRightIcon className={expanded ? 'rotate-90 transition-transform' : 'transition-transform'} aria-hidden="true" />
                            </Button>
                          </TableCell>
                          <TableCell>
                            <div className="flex min-w-48 items-center gap-2.5">
                              <AppIcon id={app.appId} title={app.title} />
                              <div className="grid min-w-0">
                                <span className="flex flex-wrap items-center gap-1.5">
                                  <strong>{app.title}</strong>
                                  {app.enabled ? null : <Badge variant="muted">Desativado</Badge>}
                                  {app.autoUpdate ? <Badge variant="info">Atualiza sozinho</Badge> : null}
                                </span>
                                <span className="font-mono text-xs text-muted-foreground">{app.packageId}</span>
                              </div>
                            </div>
                          </TableCell>
                          <TableCell>
                            <div className="flex flex-wrap gap-1.5">
                              {versionsOf(app).map((v) => (
                                <Badge key={v.version} variant="muted" className="font-mono" title={`${plural(v.count, 'máquina')} em ${v.version}`}>
                                  {v.count}× {v.version}
                                  <span className="sr-only"> ({plural(v.count, 'máquina')})</span>
                                </Badge>
                              ))}
                            </div>
                          </TableCell>
                          <TableCell className="font-mono text-[12.5px]">{app.latestVersion}</TableCell>
                          <TableCell>
                            <div className="grid">
                              <strong>{app.machines.length}</strong>
                              <span className="text-xs text-muted-foreground">{ready === app.machines.length ? 'todas online' : `${ready} online agora`}</span>
                            </div>
                          </TableCell>
                          <TableCell className="text-right">
                            <Button
                              size="sm"
                              onClick={() => setTarget(app)}
                              disabled={ready === 0}
                              title={ready === 0 ? 'Nenhuma máquina ao alcance agora' : undefined}
                              aria-label={`Atualizar ${app.title} em todas as máquinas atrasadas`}
                            >
                              Atualizar em todas
                            </Button>
                          </TableCell>
                        </TableRow>
                        {expanded ? (
                          <TableRow className="bg-muted/30 hover:bg-muted/30">
                            <TableCell />
                            <TableCell colSpan={5}>
                              <ul aria-label={`Máquinas atrasadas de ${app.title}`} className="grid gap-1.5 py-1 sm:grid-cols-2">
                                {app.machines.map((machine) => (
                                  <li key={machine.hostname} className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
                                    <Link to={`/maquinas/${encodeURIComponent(machine.hostname)}`} className="font-mono underline-offset-4 hover:underline focus-visible:underline">
                                      {machine.hostname}
                                    </Link>
                                    <span className="font-mono text-xs text-muted-foreground">
                                      {machine.installedVersion} → {app.latestVersion}
                                    </span>
                                    <Badge variant={REACH[machine.reach].variant}>{REACH[machine.reach].label}</Badge>
                                  </li>
                                ))}
                              </ul>
                            </TableCell>
                          </TableRow>
                        ) : null}
                      </Fragment>
                    )
                  })}
                </TableBody>
              )}
            </Table>
            {updates.data && rows.length === 0 ? (
              <EmptyState icon={CircleCheckIcon} title="Tudo em dia" description="Nenhuma máquina tem versão menor que a da loja. A lista vem da última verificação de cada máquina." />
            ) : null}
          </div>
        </>
      )}

      <UpgradeAllDialog app={target} onClose={() => setTarget(null)} onDone={done} />

      <Dialog open={skipped !== null} onOpenChange={(next) => !next && setSkipped(null)}>
        <DialogContent>
          {shownSkipped ? (
            <>
              <DialogHeader>
                <DialogTitle>Máquinas puladas</DialogTitle>
                <DialogDescription>Estas máquinas ficaram de fora da atualização de {shownSkipped.title}. Elas entram na próxima.</DialogDescription>
              </DialogHeader>
              <ul className="grid max-h-64 gap-1.5 overflow-auto pl-5 text-sm [list-style:disc]">
                {shownSkipped.messages.map((message) => (
                  <li key={message}>{message}</li>
                ))}
              </ul>
              <DialogFooter>
                <Button onClick={() => setSkipped(null)}>Entendi</Button>
              </DialogFooter>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  )
}
