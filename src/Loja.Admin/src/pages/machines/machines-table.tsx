import { Link, useLocation } from 'react-router-dom'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { SortableHead } from '@/components/ui/sortable-head'
import { StatusDot } from '@/components/ui/status-dot'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { formatAgo, formatDateTime, plural, shortLogin, shortOs } from '@/lib/format'
import type { SortState } from '@/lib/sort'
import type { Machine } from '@/lib/types'
import { compareVersions } from '@/lib/version'

const STATUS_TEXT: Record<string, string> = { online: 'online', offline: 'offline', overdue: 'sem contato' }
const STATUS_TONE = { online: 'success', overdue: 'warning', offline: 'muted' } as const

type Props = {
  rows: Machine[]
  loading: boolean
  sort: SortState | null
  onSort: (key: string) => void
  onOpen: (hostname: string) => void
  /** Nomes (em maiúsculas) em que a loja escolheu um agente: os outros agentes de mesmo nome são registros antigos. */
  withPreferred: Set<string>
  /** Versão do app da loja que está publicada como atual (para marcar as máquinas com versão antiga). */
  currentStore: string | null
  /** Seleção para "Verificar programas" em lote (nomes dos computadores). Servidores não se selecionam: a loja não age neles. */
  selected: ReadonlySet<string>
  onSelect: (hostnames: string[], on: boolean) => void
}

/** Selos de situação da máquina: bloqueada e/ou nome duplicado no Tactical. Uma coluna só, para as linhas não variarem de altura. */
function Notes({ machine, withPreferred }: { machine: Machine; withPreferred: Set<string> }) {
  const notes = []
  if (machine.blockReason) {
    notes.push(
      <Badge key="blocked" variant="muted" title={`A loja não age nesta máquina: ${machine.blockReason}`}>
        Bloqueada
      </Badge>,
    )
  }
  if (machine.sameNameCount > 1) {
    if (machine.preferred) {
      notes.push(
        <Badge key="dup" variant="warning" title="Há mais de um agente com este nome no Tactical. A loja usa este.">
          duplicado · em uso
        </Badge>,
      )
    } else if (withPreferred.has(machine.hostname.toUpperCase())) {
      notes.push(
        <Badge key="dup" variant="muted" title="Registro antigo: a loja ignora. Pode ser apagado no Tactical.">
          registro antigo
        </Badge>,
      )
    } else {
      notes.push(
        <Badge key="dup" variant="destructive" title="Mais de um agente online com este nome: a loja recusa até sobrar um.">
          duplicado · recusada
        </Badge>,
      )
    }
  }
  return notes.length ? <div className="flex flex-wrap gap-1">{notes}</div> : <span className="text-muted-foreground">—</span>
}

function StoreVersion({ machine, current }: { machine: Machine; current: string | null }) {
  if (!machine.appVersion) {
    return (
      <span className="text-muted-foreground" title="Loja nunca aberta (ou versão antiga, que não informa)">
        —
      </span>
    )
  }
  const old = current !== null && compareVersions(machine.appVersion, current) < 0
  return (
    <span className="inline-flex items-center gap-1.5" title={machine.appSeenUtc ? `Loja aberta ${formatAgo(machine.appSeenUtc)}` : undefined}>
      <span className="font-mono text-[12.5px]">{machine.appVersion}</span>
      {old ? (
        <Badge variant="warning" title={`A versão atual é a ${current}. Esta loja atualiza na próxima vez que abrir.`}>
          antiga
        </Badge>
      ) : null}
    </span>
  )
}

export function MachinesTable({ rows, loading, sort, onSort, onOpen, withPreferred, currentStore, selected, onSelect }: Props) {
  // Abrir uma máquina mantém os filtros da lista na URL (a gaveta abre por cima, a lista não muda).
  const { search } = useLocation()
  const selectable = rows.filter((m) => !m.blockReason)
  const pageSelected = selectable.filter((m) => selected.has(m.hostname)).length
  const allSelected = selectable.length > 0 && pageSelected === selectable.length

  return (
    <div className="overflow-hidden rounded-lg border bg-card">
      <Table aria-busy={loading} className="[&_td]:px-2 [&_th]:px-2">
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead className="w-10 pl-3">
              <Checkbox
                checked={allSelected ? true : pageSelected > 0 ? 'indeterminate' : false}
                disabled={selectable.length === 0}
                onCheckedChange={(checked) => onSelect(selectable.map((m) => m.hostname), checked === true)}
                aria-label="Selecionar todas as máquinas desta página"
              />
            </TableHead>
            <SortableHead label="Computador" sortKey="hostname" sort={sort} onSort={onSort} />
            <SortableHead label="Site" sortKey="site" sort={sort} onSort={onSort} />
            <SortableHead label="Status" sortKey="status" sort={sort} onSort={onSort} />
            <SortableHead label="Usuário" sortKey="user" sort={sort} onSort={onSort} />
            <SortableHead label="Sistema" sortKey="os" sort={sort} onSort={onSort} />
            <SortableHead label="Apps" sortKey="apps" sort={sort} onSort={onSort} />
            <SortableHead label="Pendentes" sortKey="outdated" sort={sort} onSort={onSort} />
            <SortableHead label="Verificada" sortKey="inventory" sort={sort} onSort={onSort} />
            <SortableHead label="Loja" sortKey="store" sort={sort} onSort={onSort} />
            <TableHead>Situação</TableHead>
          </TableRow>
        </TableHeader>
        {loading ? (
          <TableSkeleton columns={11} rows={8} />
        ) : (
          <TableBody>
            {rows.map((m) => (
              <TableRow key={`${m.hostname}:${m.agentId}`} className="cursor-pointer" data-state={selected.has(m.hostname) ? 'selected' : undefined} onClick={() => onOpen(m.hostname)}>
                <TableCell className="pl-3" onClick={(event) => event.stopPropagation()}>
                  <Checkbox
                    checked={selected.has(m.hostname)}
                    disabled={m.blockReason !== null}
                    onCheckedChange={(checked) => onSelect([m.hostname], checked === true)}
                    aria-label={m.blockReason ? `${m.hostname}: a loja não age nesta máquina` : `Selecionar ${m.hostname}`}
                    title={m.blockReason ? `A loja não age nesta máquina: ${m.blockReason}` : undefined}
                  />
                </TableCell>
                <TableCell>
                  <div className="grid">
                    <Link
                      to={{ pathname: `/maquinas/${encodeURIComponent(m.hostname)}`, search }}
                      onClick={(event) => event.stopPropagation()}
                      className="font-semibold whitespace-nowrap outline-none hover:underline focus-visible:underline"
                    >
                      {m.hostname}
                    </Link>
                    {m.sameNameCount > 1 ? (
                      <span className="font-mono text-[11px] whitespace-nowrap text-muted-foreground" title={`Agente ${m.agentId}${m.lastSeenUtc ? ` · último contato ${formatDateTime(m.lastSeenUtc)}` : ''}`}>
                        id …{m.agentId.slice(-6)}
                        {m.lastSeenUtc ? ` · ${formatAgo(m.lastSeenUtc)}` : ''}
                      </span>
                    ) : null}
                  </div>
                </TableCell>
                <TableCell>{m.site ?? '—'}</TableCell>
                <TableCell className="whitespace-nowrap">
                  <StatusDot tone={STATUS_TONE[m.status as keyof typeof STATUS_TONE] ?? 'muted'} title={`Status no Tactical: ${m.status}`}>
                    {STATUS_TEXT[m.status] ?? m.status}
                  </StatusDot>
                </TableCell>
                <TableCell title={m.loggedUser ?? undefined}>{m.loggedUser && m.loggedUser !== '-' ? shortLogin(m.loggedUser) : <span className="text-muted-foreground">—</span>}</TableCell>
                <TableCell className="max-w-36 text-[13px]">{shortOs(m.operatingSystem)}</TableCell>
                <TableCell>{m.storeAppsInstalled}</TableCell>
                <TableCell>
                  {m.outdated ? (
                    <Badge variant="warning" title={plural(m.outdated, 'aplicativo desatualizado', 'aplicativos desatualizados')}>
                      {m.outdated}
                    </Badge>
                  ) : (
                    <span className="text-muted-foreground">0</span>
                  )}
                </TableCell>
                <TableCell className="whitespace-nowrap text-[13px] text-muted-foreground">{formatAgo(m.inventoryUtc)}</TableCell>
                <TableCell>
                  <StoreVersion machine={m} current={currentStore} />
                </TableCell>
                <TableCell>
                  <Notes machine={m} withPreferred={withPreferred} />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        )}
      </Table>
    </div>
  )
}
