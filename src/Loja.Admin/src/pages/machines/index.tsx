import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { MonitorIcon, RefreshCwIcon, ScanSearchIcon } from 'lucide-react'
import { GroupPicker } from '@/components/group-picker'
import { PageHeader } from '@/components/shell/page-header'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Chip } from '@/components/ui/chip'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { PaginationBar } from '@/components/ui/pagination-bar'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { StatCard } from '@/components/ui/stat-card'
import { useApi } from '@/hooks/use-api'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { plural, shortLogin, shortOs } from '@/lib/format'
import { useSort } from '@/lib/sort'
import type { ClientRelease, Machine, UpgradeAllResult } from '@/lib/types'
import { MachineDrawer } from './machine-drawer'
import { MachinesTable } from './machines-table'

type Show = 'eligible' | 'store' | 'outdated' | 'blocked' | 'duplicate' | 'all'
const SHOW_VALUES: Show[] = ['eligible', 'store', 'outdated', 'blocked', 'duplicate', 'all']
const SHOW_LABEL: Record<Show, string> = {
  eligible: 'Estações (onde a loja atua)',
  store: 'Com apps da loja',
  outdated: 'Com atualização pendente',
  blocked: 'Bloqueadas (servidores)',
  duplicate: 'Nome duplicado no Tactical',
  all: 'Todas',
}
const STATUS_LABEL: Record<string, string> = { online: 'Online', offline: 'Offline', overdue: 'Sem contato' }
const ALL = 'all'
const PAGE_SIZE = 50
const MAX_SCAN = 100

// Fora do componente: o useSort recalcula só quando a regra muda de verdade.
const accessors = {
  hostname: (m: Machine) => m.hostname,
  site: (m: Machine) => m.site,
  status: (m: Machine) => m.status,
  user: (m: Machine) => (m.loggedUser && m.loggedUser !== '-' ? shortLogin(m.loggedUser) : null),
  os: (m: Machine) => shortOs(m.operatingSystem),
  apps: (m: Machine) => m.storeAppsInstalled,
  outdated: (m: Machine) => m.outdated,
  inventory: (m: Machine) => (m.inventoryUtc ? Date.parse(m.inventoryUtc) : null),
  store: (m: Machine) => m.appVersion,
}

/**
 * Máquinas do Tactical. Os filtros ficam na URL (`#/maquinas?site=Matriz&loja=sem`): o link pode ser guardado ou mandado a alguém,
 * e a Visão geral abre esta tela já filtrada. "Com a loja" = a loja (app do funcionário) já foi aberta na máquina e informou a versão;
 * versões antigas do app não informam, então aparecem como "sem a loja".
 */
export function MachinesPage() {
  const machines = useApi<Machine[]>('/machines')
  const releases = useApi<ClientRelease[]>('/client/releases')
  const { host } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()

  const requestedShow = params.get('mostrar') as Show | null
  const show: Show = requestedShow && SHOW_VALUES.includes(requestedShow) ? requestedShow : 'eligible'
  const query = params.get('q') ?? ''
  const site = params.get('site') ?? ''
  const status = params.get('status') ?? ''
  const onlineOnly = params.get('online') === '1'
  const store = params.get('loja') === 'com' ? 'com' : params.get('loja') === 'sem' ? 'sem' : ''
  const groupSid = params.get('grupo') ?? ''
  const groupName = params.get('grupoNome') ?? ''
  const [page, setPage] = useState(1)

  /** Muda parâmetros da URL (valor vazio apaga) e volta à primeira página. */
  function update(changes: Record<string, string | null>, replace = false) {
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const [key, value] of Object.entries(changes)) {
          if (value) next.set(key, value)
          else next.delete(key)
        }
        return next
      },
      { replace },
    )
    setPage(1)
  }

  // A busca por texto espera a pessoa parar de digitar antes de mexer na URL.
  const [draft, setDraft] = useState(query)
  useEffect(() => {
    setDraft((current) => (current.trim() === query ? current : query))
  }, [query])
  useEffect(() => {
    if (draft.trim() === query) return
    const timer = setTimeout(() => update({ q: draft.trim() || null }, true), 250)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft])

  const all = useMemo(() => machines.data ?? [], [machines.data])
  const eligible = useMemo(() => all.filter((m) => !m.blockReason), [all])
  const online = (m: Machine) => m.status === 'online'
  const sites = useMemo(() => [...new Set(all.map((m) => m.site).filter((s): s is string => Boolean(s)))].sort((a, b) => a.localeCompare(b, 'pt-BR')), [all])
  // Nomes duplicados em que a loja escolheu um agente (os outros são registros antigos).
  const withPreferred = useMemo(() => new Set(all.filter((m) => m.sameNameCount > 1 && m.preferred).map((m) => m.hostname.toUpperCase())), [all])

  // Grupo do usuário: o servidor consulta o AD (com cache) e devolve só as máquinas cujo usuário logado agora está no grupo.
  const byGroup = useApi<Machine[]>(groupSid ? `/machines?group=${encodeURIComponent(groupSid)}` : null)
  const groupMatches = useMemo(() => new Set((byGroup.data ?? []).map((m) => m.agentId)), [byGroup.data])
  const groupPending = groupSid !== '' && byGroup.loading && !byGroup.data

  const filtered = useMemo(() => {
    const text = query.trim().toLowerCase()
    return all.filter((m) => {
      if (show === 'eligible' && m.blockReason) return false
      if (show === 'store' && m.storeAppsInstalled === 0) return false
      if (show === 'outdated' && m.outdated === 0) return false
      if (show === 'blocked' && !m.blockReason) return false
      if (show === 'duplicate' && m.sameNameCount <= 1) return false
      if (site && m.site !== site) return false
      if (status && m.status !== status) return false
      if (onlineOnly && !online(m)) return false
      if (store === 'com' && !m.appVersion) return false
      if (store === 'sem' && m.appVersion) return false
      if (groupSid && !byGroup.error && !groupMatches.has(m.agentId)) return false
      if (text && ![m.hostname, m.loggedUser, m.site].some((t) => (t ?? '').toLowerCase().includes(text))) return false
      return true
    })
  }, [all, query, show, site, status, onlineOnly, store, groupSid, groupMatches, byGroup.error])

  const { sorted, sort, toggle } = useSort(filtered, accessors, { key: 'hostname', direction: 'asc' })
  const lastPage = Math.max(1, Math.ceil(sorted.length / PAGE_SIZE))
  const current = Math.min(page, lastPage)
  const visible = sorted.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE)

  const chips: { id: string; label: string; remove: () => void }[] = []
  if (query.trim()) chips.push({ id: 'q', label: `Busca: “${query.trim()}”`, remove: () => update({ q: null }) })
  if (show !== 'eligible') chips.push({ id: 'mostrar', label: `Mostrar: ${SHOW_LABEL[show]}`, remove: () => update({ mostrar: null }) })
  if (site) chips.push({ id: 'site', label: `Site: ${site}`, remove: () => update({ site: null }) })
  if (status) chips.push({ id: 'status', label: `Status: ${STATUS_LABEL[status] ?? status}`, remove: () => update({ status: null }) })
  if (onlineOnly) chips.push({ id: 'online', label: 'Só online', remove: () => update({ online: null }) })
  if (store) chips.push({ id: 'loja', label: store === 'com' ? 'Só com a loja' : 'Sem a loja', remove: () => update({ loja: null }) })
  if (groupSid) chips.push({ id: 'grupo', label: `Grupo do usuário: ${groupName || groupSid}`, remove: () => update({ grupo: null, grupoNome: null }) })
  const filtering = chips.length > 0

  function clearFilters() {
    setDraft('')
    setParams(new URLSearchParams(), { replace: false })
    setPage(1)
  }

  // ---- Seleção para "Verificar programas" ----
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [scanning, setScanning] = useState(false)
  const [skipped, setSkipped] = useState<string[] | null>(null)
  const shownSkipped = useLastValue(skipped)
  useEffect(() => {
    // Máquina que sumiu da lista sai da seleção.
    if (machines.data) setSelected((previous) => new Set([...previous].filter((name) => machines.data!.some((m) => m.hostname === name))))
  }, [machines.data])

  function select(hostnames: string[], on: boolean) {
    setSelected((previous) => {
      const next = new Set(previous)
      for (const name of hostnames) {
        if (on) next.add(name)
        else next.delete(name)
      }
      return next
    })
  }

  async function scan() {
    setScanning(true)
    try {
      const result = await api<UpgradeAllResult>('/machines/scan', { method: 'POST', body: { hostnames: [...selected] } })
      toast(`${plural(result.started, 'verificação enviada', 'verificações enviadas')}${result.skipped ? `, ${plural(result.skipped, 'máquina pulada', 'máquinas puladas')}` : ''}.`, { duration: 7000 })
      if (result.messages.length) setSkipped(result.messages)
      setSelected(new Set())
      setTimeout(machines.reload, 4000)
    } catch (error) {
      toast.error(messageOf(error))
    } finally {
      setScanning(false)
    }
  }

  const currentStore = releases.data?.find((r) => r.isCurrent)?.version ?? null
  const failed = machines.error && !machines.data
  const refreshing = machines.loading && machines.data !== null

  function pick(next: Show) {
    update({ mostrar: next === 'eligible' ? null : next })
  }

  return (
    <div className="grid gap-4">
      <PageHeader
        title="Máquinas"
        actions={
          <Button variant="secondary" loading={refreshing} onClick={machines.reload}>
            <RefreshCwIcon aria-hidden="true" />
            Atualizar lista
          </Button>
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label="Estações no Tactical" value={eligible.length} hint={`${eligible.filter(online).length} online agora`} onClick={() => pick('eligible')} active={show === 'eligible'} />
        <StatCard label="Com apps da loja" value={all.filter((m) => m.storeAppsInstalled > 0).length} onClick={() => pick('store')} active={show === 'store'} />
        <StatCard
          label="Com atualização pendente"
          value={all.filter((m) => m.outdated > 0).length}
          tone={all.some((m) => m.outdated > 0) ? 'warning' : 'default'}
          onClick={() => pick('outdated')}
          active={show === 'outdated'}
        />
        <StatCard label="Bloqueadas (servidores)" value={all.length - eligible.length} hint="a loja nunca age nelas" onClick={() => pick('blocked')} active={show === 'blocked'} />
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Input type="search" aria-label="Filtrar por computador, usuário ou site" placeholder="Computador, usuário ou site…" className="min-w-56 flex-1" value={draft} onChange={(event) => setDraft(event.target.value)} />
        <Select value={show} onValueChange={(value) => pick(value as Show)}>
          <SelectTrigger aria-label="Mostrar" className="w-60">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {SHOW_VALUES.map((value) => (
              <SelectItem key={value} value={value}>
                {SHOW_LABEL[value]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={site || ALL} onValueChange={(value) => update({ site: value === ALL ? null : value })}>
          <SelectTrigger aria-label="Site" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Todos os sites</SelectItem>
            {sites.map((name) => (
              <SelectItem key={name} value={name}>
                {name}
              </SelectItem>
            ))}
            {/* site vindo da URL que não existe mais na lista: continua visível para dar para tirar */}
            {site && !sites.includes(site) ? <SelectItem value={site}>{site}</SelectItem> : null}
          </SelectContent>
        </Select>
        <Select value={status || ALL} onValueChange={(value) => update({ status: value === ALL ? null : value })}>
          <SelectTrigger aria-label="Status" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Todos os status</SelectItem>
            <SelectItem value="online">Online</SelectItem>
            <SelectItem value="offline">Offline</SelectItem>
            <SelectItem value="overdue">Sem contato</SelectItem>
          </SelectContent>
        </Select>
      </div>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="w-72 max-w-full">
          <GroupPicker
            value={groupSid ? { name: groupName || groupSid, sid: groupSid } : null}
            onChange={(group) => update({ grupo: group?.sid ?? null, grupoNome: group?.sid ? group.name : null })}
            placeholder="Grupo do usuário"
            clearLabel="Tirar o filtro de grupo"
            aria-label="Grupo do usuário"
          />
        </div>
        <div className="flex items-center gap-2">
          <Checkbox id="so-online" checked={onlineOnly} onCheckedChange={(checked) => update({ online: checked === true ? '1' : null })} />
          <Label htmlFor="so-online" className="font-normal">
            Só online
          </Label>
        </div>
        <div className="flex items-center gap-2">
          <Checkbox id="so-loja" checked={store === 'com'} onCheckedChange={(checked) => update({ loja: checked === true ? 'com' : null })} />
          <Label htmlFor="so-loja" className="font-normal" title="A loja já foi aberta na máquina e informou a versão">
            Só com a loja
          </Label>
        </div>
        <span aria-live="polite" className="text-sm text-muted-foreground">
          {groupPending ? 'Consultando o AD…' : `${plural(filtered.length, 'máquina')}${filtering ? ` de ${all.length}` : ''}`}
        </span>
      </div>

      {groupSid ? (
        byGroup.error && !byGroup.data ? (
          <Alert variant="destructive">
            <AlertDescription>Não foi possível aplicar o filtro de grupo: {byGroup.error} A lista abaixo está sem esse filtro.</AlertDescription>
          </Alert>
        ) : (
          <p className="text-xs text-muted-foreground">O grupo vale para quem está com sessão aberta na máquina agora (segundo o Tactical), pela associação de hoje no AD, com cache de 10 minutos.</p>
        )
      ) : null}

      {chips.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Filtros ligados">
          {chips.map((chip) => (
            <Chip key={chip.id} onRemove={chip.remove} removeLabel={`Tirar o filtro: ${chip.label}`}>
              {chip.label}
            </Chip>
          ))}
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            Limpar
          </Button>
        </div>
      ) : null}

      {selected.size > 0 ? (
        <div role="region" aria-label="Ações em lote" className="flex flex-wrap items-center gap-2 rounded-lg border border-primary/40 bg-accent px-3 py-2 text-accent-foreground">
          <span aria-live="polite" className="text-sm font-medium">
            {plural(selected.size, 'máquina selecionada', 'máquinas selecionadas')}
          </span>
          {selected.size > MAX_SCAN ? <span className="text-xs">No máximo {MAX_SCAN} por vez.</span> : null}
          <div className="ml-auto flex flex-wrap gap-2">
            <Button size="sm" variant="secondary" loading={scanning} disabled={selected.size > MAX_SCAN} onClick={() => void scan()} title="Só consulta os programas instalados: nada é instalado nem removido">
              <ScanSearchIcon aria-hidden="true" />
              Verificar programas
            </Button>
            <Button size="sm" variant="ghost" disabled={scanning} onClick={() => setSelected(new Set())}>
              Limpar seleção
            </Button>
          </div>
        </div>
      ) : null}

      {failed ? (
        <div className="rounded-lg border bg-card">
          <ErrorState title="Não foi possível carregar as máquinas" message={machines.error!} onRetry={machines.reload} />
        </div>
      ) : (
        <>
          <MachinesTable
            rows={visible}
            loading={(machines.loading && !machines.data) || groupPending}
            sort={sort}
            onSort={toggle}
            onOpen={(hostname) => navigate({ pathname: `/maquinas/${encodeURIComponent(hostname)}`, search: params.size ? `?${params}` : '' })}
            withPreferred={withPreferred}
            currentStore={currentStore}
            selected={selected}
            onSelect={select}
          />
          {!machines.loading && !groupPending && filtered.length === 0 ? (
            <div className="rounded-lg border bg-card">
              <EmptyState
                icon={MonitorIcon}
                title={all.length === 0 ? 'Nenhuma máquina no Tactical' : 'Nenhuma máquina com esse filtro'}
                description={all.length === 0 ? 'Confira a conexão com o Tactical RMM em Configurações.' : 'Nenhuma máquina combina com a busca e os filtros.'}
                action={
                  all.length > 0 ? (
                    <Button variant="secondary" onClick={clearFilters}>
                      Limpar filtros
                    </Button>
                  ) : undefined
                }
              />
            </div>
          ) : null}
          {sorted.length > PAGE_SIZE ? <PaginationBar total={sorted.length} page={current} pageSize={PAGE_SIZE} onPageChange={setPage} singular="máquina" pluralForm="máquinas" /> : null}
        </>
      )}

      <MachineDrawer hostname={host ?? null} onClose={() => navigate({ pathname: '/maquinas', search: params.size ? `?${params}` : '' })} />

      <Dialog open={skipped !== null} onOpenChange={(open) => !open && setSkipped(null)}>
        <DialogContent>
          {shownSkipped ? (
            <>
              <DialogHeader>
                <DialogTitle>Máquinas puladas</DialogTitle>
                <DialogDescription>A verificação não foi enviada a estas máquinas. Tente de novo quando estiverem ao alcance.</DialogDescription>
              </DialogHeader>
              <ul className="grid max-h-64 gap-1.5 overflow-auto pl-5 text-sm [list-style:disc]">
                {shownSkipped.map((message) => (
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
