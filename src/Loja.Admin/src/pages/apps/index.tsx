import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { MinusIcon, PackageIcon, PlusIcon } from 'lucide-react'
import { groupKey, groupLabel, isEveryone } from '@/components/group-select'
import { PageHeader } from '@/components/shell/page-header'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { PaginationBar } from '@/components/ui/pagination-bar'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useApi } from '@/hooks/use-api'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'
import { useSort } from '@/lib/sort'
import type { AdminApp, AppStat, BulkGroupsResult, GroupRef, GroupsBefore, UpgradeAllResult } from '@/lib/types'
import { AppHistoryDialog } from '@/components/audit/app-history-dialog'
import { AddAppDialog } from './add-app-dialog'
import { AliasesDialog } from './aliases-dialog'
import { AppsTable, type AppRow, type Flag, type RowAction } from './apps-table'
import { BulkGroupsDialog, type BulkMode } from './bulk-groups-dialog'
import { EditGroupsDialog } from './edit-groups-dialog'
import { GroupFilter, type GroupFilterOption } from './group-filter'

type Origin = 'all' | 'community' | 'internal'
type Status = 'all' | 'enabled' | 'disabled' | 'outdated' | 'missing'
type Dialog =
  | { kind: 'upgrade'; app: AppRow }
  | { kind: 'remove'; app: AppRow }
  | { kind: 'skipped'; title: string; messages: string[] }

const PAGE_SIZE = 20
const ORIGINS: Origin[] = ['community', 'internal']
const STATUSES: Status[] = ['enabled', 'disabled', 'outdated', 'missing']
const ORIGIN_LABEL: Record<Exclude<Origin, 'all'>, string> = { community: 'Internet', internal: 'Interno' }
const STATUS_LABEL: Record<Exclude<Status, 'all'>, string> = {
  enabled: 'Ativos',
  disabled: 'Desativados',
  outdated: 'Com máquinas desatualizadas',
  missing: 'Com grupo que sumiu do AD',
}

// Fora do componente: o useSort recalcula só quando as regras mudam de verdade.
const accessors = {
  title: (a: AppRow) => a.title,
  source: (a: AppRow) => a.source,
  version: (a: AppRow) => a.version,
  installed: (a: AppRow) => a.installed,
  outdated: (a: AppRow) => a.outdated,
}

/** O que cada chave diz na confirmação, com o efeito em português claro. */
const FLAG_TEXT: Record<Flag, { on: (title: string) => string; off: (title: string) => string }> = {
  enabled: {
    on: (t) => `${t}: ativo. Volta a aparecer na loja.`,
    off: (t) => `${t}: desativado. Some da loja, mas continua cadastrado.`,
  },
  autoUpdate: {
    on: (t) => `${t}: vai atualizar sozinho na janela de horário.`,
    off: (t) => `${t}: não atualiza mais sozinho.`,
  },
  allowUninstall: {
    on: (t) => `${t}: a pessoa pode desinstalar pela loja.`,
    off: (t) => `${t}: não dá mais para desinstalar pela loja.`,
  },
}

/**
 * Lista de aplicativos. Busca, origem, situação e grupos ficam na URL (`#/aplicativos?grupo=S-1-5-…`): o link pode ser
 * guardado, e os atalhos "Ver apps deste grupo" de outras telas chegam aqui já filtrados. O filtro de grupo é feito aqui
 * mesmo (a lista inteira já está na tela) e compara pelo SID, então continua valendo se o grupo for renomeado no AD.
 */
export function AppsPage() {
  const apps = useApi<AdminApp[]>('/apps')
  const stats = useApi<AppStat[]>('/apps/stats')
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()

  // Ajustes otimistas das chaves: a tela muda na hora e o servidor confirma depois. Quando chegam dados novos, já estão neles.
  const [overrides, setOverrides] = useState<Record<number, Partial<AdminApp>>>({})
  useEffect(() => setOverrides({}), [apps.data])

  const rows = useMemo<AppRow[]>(() => {
    const byId = new Map((stats.data ?? []).map((s) => [s.appId, s]))
    return (apps.data ?? []).map((app) => {
      const stat = byId.get(app.id)
      return { ...app, ...overrides[app.id], installed: stat?.installed ?? 0, outdated: stat?.outdated ?? 0 }
    })
  }, [apps.data, stats.data, overrides])

  const rowsRef = useRef(rows)
  useEffect(() => {
    rowsRef.current = rows
  }, [rows])

  // ---- Filtros (na URL) ----
  const query = params.get('q') ?? ''
  const originParam = params.get('origem') as Origin | null
  const statusParam = params.get('situacao') as Status | null
  const origin: Origin = originParam && ORIGINS.includes(originParam) ? originParam : 'all'
  const status: Status = statusParam && STATUSES.includes(statusParam) ? statusParam : 'all'
  // Chaves dos grupos (SID, "*" ou "nome:…") sempre em minúsculas, de onde quer que o link tenha vindo.
  const groupKeys = useMemo(() => params.getAll('grupo').map((key) => key.toLowerCase()), [params])
  const activeGroups = useMemo(() => new Set(groupKeys), [groupKeys])
  const [page, setPage] = useState(1)

  function setFilters(changes: { q?: string; origem?: string; situacao?: string; grupo?: string[] }, replace = false) {
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const key of ['q', 'origem', 'situacao'] as const) {
          if (changes[key] === undefined) continue
          if (changes[key]) next.set(key, changes[key])
          else next.delete(key)
        }
        if (changes.grupo) {
          next.delete('grupo')
          for (const key of changes.grupo) next.append('grupo', key)
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
    const timer = setTimeout(() => setFilters({ q: draft.trim() }, true), 250)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft])

  // Grupos que a loja usa (para o filtro e para dar nome aos chips). A chave é o SID; "*" e grupos antigos sem SID têm as suas.
  const groupOptions = useMemo<GroupFilterOption[]>(() => {
    const byKey = new Map<string, GroupFilterOption>()
    for (const app of rows) {
      for (const group of app.groupRefs) {
        const key = groupKey(group).toLowerCase()
        const entry = byKey.get(key)
        if (entry) entry.count++
        else byKey.set(key, { key, name: groupLabel(group), count: 1 })
      }
    }
    // Grupo que veio por link e nenhum aplicativo usa: continua no filtro, com o que der para mostrar.
    for (const key of groupKeys) if (!byKey.has(key)) byKey.set(key, { key, name: key.startsWith('nome:') ? key.slice(5) : key, count: 0 })
    return [...byKey.values()].sort((a, b) => (a.key === '*' ? -1 : b.key === '*' ? 1 : a.name.localeCompare(b.name, 'pt-BR')))
  }, [rows, groupKeys])

  const { filtered, viaEveryone } = useMemo(() => {
    const text = query.trim().toLowerCase()
    const everyone = new Set<number>()
    const list = rows.filter((app) => {
      if (text && ![app.title, app.packageId, ...app.groups].some((t) => t.toLowerCase().includes(text))) return false
      if (origin !== 'all' && app.source !== origin) return false
      if (status === 'enabled' && !app.enabled) return false
      if (status === 'disabled' && app.enabled) return false
      if (status === 'outdated' && app.outdated === 0) return false
      if (status === 'missing' && !(app.missingGroups ?? []).length) return false
      if (activeGroups.size > 0) {
        const direct = app.groupRefs.some((group) => activeGroups.has(groupKey(group).toLowerCase()))
        // Liberado para "Todos os usuários" vale para quem está em qualquer grupo: entra, e a linha diz por quê.
        const byEveryone = !direct && app.groupRefs.some(isEveryone)
        if (!direct && !byEveryone) return false
        if (byEveryone) everyone.add(app.id)
      }
      return true
    })
    return { filtered: list, viaEveryone: everyone }
  }, [rows, query, origin, status, activeGroups])

  const { sorted, sort, toggle } = useSort(filtered, accessors, { key: 'title', direction: 'asc' })
  const lastPage = Math.max(1, Math.ceil(sorted.length / PAGE_SIZE))
  const current = Math.min(page, lastPage)
  const visible = sorted.slice((current - 1) * PAGE_SIZE, current * PAGE_SIZE)
  const filtering = query.trim() !== '' || origin !== 'all' || status !== 'all' || activeGroups.size > 0

  const chips: { id: string; label: string; remove: () => void }[] = []
  if (query.trim()) chips.push({ id: 'q', label: `Busca: “${query.trim()}”`, remove: () => setFilters({ q: '' }) })
  if (origin !== 'all') chips.push({ id: 'origem', label: `Origem: ${ORIGIN_LABEL[origin]}`, remove: () => setFilters({ origem: '' }) })
  if (status !== 'all') chips.push({ id: 'situacao', label: `Situação: ${STATUS_LABEL[status]}`, remove: () => setFilters({ situacao: '' }) })
  for (const key of groupKeys) {
    const name = groupOptions.find((option) => option.key === key)?.name ?? key
    chips.push({ id: `grupo:${key}`, label: `Grupo: ${name}`, remove: () => setFilters({ grupo: groupKeys.filter((k) => k !== key) }) })
  }

  function clearFilters() {
    setDraft('')
    setParams(new URLSearchParams(), { replace: false })
    setPage(1)
  }

  function filterByGroup(group: GroupRef) {
    const key = groupKey(group).toLowerCase()
    if (activeGroups.has(key)) return
    setFilters({ grupo: [...groupKeys, key] })
  }

  function seeGroup(group: GroupRef) {
    if (group.sid) navigate(`/grupos/${encodeURIComponent(group.sid)}`)
  }

  // ---- Seleção para editar em lote ----
  const [selected, setSelected] = useState<Set<number>>(new Set())
  useEffect(() => {
    // Aplicativo removido (ou lista recarregada) sai da seleção.
    if (apps.data) setSelected((previous) => new Set([...previous].filter((id) => apps.data!.some((app) => app.id === id))))
  }, [apps.data])
  const selectedRows = useMemo(() => rows.filter((app) => selected.has(app.id)), [rows, selected])
  const everyVisibleSelected = visible.length > 0 && visible.every((app) => selected.has(app.id))
  const selectedInList = sorted.filter((app) => selected.has(app.id)).length

  function select(ids: number[], on: boolean) {
    setSelected((previous) => {
      const next = new Set(previous)
      for (const id of ids) {
        if (on) next.add(id)
        else next.delete(id)
      }
      return next
    })
  }

  const [bulk, setBulk] = useState<BulkMode | null>(null)

  // ---- Diálogos ----
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<AppRow | null>(null)
  const shownEditing = useLastValue(editing)
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const shown = useLastValue(dialog)
  const [historyOf, setHistoryOf] = useState<AppRow | null>(null)
  const shownHistory = useLastValue(historyOf)
  const [aliasesOf, setAliasesOf] = useState<AppRow | null>(null)
  const shownAliases = useLastValue(aliasesOf)

  function reloadAll() {
    apps.reload()
    stats.reload()
  }

  async function setFlag(app: AppRow, flag: Flag, value: boolean, announce = true, undo = false) {
    const previous = rowsRef.current.find((a) => a.id === app.id)?.[flag] ?? app[flag]
    setOverrides((all) => ({ ...all, [app.id]: { ...all[app.id], [flag]: value } }))
    // O corpo parte do estado de agora (com as outras chaves já mexidas), para duas trocas seguidas não se atropelarem.
    const latest = { ...(rowsRef.current.find((a) => a.id === app.id) ?? app), [flag]: value }
    try {
      await api(`/apps/${app.id}`, {
        method: 'PUT',
        body: { groups: latest.groups, groupRefs: latest.groupRefs, enabled: latest.enabled, autoUpdate: latest.autoUpdate, allowUninstall: latest.allowUninstall, reason: undo ? 'undo' : undefined },
      })
      reloadAll()
      if (announce) {
        const text = FLAG_TEXT[flag]
        toast(value ? text.on(app.title) : text.off(app.title), {
          duration: 7000,
          action: { label: 'Desfazer', onClick: () => void setFlag(app, flag, previous, false, true).then(() => toast.success('Desfeito.')) },
        })
      }
    } catch (error) {
      setOverrides((all) => {
        const { [flag]: _discarded, ...rest } = all[app.id] ?? {}
        void _discarded
        return { ...all, [app.id]: rest }
      })
      toast.error(messageOf(error))
    }
  }

  /** "Desfazer" das mudanças de grupos: o servidor confere de novo (autorização, grupos no AD) e registra na auditoria. */
  async function undoGroups(before: GroupsBefore[]) {
    try {
      await api<BulkGroupsResult>('/apps/restore-groups', { method: 'POST', body: { items: before } })
      reloadAll()
      toast.success('Desfeito.')
    } catch (error) {
      toast.error(messageOf(error))
    }
  }

  function announceGroups(message: string, before: GroupsBefore[]) {
    toast(message, {
      duration: 9000,
      action: before.length ? { label: 'Desfazer', onClick: () => void undoGroups(before) } : undefined,
    })
  }

  function onBulkDone(result: BulkGroupsResult, summary: string) {
    reloadAll()
    setSelected(new Set())
    announceGroups(summary, result.before)
  }

  function onGroupsSaved(app: AdminApp, before: GroupRef[]) {
    reloadAll()
    announceGroups(`Grupos de “${app.title}” salvos.`, [{ appId: app.id, groups: before }])
  }

  /** "Desfazer" dos nomes no Windows: volta à lista de antes e fica na auditoria como revertido. */
  async function undoAliases(app: AdminApp, before: string[]) {
    try {
      await api(`/apps/${app.id}/aliases`, { method: 'PUT', body: { aliases: before, reason: 'undo' } })
      reloadAll()
      toast.success('Desfeito.')
    } catch (error) {
      toast.error(messageOf(error))
    }
  }

  function onAliasesSaved(app: AdminApp, before: string[]) {
    reloadAll()
    toast(`Nomes no Windows de “${app.title}” salvos.`, { duration: 9000, action: { label: 'Desfazer', onClick: () => void undoAliases(app, before) } })
  }

  async function refresh(app: AppRow) {
    try {
      const updated = await api<AdminApp>(`/apps/${app.id}/refresh`, { method: 'POST' })
      toast.success(`Dados de "${updated.title}" atualizados (versão ${updated.version ?? '?'}).`)
      reloadAll()
    } catch (error) {
      toast.error(messageOf(error))
    }
  }

  function onAction(action: RowAction, app: AppRow) {
    if (action === 'groups') setEditing(app)
    else if (action === 'aliases') setAliasesOf(app)
    else if (action === 'refresh') void refresh(app)
    else if (action === 'history') setHistoryOf(app)
    else setDialog({ kind: action, app })
  }

  async function upgradeAll(app: AppRow) {
    try {
      const result = await api<UpgradeAllResult>(`/apps/${app.id}/upgrade-all`, { method: 'POST' })
      toast(`${plural(result.started, 'atualização enviada', 'atualizações enviadas')}, ${plural(result.skipped, 'máquina pulada', 'máquinas puladas')}.`, { duration: 7000 })
      if (result.messages.length) setTimeout(() => setDialog({ kind: 'skipped', title: app.title, messages: result.messages }), 300)
      reloadAll()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  async function remove(app: AppRow) {
    try {
      await api(`/apps/${app.id}`, { method: 'DELETE' })
      toast.success(`"${app.title}" removido da loja.`)
      reloadAll()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  const confirm =
    shown?.kind === 'upgrade'
      ? {
          title: 'Atualizar em todas as máquinas',
          description: `Enviar a atualização de "${shown.app.title}" para ${plural(shown.app.outdated, 'máquina com versão antiga', 'máquinas com versão antiga')}? Máquinas offline ou ocupadas ficam para depois.`,
          label: 'Atualizar',
          destructive: false,
          run: () => upgradeAll(shown.app),
        }
      : shown?.kind === 'remove'
        ? {
            title: 'Remover da loja',
            description: `Remover "${shown.app.title}" da loja? Quem já instalou continua com o programa; ele só some da loja.`,
            label: 'Remover',
            destructive: true,
            run: () => remove(shown.app),
          }
        : null

  const failed = apps.error && !apps.data

  return (
    <div className="grid gap-4">
      <PageHeader
        title="Aplicativos"
        actions={
          <Button onClick={() => setAdding(true)}>
            <PlusIcon aria-hidden="true" />
            Adicionar aplicativo
          </Button>
        }
      />

      <div className="flex flex-wrap items-center gap-2">
        <Input type="search" aria-label="Filtrar por nome, pacote ou grupo" placeholder="Filtrar por nome, pacote ou grupo…" className="min-w-56 flex-1" value={draft} onChange={(event) => setDraft(event.target.value)} />
        <Select value={origin} onValueChange={(value) => setFilters({ origem: value === 'all' ? '' : value })}>
          <SelectTrigger aria-label="Origem" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">Todas as origens</SelectItem>
            <SelectItem value="community">Internet</SelectItem>
            <SelectItem value="internal">Interno</SelectItem>
          </SelectContent>
        </Select>
        <Select value={status} onValueChange={(value) => setFilters({ situacao: value === 'all' ? '' : value })}>
          <SelectTrigger aria-label="Situação" className="w-56">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">Todas as situações</SelectItem>
            <SelectItem value="enabled">Ativos</SelectItem>
            <SelectItem value="disabled">Desativados</SelectItem>
            <SelectItem value="outdated">Com máquinas desatualizadas</SelectItem>
            <SelectItem value="missing">Com grupo que sumiu do AD</SelectItem>
          </SelectContent>
        </Select>
        <GroupFilter options={groupOptions} value={groupKeys} onChange={(keys) => setFilters({ grupo: keys })} />
        <span aria-live="polite" className="text-sm text-muted-foreground">
          {plural(filtered.length, 'aplicativo')}
          {filtering ? ` de ${rows.length}` : ''}
        </span>
      </div>

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
          {viaEveryone.size > 0 ? (
            <span className="text-xs text-muted-foreground">
              {plural(viaEveryone.size, 'aplicativo entra', 'aplicativos entram')} por serem liberados para Todos os usuários.
            </span>
          ) : null}
        </div>
      ) : null}

      {selectedRows.length > 0 ? (
        <div role="region" aria-label="Edição em lote" className="flex flex-wrap items-center gap-2 rounded-lg border border-primary/40 bg-accent px-3 py-2 text-accent-foreground">
          <span aria-live="polite" className="text-sm font-medium">
            {plural(selectedRows.length, 'aplicativo selecionado', 'aplicativos selecionados')}
          </span>
          {everyVisibleSelected && sorted.length > selectedInList ? (
            <Button variant="link" className="text-[13px]" onClick={() => select(sorted.map((app) => app.id), true)}>
              Selecionar {sorted.length === 1 ? 'o aplicativo' : `os ${sorted.length} aplicativos`} da lista
            </Button>
          ) : null}
          <div className="ml-auto flex flex-wrap gap-2">
            <Button size="sm" variant="secondary" onClick={() => setBulk('add')}>
              <PlusIcon aria-hidden="true" />
              Adicionar grupo
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setBulk('remove')}>
              <MinusIcon aria-hidden="true" />
              Remover grupo
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
              Limpar seleção
            </Button>
          </div>
        </div>
      ) : null}

      {failed ? (
        <div className="rounded-lg border bg-card">
          <ErrorState message={apps.error!} onRetry={reloadAll} />
        </div>
      ) : (
        <>
          <AppsTable
            rows={visible}
            loading={apps.loading && !apps.data}
            sort={sort}
            onSort={toggle}
            onFlag={(app, flag, value) => void setFlag(app, flag, value)}
            onAction={onAction}
            selected={selected}
            onSelect={select}
            activeGroups={activeGroups}
            viaEveryone={viaEveryone}
            onFilterGroup={filterByGroup}
            onSeeGroup={seeGroup}
          />
          {!apps.loading && rows.length === 0 ? (
            <div className="rounded-lg border bg-card">
              <EmptyState
                icon={PackageIcon}
                title="Nenhum aplicativo na loja"
                description="Adicione o primeiro buscando no Chocolatey ou na pasta interna do NAS. Quem estiver nos grupos que você escolher passa a ver e instalar."
                action={
                  <Button onClick={() => setAdding(true)}>
                    <PlusIcon aria-hidden="true" />
                    Adicionar aplicativo
                  </Button>
                }
              />
            </div>
          ) : null}
          {!apps.loading && rows.length > 0 && filtered.length === 0 ? (
            <div className="rounded-lg border bg-card">
              <EmptyState
                title="Nenhum aplicativo encontrado"
                description="Nenhum aplicativo combina com a busca e os filtros."
                action={
                  <Button variant="secondary" onClick={clearFilters}>
                    Limpar filtros
                  </Button>
                }
              />
            </div>
          ) : null}
          {sorted.length > PAGE_SIZE ? <PaginationBar total={sorted.length} page={current} pageSize={PAGE_SIZE} onPageChange={setPage} singular="aplicativo" pluralForm="aplicativos" /> : null}
        </>
      )}

      <AddAppDialog open={adding} onOpenChange={setAdding} onAdded={reloadAll} />
      <AppHistoryDialog app={shownHistory} open={historyOf !== null} onOpenChange={(open) => !open && setHistoryOf(null)} />
      <AliasesDialog app={shownAliases} open={aliasesOf !== null} onOpenChange={(open) => !open && setAliasesOf(null)} onSaved={onAliasesSaved} />
      <EditGroupsDialog app={shownEditing} open={editing !== null} onOpenChange={(open) => !open && setEditing(null)} onSaved={onGroupsSaved} />
      <BulkGroupsDialog mode={bulk} apps={selectedRows} onOpenChange={(open) => !open && setBulk(null)} onDone={onBulkDone} />

      {confirm ? (
        <ConfirmDialog
          open={dialog?.kind === 'upgrade' || dialog?.kind === 'remove'}
          onOpenChange={(open) => !open && setDialog(null)}
          title={confirm.title}
          description={confirm.description}
          confirmLabel={confirm.label}
          destructive={confirm.destructive}
          onConfirm={confirm.run}
        />
      ) : null}

      <Dialog open={dialog?.kind === 'skipped'} onOpenChange={(open) => !open && setDialog(null)}>
        <DialogContent>
          {shown?.kind === 'skipped' ? (
            <>
              <DialogHeader>
                <DialogTitle>Máquinas puladas</DialogTitle>
                <DialogDescription>Estas máquinas ficaram de fora da atualização de {shown.title}. Elas entram na próxima.</DialogDescription>
              </DialogHeader>
              <ul className="grid max-h-64 gap-1.5 overflow-auto pl-5 text-sm [list-style:disc]">
                {shown.messages.map((message) => (
                  <li key={message}>{message}</li>
                ))}
              </ul>
              <DialogFooter>
                <Button onClick={() => setDialog(null)}>Entendi</Button>
              </DialogFooter>
            </>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  )
}
