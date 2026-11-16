import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { AppIcon } from '@/components/app-icon'
import { RequestsTable } from '@/components/requests/requests-table'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusDot } from '@/components/ui/status-dot'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useApi } from '@/hooks/use-api'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { formatAgo, formatDateTime, plural, shortLogin, shortOs } from '@/lib/format'
import { ACTION_LABELS } from '@/lib/requests'
import type { MachineDetail, StoreAppState } from '@/lib/types'
import { cn } from '@/lib/utils'

type Action = 'install' | 'upgrade' | 'uninstall' | 'list'
type Pending = { kind: 'upgrade' | 'uninstall'; app: StoreAppState }

const STATUS_TEXT: Record<string, string> = { online: 'online', offline: 'offline', overdue: 'sem contato' }
const STATUS_TONE = { online: 'success', overdue: 'warning', offline: 'muted' } as const

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid content-start gap-0.5">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="break-words">{children}</dd>
    </div>
  )
}

/** Como o aplicativo está nesta máquina: o servidor decide o estado (versionState) e aqui só se escreve, sempre em palavras. */
function InstalledCell({ app }: { app: StoreAppState }) {
  const state = app.versionState ?? (!app.installedVersion ? 'none' : app.updateAvailable ? 'update' : 'current')
  if (state === 'external') {
    return (
      <span className="inline-flex flex-wrap items-center gap-1.5" title="Veio de fora da loja: o nome do programa no Windows está cadastrado neste aplicativo. Atualizar e desinstalar ficam de fora.">
        <Badge variant="info">Fora da loja</Badge>
        <span className="font-mono text-[12.5px]">{app.externalVersion ?? 'sem versão'}</span>
      </span>
    )
  }
  if (!app.installedVersion) return <span className="text-muted-foreground">não instalado</span>
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className="font-mono text-[12.5px]">{app.installedVersion}</span>
      {state === 'update' ? (
        <Badge variant="warning">desatualizada</Badge>
      ) : state === 'newer' ? (
        <Badge variant="muted" title="A versão instalada é maior que a da loja: não será trocada">
          mais nova que a loja
        </Badge>
      ) : state === 'unknown' ? (
        <Badge variant="muted" title="Não dá para comparar com a versão da loja">
          versão não conferida
        </Badge>
      ) : (
        <Badge variant="success">em dia</Badge>
      )}
    </span>
  )
}

function DrawerBody({ hostname }: { hostname: string }) {
  const detail = useApi<MachineDetail>(`/machines/${encodeURIComponent(hostname)}`)
  const [sending, setSending] = useState<string | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const shown = useLastValue(pending)
  const [packageQuery, setPackageQuery] = useState('')
  const [source, setSource] = useState<'choco' | 'windows'>('choco')

  const d = detail.data
  const m = d?.machine
  const running = !!d?.requests.some((r) => r.status === 'running')
  const canAct = !!m && !m.blockReason && m.status === 'online'
  const busy = running || sending !== null

  // Enquanto houver ação em andamento, o detalhe se atualiza sozinho.
  useEffect(() => {
    if (!running) return
    const timer = setInterval(detail.reload, 4000)
    return () => clearInterval(timer)
  }, [running, detail.reload])

  const packages = useMemo(() => {
    const text = packageQuery.trim().toLowerCase()
    return (d?.packages ?? []).filter((p) => !text || p.packageId.toLowerCase().includes(text))
  }, [d?.packages, packageQuery])

  const windowsPrograms = useMemo(() => {
    const text = packageQuery.trim().toLowerCase()
    return (d?.programs ?? []).filter((p) => !text || p.name.toLowerCase().includes(text))
  }, [d?.programs, packageQuery])

  async function send(action: Action, app?: StoreAppState) {
    if (!m) return
    setSending(`${action}:${app?.appId ?? ''}`)
    try {
      await api(`/machines/${encodeURIComponent(m.hostname)}/action`, { method: 'POST', body: { appId: app?.appId ?? null, action } })
      toast.success(action === 'list' ? `Verificação enviada a ${m.hostname}.` : `${ACTION_LABELS[action]} "${app?.title}" enviado a ${m.hostname}.`)
      detail.reload()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    } finally {
      setSending(null)
    }
  }

  const confirm =
    shown?.kind === 'upgrade'
      ? {
          title: 'Atualizar aplicativo',
          description: `Enviar a atualização de "${shown.app.title}" para ${hostname}${shown.app.installedVersion && shown.app.latestVersion ? ` (de ${shown.app.installedVersion} para ${shown.app.latestVersion})` : ''}?`,
          label: 'Atualizar',
          destructive: false,
        }
      : shown?.kind === 'uninstall'
        ? {
            title: 'Desinstalar aplicativo',
            description: `Desinstalar "${shown.app.title}" de ${hostname}? O programa sai da máquina.`,
            label: 'Desinstalar',
            destructive: true,
          }
        : null

  const login = m?.loggedUser && m.loggedUser !== '-' ? m.loggedUser : null

  return (
    <>
      <SheetHeader>
        <SheetTitle className="text-xl">{hostname}</SheetTitle>
        <SheetDescription>{m ? ([m.client, m.site].filter(Boolean).join(' · ') || 'Máquina do Tactical') : 'Carregando…'}</SheetDescription>
      </SheetHeader>

      <div className="grid grid-cols-[minmax(0,1fr)] gap-4 px-4 pb-6">
        {detail.error && !d ? (
          <ErrorState title="Não foi possível carregar a máquina" message={detail.error} onRetry={detail.reload} />
        ) : !d || !m ? (
          <div className="grid gap-3" aria-busy="true">
            <Skeleton className="h-9 w-56" />
            <Skeleton className="h-28 w-full" />
            <Skeleton className="h-40 w-full" />
          </div>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <Button variant="secondary" disabled={!canAct || busy} loading={sending === 'list:'} onClick={() => void send('list').catch(() => undefined)}>
                Verificar programas agora
              </Button>
              {!canAct && !m.blockReason ? <span className="text-xs text-muted-foreground">Máquina {STATUS_TEXT[m.status] ?? m.status}: as ações ficam indisponíveis até ela voltar.</span> : null}
              {running ? <Badge variant="info">ação em andamento…</Badge> : null}
            </div>

            {m.blockReason ? (
              <Alert variant="destructive">
                <AlertDescription>A loja não age nesta máquina: {m.blockReason}.</AlertDescription>
              </Alert>
            ) : null}
            {m.sameNameCount > 1 ? (
              <Alert variant="warning" role="note">
                <AlertDescription className="text-warning">
                  {m.preferred
                    ? `Há ${m.sameNameCount} agentes com o nome ${m.hostname} no Tactical (agente reinstalado e registro antigo esquecido). A loja usa este, que está online; apague o registro antigo no Tactical.`
                    : `Há ${m.sameNameCount} agentes com o nome ${m.hostname} no Tactical e nenhum está online sozinho: a loja não age até sobrar um registro. Apague o(s) antigo(s) no Tactical.`}
                  <span className="mt-1 block font-mono text-xs">Este agente: …{m.agentId.slice(-6)}</span>
                </AlertDescription>
              </Alert>
            ) : null}

            <dl className="grid gap-x-6 gap-y-4 rounded-lg border bg-card p-4 sm:grid-cols-3">
              <Item label="Status">
                <StatusDot tone={STATUS_TONE[m.status as keyof typeof STATUS_TONE] ?? 'muted'}>{STATUS_TEXT[m.status] ?? m.status}</StatusDot>
              </Item>
              <Item label="Usuário com sessão aberta">
                {login ? (
                  <span className="flex flex-wrap items-center gap-x-2" title={login}>
                    {shortLogin(login)}
                    <Button asChild variant="link" size="sm" className="text-xs">
                      <Link to={`/ver-como/${encodeURIComponent(shortLogin(login))}@@${encodeURIComponent(m.hostname)}`}>Ver a loja desta pessoa</Link>
                    </Button>
                  </span>
                ) : (
                  '—'
                )}
              </Item>
              <Item label="Sistema">{shortOs(m.operatingSystem)}</Item>
              <Item label="Programas verificados">{formatAgo(m.inventoryUtc)}</Item>
              <Item label="Último pedido">{formatDateTime(m.lastRequestUtc)}</Item>
              <Item label="Versão da loja">{m.appVersion ? `${m.appVersion} (aberta ${formatAgo(m.appSeenUtc)})` : '—'}</Item>
            </dl>

            <Tabs defaultValue="apps">
              <TabsList className="max-w-full justify-start overflow-x-auto">
                <TabsTrigger value="apps">Apps da loja ({d.storeApps.length})</TabsTrigger>
                <TabsTrigger value="programas">Programas instalados</TabsTrigger>
                <TabsTrigger value="historico">Histórico ({d.requests.length})</TabsTrigger>
              </TabsList>

              <TabsContent value="apps" className="grid gap-3 pt-3">
                {d.storeApps.length === 0 ? (
                  <div className="rounded-lg border">
                    <EmptyState title="Nenhum app na loja ainda" description="Adicione aplicativos em Aplicativos para poder instalá-los daqui." />
                  </div>
                ) : (
                  <div className="overflow-hidden rounded-lg border">
                    <Table>
                      <TableHeader>
                        <TableRow className="hover:bg-transparent">
                          <TableHead>Aplicativo</TableHead>
                          <TableHead>Na loja</TableHead>
                          <TableHead>Instalada</TableHead>
                          <TableHead className="w-48">
                            <span className="sr-only">Ações</span>
                          </TableHead>
                        </TableRow>
                      </TableHeader>
                      <TableBody>
                        {d.storeApps.map((a) => (
                          <TableRow key={a.appId}>
                            <TableCell>
                              <div className="flex min-w-44 items-center gap-2.5">
                                <AppIcon id={a.appId} title={a.title} />
                                <div className="grid min-w-0">
                                  <strong>{a.title}</strong>
                                  <span className="font-mono text-xs text-muted-foreground">{a.packageId}</span>
                                </div>
                              </div>
                            </TableCell>
                            <TableCell className="font-mono text-[12.5px]">{a.latestVersion ?? '—'}</TableCell>
                            <TableCell>
                              <InstalledCell app={a} />
                            </TableCell>
                            <TableCell>
                              <div className="flex justify-end gap-2">
                                {!a.installedVersion ? (
                                  <Button
                                    size="sm"
                                    aria-label={`Instalar ${a.title}`}
                                    title={a.versionState === 'external' ? 'Já há um programa com este nome nesta máquina, instalado fora da loja. Instalar pela loja pode duplicar.' : undefined}
                                    disabled={!canAct || busy}
                                    loading={sending === `install:${a.appId}`}
                                    onClick={() => void send('install', a).catch(() => undefined)}
                                  >
                                    Instalar
                                  </Button>
                                ) : null}
                                {a.updateAvailable ? (
                                  <Button size="sm" aria-label={`Atualizar ${a.title}`} disabled={!canAct || busy} onClick={() => setPending({ kind: 'upgrade', app: a })}>
                                    Atualizar
                                  </Button>
                                ) : null}
                                {a.installedVersion ? (
                                  <Button size="sm" variant="secondary" aria-label={`Desinstalar ${a.title}`} disabled={!canAct || busy} onClick={() => setPending({ kind: 'uninstall', app: a })}>
                                    Desinstalar
                                  </Button>
                                ) : null}
                              </div>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                )}
              </TabsContent>

              <TabsContent value="programas" className="grid gap-3 pt-3">
                <div role="group" aria-label="Origem dos programas" className="inline-flex w-fit overflow-hidden rounded-lg border">
                  {(
                    [
                      ['choco', `Chocolatey (${d.packages.length})`],
                      ['windows', d.programs ? `Windows (${d.programs.length})` : 'Windows'],
                    ] as const
                  ).map(([value, label]) => (
                    <button
                      key={value}
                      type="button"
                      aria-pressed={source === value}
                      onClick={() => setSource(value)}
                      className={cn(
                        'px-3 py-1.5 text-sm font-medium outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset',
                        source === value ? 'bg-primary text-primary-foreground' : 'bg-card text-foreground hover:bg-muted',
                      )}
                    >
                      {label}
                    </button>
                  ))}
                </div>

                {source === 'choco' ? (
                  d.packages.length === 0 ? (
                    <div className="rounded-lg border">
                      <EmptyState
                        title={m.inventoryUtc ? 'Nenhum pacote do Chocolatey' : 'Ainda não verificado'}
                        description={m.inventoryUtc ? 'Esta máquina não tem programas instalados pelo Chocolatey.' : 'Use "Verificar programas agora" para a loja listar o que há na máquina.'}
                      />
                    </div>
                  ) : (
                    <>
                      <Input type="search" aria-label="Filtrar programas instalados" placeholder="Filtrar pelo nome do pacote…" value={packageQuery} onChange={(event) => setPackageQuery(event.target.value)} />
                      <div className="max-h-80 overflow-auto rounded-lg border">
                        <Table>
                          <TableHeader className="sticky top-0 z-10">
                            <TableRow className="hover:bg-transparent">
                              <TableHead>Pacote</TableHead>
                              <TableHead>Versão</TableHead>
                              <TableHead>Origem</TableHead>
                            </TableRow>
                          </TableHeader>
                          <TableBody>
                            {packages.map((p) => (
                              <TableRow key={p.packageId}>
                                <TableCell className="font-mono text-[12.5px]">{p.packageId}</TableCell>
                                <TableCell className="font-mono text-[12.5px]">{p.version}</TableCell>
                                <TableCell>{p.inStore ? <Badge variant="success">Na loja</Badge> : <Badge variant="muted">Fora da loja</Badge>}</TableCell>
                              </TableRow>
                            ))}
                          </TableBody>
                        </Table>
                        {packages.length === 0 ? <p className="p-4 text-center text-muted-foreground">Nenhum pacote com esse nome.</p> : null}
                      </div>
                      <p aria-live="polite" className="text-xs text-muted-foreground">
                        {plural(packages.length, 'pacote')}
                        {packages.length !== d.packages.length ? ` de ${d.packages.length}` : ''}
                      </p>
                    </>
                  )
                ) : d.programs === null ? (
                  <div className="rounded-lg border">
                    <EmptyState
                      title="Programas do Windows ainda não coletados"
                      description="O script do Tactical desta máquina ainda não envia essa lista (ele passa a enviar com a loja 1.6.0). Depois de atualizar o script, use “Verificar programas agora”."
                    />
                  </div>
                ) : (
                  <>
                    <Input type="search" aria-label="Filtrar programas do Windows" placeholder="Filtrar pelo nome do programa…" value={packageQuery} onChange={(event) => setPackageQuery(event.target.value)} />
                    <div className="max-h-80 overflow-auto rounded-lg border">
                      <Table>
                        <TableHeader className="sticky top-0 z-10">
                          <TableRow className="hover:bg-transparent">
                            <TableHead>Programa</TableHead>
                            <TableHead>Versão</TableHead>
                            <TableHead>Na loja</TableHead>
                          </TableRow>
                        </TableHeader>
                        <TableBody>
                          {windowsPrograms.map((p) => (
                            <TableRow key={`${p.name}|${p.version}`}>
                              <TableCell>{p.name}</TableCell>
                              <TableCell className="font-mono text-[12.5px]">{p.version || '—'}</TableCell>
                              <TableCell>{p.storeApp ? <Badge variant="info">Ligado a {p.storeApp}</Badge> : <span className="text-muted-foreground">—</span>}</TableCell>
                            </TableRow>
                          ))}
                        </TableBody>
                      </Table>
                      {windowsPrograms.length === 0 ? <p className="p-4 text-center text-muted-foreground">Nenhum programa com esse nome.</p> : null}
                    </div>
                    <p aria-live="polite" className="text-xs text-muted-foreground">
                      {plural(windowsPrograms.length, 'programa')}
                      {windowsPrograms.length !== d.programs.length ? ` de ${d.programs.length}` : ''} · coletados {formatAgo(d.programsUtc)}. Só os instalados para a máquina toda (programas só de um usuário e portáteis não aparecem).
                    </p>
                  </>
                )}
              </TabsContent>

              <TabsContent value="historico" className="pt-3">
                <RequestsTable rows={d.requests} showHost={false} onRetried={detail.reload} />
                {d.requests.length > 0 ? (
                  <Button asChild variant="link" size="sm" className="mt-1 px-0">
                    <Link to={`/historico?host=${encodeURIComponent(m.hostname)}`}>Ver todos os pedidos desta máquina no Histórico</Link>
                  </Button>
                ) : null}
              </TabsContent>
            </Tabs>
          </>
        )}
      </div>

      {confirm ? (
        <ConfirmDialog
          open={pending !== null}
          onOpenChange={(open) => !open && setPending(null)}
          title={confirm.title}
          description={confirm.description}
          confirmLabel={confirm.label}
          destructive={confirm.destructive}
          onConfirm={() => send(shown!.kind, shown!.app)}
        />
      ) : null}
    </>
  )
}

/** Detalhe da máquina em gaveta. O endereço (#/maquinas/HOST) abre e fecha a gaveta, então dá para mandar o link a alguém. */
export function MachineDrawer({ hostname, onClose }: { hostname: string | null; onClose: () => void }) {
  const shown = useLastValue(hostname)
  return (
    <Sheet open={hostname !== null} onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="overflow-y-auto data-[side=right]:w-full data-[side=right]:sm:w-3/4 data-[side=right]:sm:max-w-3xl">{shown ? <DrawerBody key={shown} hostname={shown} /> : null}</SheetContent>
    </Sheet>
  )
}
