import { useEffect, useState, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { DownloadIcon, HistoryIcon, SearchXIcon } from 'lucide-react'
import { GroupPicker } from '@/components/group-picker'
import { RequestsTable } from '@/components/requests/requests-table'
import { PageHeader } from '@/components/shell/page-header'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { PaginationBar } from '@/components/ui/pagination-bar'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useApi } from '@/hooks/use-api'
import { plural } from '@/lib/format'
import { ACTION_LABELS, PROBLEM_STATUSES, STATUS_META, statusLabel } from '@/lib/requests'
import type { SortState } from '@/lib/sort'
import type { RequestRow } from '@/lib/types'
import { cn } from '@/lib/utils'

type HistoryResponse = { total: number; page: number; size: number; items: RequestRow[] }

const PAGE_SIZE = 50
/** Colunas que o servidor sabe ordenar (GET /history?sort=). */
const SORT_KEYS = ['created', 'user', 'host', 'app', 'action', 'status']
const FILTER_ACTIONS = ['install', 'upgrade', 'uninstall']
const ALL = 'all'

/** "2026-10-21" → "21/10/2026", sem passar por Date (fuso não pode mudar o dia). */
function brDate(day: string): string {
  const [year, month, date] = day.split('-')
  return year && month && date ? `${date}/${month}/${year}` : day
}

function Field({ label, htmlFor, children }: { label: string; htmlFor: string; children: ReactNode }) {
  return (
    <div className="grid content-start gap-1.5">
      <Label htmlFor={htmlFor} className="text-[12.5px] font-normal text-muted-foreground">
        {label}
      </Label>
      {children}
    </div>
  )
}

/**
 * Histórico de pedidos. Os filtros, a ordem e a página ficam na URL (`#/historico?status=failed&page=2`): o link
 * pode ser guardado ou mandado a alguém, e o botão Voltar do navegador desfaz o último filtro.
 */
export function HistoryPage() {
  const [params, setParams] = useSearchParams()

  const filters = {
    text: params.get('text') ?? '',
    status: params.get('status') ?? '',
    action: params.get('action') ?? '',
    host: params.get('host') ?? '',
    from: params.get('from') ?? '',
    to: params.get('to') ?? '',
  }
  // Grupo do usuário: o SID vai ao servidor (`group`); o nome fica na URL só para o chip e o botão mostrarem algo legível.
  const groupSid = params.get('grupo') ?? ''
  const groupName = params.get('grupoNome') ?? ''
  const page = Math.max(1, Math.floor(Number(params.get('page'))) || 1)
  const requestedKey = params.get('sort') ?? ''
  const sort: SortState = {
    key: SORT_KEYS.includes(requestedKey) ? requestedKey : 'created',
    direction: params.get('dir') === 'asc' ? 'asc' : 'desc',
  }
  const active = Object.values(filters).some(Boolean) || groupSid !== ''

  /** Muda parâmetros da URL (valor vazio apaga). Mudar filtro ou ordem volta à primeira página. */
  function update(changes: Record<string, string | null>, replace = false) {
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const [key, value] of Object.entries(changes)) {
          if (value) next.set(key, value)
          else next.delete(key)
        }
        if (!('page' in changes)) next.delete('page')
        return next
      },
      { replace },
    )
  }

  // A busca por texto espera a pessoa parar de digitar para não pedir ao servidor a cada tecla.
  const [draft, setDraft] = useState(filters.text)
  useEffect(() => {
    setDraft((current) => (current.trim() === filters.text ? current : filters.text))
  }, [filters.text])
  useEffect(() => {
    if (draft.trim() === filters.text) return
    const timer = setTimeout(() => update({ text: draft.trim() || null }, true), 350)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft])

  const query = new URLSearchParams(Object.entries(filters).filter(([, value]) => value))
  if (groupSid) query.set('group', groupSid)
  query.set('sort', sort.key)
  query.set('dir', sort.direction)
  const history = useApi<HistoryResponse>(`/history?${query}&page=${page}&size=${PAGE_SIZE}`)
  const exportHref = `/api/admin/history.csv?${query}`

  const data = history.data
  const lastPage = data ? Math.max(1, Math.ceil(data.total / data.size)) : 1
  // Filtro novo pode deixar a página pedida além do fim: volta para a última que existe.
  useEffect(() => {
    if (data && page > lastPage) update({ page: lastPage > 1 ? String(lastPage) : null }, true)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, page, lastPage])

  function toggleSort(key: string) {
    const direction = sort.key === key ? (sort.direction === 'asc' ? 'desc' : 'asc') : key === 'created' ? 'desc' : 'asc'
    const standard = key === 'created' && direction === 'desc'
    update({ sort: standard ? null : key, dir: standard ? null : direction })
  }

  function clearFilters() {
    update({ text: null, status: null, action: null, host: null, from: null, to: null, grupo: null, grupoNome: null })
  }

  const chips: { id: string; label: string; remove: () => void }[] = []
  if (filters.text) chips.push({ id: 'text', label: `Busca: “${filters.text}”`, remove: () => update({ text: null }) })
  if (filters.status) chips.push({ id: 'status', label: `Situação: ${statusLabel(filters.status)}`, remove: () => update({ status: null }) })
  if (filters.action) chips.push({ id: 'action', label: `Ação: ${ACTION_LABELS[filters.action] ?? filters.action}`, remove: () => update({ action: null }) })
  if (filters.host) chips.push({ id: 'host', label: `Computador: ${filters.host}`, remove: () => update({ host: null }) })
  if (groupSid) chips.push({ id: 'grupo', label: `Grupo do usuário: ${groupName || groupSid}`, remove: () => update({ grupo: null, grupoNome: null }) })
  if (filters.from) chips.push({ id: 'from', label: `De ${brDate(filters.from)}`, remove: () => update({ from: null }) })
  if (filters.to) chips.push({ id: 'to', label: `Até ${brDate(filters.to)}`, remove: () => update({ to: null }) })

  const failed = history.error && !data
  const refreshing = history.loading && data !== null

  return (
    <div className="grid gap-4">
      <PageHeader
        title="Histórico"
        actions={
          <Button asChild variant="secondary" title="Baixa até 5.000 pedidos, com os filtros e a ordem desta tela">
            <a href={exportHref}>
              <DownloadIcon aria-hidden="true" />
              Exportar para Excel (CSV)
            </a>
          </Button>
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div className="sm:col-span-2">
          <Field label="Buscar" htmlFor="historico-busca">
            <Input
              id="historico-busca"
              type="search"
              placeholder="Usuário, computador, app ou mensagem…"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter') update({ text: draft.trim() || null }, true)
              }}
            />
          </Field>
        </div>
        <Field label="Situação" htmlFor="historico-situacao">
          <Select value={filters.status || ALL} onValueChange={(value) => update({ status: value === ALL ? null : value })}>
            <SelectTrigger id="historico-situacao" className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>Todas as situações</SelectItem>
              {Object.entries(STATUS_META).map(([key, meta]) => (
                <SelectItem key={key} value={key}>
                  {meta.label}
                </SelectItem>
              ))}
              <SelectItem value={PROBLEM_STATUSES}>Falhou ou sem retorno</SelectItem>
              {/* combinação vinda da URL que não está na lista: mostra o que está valendo */}
              {filters.status && filters.status !== PROBLEM_STATUSES && !STATUS_META[filters.status] ? <SelectItem value={filters.status}>{statusLabel(filters.status)}</SelectItem> : null}
            </SelectContent>
          </Select>
        </Field>
        <Field label="Ação" htmlFor="historico-acao">
          <Select value={filters.action || ALL} onValueChange={(value) => update({ action: value === ALL ? null : value })}>
            <SelectTrigger id="historico-acao" className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>Todas as ações</SelectItem>
              {FILTER_ACTIONS.map((key) => (
                <SelectItem key={key} value={key}>
                  {ACTION_LABELS[key]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <div className="sm:col-span-2">
          <Field label="Grupo do usuário" htmlFor="historico-grupo">
            <GroupPicker
              id="historico-grupo"
              value={groupSid ? { name: groupName || groupSid, sid: groupSid } : null}
              onChange={(group) => update({ grupo: group?.sid ?? null, grupoNome: group?.sid ? group.name : null })}
              placeholder="Todos os grupos"
              clearLabel="Tirar o filtro de grupo"
              aria-label="Grupo do usuário"
            />
          </Field>
        </div>
        <Field label="De" htmlFor="historico-de">
          <Input id="historico-de" type="date" value={filters.from} max={filters.to || undefined} onChange={(event) => update({ from: event.target.value || null })} />
        </Field>
        <Field label="Até" htmlFor="historico-ate">
          <Input id="historico-ate" type="date" value={filters.to} min={filters.from || undefined} onChange={(event) => update({ to: event.target.value || null })} />
        </Field>
      </div>

      {chips.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Filtros ligados">
          {chips.map((chip) => (
            <Chip key={chip.id} onRemove={chip.remove} removeLabel={`Tirar o filtro: ${chip.label}`}>
              {chip.label}
            </Chip>
          ))}
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            Limpar filtros
          </Button>
        </div>
      ) : null}

      {failed ? (
        <div className="rounded-lg border bg-card">
          <ErrorState title="Não foi possível carregar o histórico" message={history.error!} onRetry={history.reload} />
        </div>
      ) : (
        <>
          <p aria-live="polite" className="text-sm text-muted-foreground">
            {data ? `${plural(data.total, 'pedido')}${active ? ' com esses filtros' : ''}` : 'Carregando…'}
            {groupSid ? ' · O filtro de grupo vale pela associação de hoje (consulta o AD, com cache de 10 minutos), não pela do dia do pedido.' : ''}
          </p>
          <div className={cn('transition-opacity', refreshing && 'opacity-60')}>
            <RequestsTable
              rows={data?.items ?? []}
              loading={history.loading && !data}
              sort={sort}
              onSort={toggleSort}
              onRetried={history.reload}
              empty={
                <div className="rounded-lg border bg-card">
                  {active ? (
                    <EmptyState
                      icon={SearchXIcon}
                      title="Nenhum pedido com esses filtros"
                      description="Nenhum pedido combina com a busca. Tire algum filtro para ver mais."
                      action={
                        <Button variant="secondary" onClick={clearFilters}>
                          Limpar filtros
                        </Button>
                      }
                    />
                  ) : (
                    <EmptyState icon={HistoryIcon} title="Nenhum pedido ainda" description="Os pedidos de instalar, atualizar e desinstalar aparecem aqui." />
                  )}
                </div>
              }
            />
          </div>
          {data && data.total > PAGE_SIZE ? (
            <PaginationBar total={data.total} page={Math.min(page, lastPage)} pageSize={PAGE_SIZE} onPageChange={(next) => update({ page: next > 1 ? String(next) : null })} singular="pedido" />
          ) : null}
        </>
      )}
    </div>
  )
}
