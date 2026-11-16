import { ChevronDownIcon, ExternalLinkIcon, FilterIcon, TriangleAlertIcon } from 'lucide-react'
import { AppIcon } from '@/components/app-icon'
import { groupKey, groupLabel } from '@/components/group-select'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Chip } from '@/components/ui/chip'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { SortableHead } from '@/components/ui/sortable-head'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { plural } from '@/lib/format'
import type { SortState } from '@/lib/sort'
import type { AdminApp, GroupRef } from '@/lib/types'
import { cn } from '@/lib/utils'

/** Aplicativo com os números de máquinas (de GET /apps/stats) juntos, para ordenar e filtrar. */
export type AppRow = AdminApp & { installed: number; outdated: number }
export type Flag = 'enabled' | 'autoUpdate' | 'allowUninstall'
export type RowAction = 'groups' | 'aliases' | 'refresh' | 'upgrade' | 'history' | 'remove'

type Props = {
  rows: AppRow[]
  loading: boolean
  sort: SortState | null
  onSort: (key: string) => void
  onFlag: (app: AppRow, flag: Flag, value: boolean) => void
  onAction: (action: RowAction, app: AppRow) => void
  /** Seleção para editar em lote (ids dos aplicativos marcados). */
  selected: ReadonlySet<number>
  onSelect: (ids: number[], on: boolean) => void
  /** Chaves (groupKey, em minúsculas) dos grupos que estão no filtro agora. */
  activeGroups: ReadonlySet<string>
  /** Aplicativos que aparecem no filtro só por serem liberados para "Todos os usuários". */
  viaEveryone: ReadonlySet<number>
  onFilterGroup: (group: GroupRef) => void
  onSeeGroup: (group: GroupRef) => void
}

const FLAG_COLUMNS: { flag: Flag; label: string; help: string }[] = [
  { flag: 'enabled', label: 'Ativo', help: 'Desativado, o aplicativo some da loja, mas continua cadastrado' },
  { flag: 'autoUpdate', label: 'Auto-atualizar', help: 'Atualiza sozinho na janela de horário das configurações' },
  { flag: 'allowUninstall', label: 'Desinstalar', help: 'A pessoa pode desinstalar pela loja' },
]

/** Quantos chips de grupo cabem na linha antes do "+N". */
const VISIBLE_GROUPS = 2

/** Só grupos com SID têm página na tela Grupos. */
const hasPage = (group: GroupRef) => Boolean(group.sid)

function isGone(app: AdminApp, group: GroupRef) {
  return (app.missingGroups ?? []).some((name) => name.toLowerCase() === group.name.toLowerCase())
}

/** Chip que filtra a lista ao clicar. */
function GroupChipButton({ app, group, active, onFilter }: { app: AppRow; group: GroupRef; active: boolean; onFilter: (group: GroupRef) => void }) {
  const gone = isGone(app, group)
  return (
    <button
      type="button"
      onClick={() => onFilter(group)}
      aria-pressed={active}
      aria-label={`Filtrar por ${groupLabel(group)}${gone ? ' (grupo não encontrado no AD)' : ''}`}
      title={gone ? 'Grupo não encontrado no AD. Edite os grupos deste aplicativo.' : 'Filtrar por este grupo'}
      className={cn('min-w-0 rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring', active && '[&>span]:border-primary [&>span]:bg-accent')}
    >
      <Chip tone={gone ? 'warning' : 'default'}>
        {gone ? <TriangleAlertIcon className="size-3 shrink-0" aria-hidden="true" /> : null}
        {groupLabel(group)}
      </Chip>
    </button>
  )
}

function GroupChips({ app, activeGroups, viaEveryone, onFilterGroup, onSeeGroup }: Pick<Props, 'activeGroups' | 'viaEveryone' | 'onFilterGroup' | 'onSeeGroup'> & { app: AppRow }) {
  const shown = app.groupRefs.slice(0, VISIBLE_GROUPS)
  const rest = app.groupRefs.slice(VISIBLE_GROUPS)
  const isActive = (group: GroupRef) => activeGroups.has(groupKey(group).toLowerCase())

  return (
    // O tooltip lista todos os grupos, inclusive os que ficaram no "+N".
    <div title={app.groupRefs.length > VISIBLE_GROUPS ? `Grupos: ${app.groupRefs.map(groupLabel).join(', ')}` : undefined} className="grid gap-1">
      <div className="flex flex-wrap items-center gap-1">
        {shown.map((group) => (
          <GroupChipButton key={groupKey(group)} app={app} group={group} active={isActive(group)} onFilter={onFilterGroup} />
        ))}
        {rest.length > 0 ? (
          <Popover>
            <PopoverTrigger asChild>
              <button
                type="button"
                aria-label={`Mais ${plural(rest.length, 'grupo')} de ${app.title}`}
                className="shrink-0 rounded-full border border-dashed border-border px-2 py-0.5 text-xs leading-5 font-medium text-muted-foreground outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring"
              >
                +{rest.length}
              </button>
            </PopoverTrigger>
            <PopoverContent align="start" className="w-80 max-w-[calc(100vw-2rem)]">
              <p className="text-xs font-medium text-muted-foreground">Grupos de {app.title}</p>
              <ul className="grid gap-1">
                {app.groupRefs.map((group) => (
                  <li key={groupKey(group)} className="flex items-center gap-1.5">
                    <span className="min-w-0 flex-1 truncate" title={groupLabel(group)}>
                      {groupLabel(group)}
                      {isGone(app, group) ? <span className="text-warning"> · não encontrado no AD</span> : null}
                    </span>
                    <Button type="button" variant="ghost" size="xs" onClick={() => onFilterGroup(group)} aria-label={`Filtrar por ${groupLabel(group)}`}>
                      <FilterIcon aria-hidden="true" />
                      Filtrar
                    </Button>
                    {hasPage(group) ? (
                      <Button type="button" variant="ghost" size="xs" onClick={() => onSeeGroup(group)} aria-label={`Ver apps do grupo ${group.name}`}>
                        <ExternalLinkIcon aria-hidden="true" />
                        Ver apps
                      </Button>
                    ) : null}
                  </li>
                ))}
              </ul>
            </PopoverContent>
          </Popover>
        ) : null}
      </div>
      {viaEveryone.has(app.id) ? <span className="text-xs text-muted-foreground">Entra por “Todos os usuários”</span> : null}
    </div>
  )
}

function RowMenu({ app, onAction, onSeeGroup }: { app: AppRow; onAction: Props['onAction']; onSeeGroup: Props['onSeeGroup'] }) {
  const withPage = app.groupRefs.filter(hasPage)
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="secondary" size="sm" aria-label={`Ações de ${app.title}`}>
          Ações
          <ChevronDownIcon aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-64">
        <DropdownMenuItem onSelect={() => onAction('groups', app)}>Editar grupos do AD</DropdownMenuItem>
        {withPage.length === 1 ? (
          <DropdownMenuItem onSelect={() => onSeeGroup(withPage[0])}>Ver apps deste grupo</DropdownMenuItem>
        ) : withPage.length > 1 ? (
          <DropdownMenuSub>
            <DropdownMenuSubTrigger>Ver apps de um grupo</DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              {withPage.map((group) => (
                <DropdownMenuItem key={groupKey(group)} onSelect={() => onSeeGroup(group)}>
                  {group.name}
                </DropdownMenuItem>
              ))}
            </DropdownMenuSubContent>
          </DropdownMenuSub>
        ) : null}
        <DropdownMenuItem onSelect={() => onAction('aliases', app)}>
          Nomes no Windows{app.aliases?.length ? ` (${app.aliases.length})` : ''}…
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => onAction('refresh', app)}>Atualizar dados (nome, versão, ícone)</DropdownMenuItem>
        <DropdownMenuItem disabled={app.outdated === 0} onSelect={() => onAction('upgrade', app)}>
          {app.outdated ? `Atualizar ${plural(app.outdated, 'máquina desatualizada', 'máquinas desatualizadas')}` : 'Nenhuma máquina desatualizada'}
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => onAction('history', app)}>Histórico de alterações</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem variant="destructive" onSelect={() => onAction('remove', app)}>
          Remover da loja…
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

export function AppsTable({ rows, loading, sort, onSort, onFlag, onAction, selected, onSelect, activeGroups, viaEveryone, onFilterGroup, onSeeGroup }: Props) {
  const pageSelected = rows.filter((app) => selected.has(app.id)).length
  const allSelected = rows.length > 0 && pageSelected === rows.length

  return (
    <div className="overflow-hidden rounded-lg border bg-card">
      <Table aria-busy={loading} className="[&_td]:px-2 [&_th]:px-2">
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead className="w-10 pl-3">
              <Checkbox
                checked={allSelected ? true : pageSelected > 0 ? 'indeterminate' : false}
                disabled={rows.length === 0}
                onCheckedChange={(checked) => onSelect(rows.map((app) => app.id), checked === true)}
                aria-label="Selecionar todos os aplicativos desta página"
              />
            </TableHead>
            <SortableHead label="Aplicativo" sortKey="title" sort={sort} onSort={onSort} />
            <SortableHead label="Origem" sortKey="source" sort={sort} onSort={onSort} />
            <SortableHead label="Versão" sortKey="version" sort={sort} onSort={onSort} />
            <TableHead>Grupos do AD</TableHead>
            <SortableHead label="Máquinas" sortKey="installed" sort={sort} onSort={onSort} />
            <SortableHead label="Pendentes" sortKey="outdated" sort={sort} onSort={onSort} />
            {FLAG_COLUMNS.map((column) => (
              <TableHead key={column.flag} className="w-[4.5rem] text-center leading-tight whitespace-normal" title={column.help}>
                {column.label}
              </TableHead>
            ))}
            <TableHead className="w-24">
              <span className="sr-only">Ações</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        {loading ? (
          <TableSkeleton columns={11} rows={6} />
        ) : (
          <TableBody>
            {rows.map((app) => (
              <TableRow key={app.id} data-state={selected.has(app.id) ? 'selected' : undefined}>
                <TableCell className="pl-3">
                  <Checkbox checked={selected.has(app.id)} onCheckedChange={(checked) => onSelect([app.id], checked === true)} aria-label={`Selecionar ${app.title}`} />
                </TableCell>
                <TableCell>
                  <div className="flex min-w-40 items-center gap-2.5">
                    <AppIcon id={app.id} title={app.title} />
                    <div className="grid min-w-0">
                      <span className="flex flex-wrap items-center gap-1.5">
                        <strong>{app.title}</strong>
                        {app.enabled ? null : <Badge variant="muted">Desativado</Badge>}
                      </span>
                      <span className="font-mono text-xs text-muted-foreground">{app.packageId}</span>
                    </div>
                  </div>
                </TableCell>
                <TableCell>{app.source === 'internal' ? <Badge variant="info">Interno</Badge> : <Badge variant="muted">Internet</Badge>}</TableCell>
                <TableCell className="font-mono text-[12.5px]">{app.version ?? '—'}</TableCell>
                <TableCell className="max-w-72 min-w-36">
                  <GroupChips app={app} activeGroups={activeGroups} viaEveryone={viaEveryone} onFilterGroup={onFilterGroup} onSeeGroup={onSeeGroup} />
                </TableCell>
                <TableCell>{app.installed}</TableCell>
                <TableCell>
                  {app.outdated ? (
                    <Badge variant="warning" title={plural(app.outdated, 'máquina desatualizada', 'máquinas desatualizadas')}>
                      {plural(app.outdated, 'pendente')}
                    </Badge>
                  ) : (
                    <span className="text-muted-foreground">—</span>
                  )}
                </TableCell>
                {FLAG_COLUMNS.map((column) => (
                  <TableCell key={column.flag}>
                    <div className="flex justify-center">
                      <Switch checked={app[column.flag]} onCheckedChange={(value) => onFlag(app, column.flag, value)} aria-label={`${column.label}: ${app.title}`} />
                    </div>
                  </TableCell>
                ))}
                <TableCell className="text-right">
                  <RowMenu app={app} onAction={onAction} onSeeGroup={onSeeGroup} />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        )}
      </Table>
    </div>
  )
}
