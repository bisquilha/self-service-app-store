import { useMemo } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { CopyIcon, UsersRoundIcon } from 'lucide-react'
import { AppIcon } from '@/components/app-icon'
import { GroupPicker } from '@/components/group-picker'
import { PageHeader } from '@/components/shell/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Chip } from '@/components/ui/chip'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { StatCard } from '@/components/ui/stat-card'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { useApi } from '@/hooks/use-api'
import { copyText } from '@/lib/clipboard'
import { plural } from '@/lib/format'
import type { AdminApp, GroupApp, GroupDetails, GroupMember } from '@/lib/types'

const TABS = ['liberados', 'sem-acesso', 'membros'] as const
type Tab = (typeof TABS)[number]

function HowItArrives({ app }: { app: GroupApp }) {
  if (app.origin === 'group') return <Badge variant="info">Pelo próprio grupo</Badge>
  if (app.origin === 'nested') return <Badge variant="muted">Via {app.via}</Badge>
  if (app.origin === 'everyone') return <Badge variant="muted">Todos os usuários</Badge>
  return <span className="text-muted-foreground">—</span>
}

function AppsTab({ rows, withHow, empty }: { rows: GroupApp[]; withHow: boolean; empty: string }) {
  if (rows.length === 0) return <EmptyState icon={UsersRoundIcon} title={empty} className="py-8" />
  return (
    <Table>
      <TableHeader>
        <TableRow className="hover:bg-transparent">
          <TableHead>Aplicativo</TableHead>
          {withHow ? <TableHead>Como chega ao grupo</TableHead> : null}
          <TableHead>Na loja</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((app) => (
          <TableRow key={app.id}>
            <TableCell>
              <div className="flex min-w-48 items-center gap-2.5">
                <AppIcon id={app.id} title={app.title} />
                <div className="grid min-w-0">
                  <Link to={`/aplicativos?q=${encodeURIComponent(app.packageId)}`} className="font-semibold underline-offset-4 hover:underline focus-visible:underline">
                    {app.title}
                  </Link>
                  <span className="font-mono text-xs text-muted-foreground">{app.packageId}</span>
                </div>
              </div>
            </TableCell>
            {withHow ? (
              <TableCell>
                <HowItArrives app={app} />
              </TableCell>
            ) : null}
            <TableCell>{app.enabled ? <Badge variant="success">Ativo</Badge> : <Badge variant="muted">Desativado · não aparece</Badge>}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function MembersTab({ details }: { details: GroupDetails }) {
  if (!details.membersListed) {
    return (
      <EmptyState
        icon={UsersRoundIcon}
        className="py-8"
        title="O AD não lista os membros deste grupo"
        description="É um grupo primário (como Domain Users): as pessoas o têm como grupo principal, e o AD não guarda essa lista no grupo. Quem o tem como principal recebe o acesso do mesmo jeito."
      />
    )
  }
  if (details.members.length === 0) {
    return (
      <EmptyState
        icon={UsersRoundIcon}
        className="py-8"
        title="Nenhuma pessoa como membro direto"
        description={
          details.nestedGroups.length
            ? 'As pessoas entram por grupos que estão dentro deste. Veja “Contém os grupos” acima.'
            : 'Isso não prova que ninguém tem acesso: pessoas de grupos que contêm este grupo, ou de grupos dentro dele, também contam.'
        }
      />
    )
  }
  return (
    <>
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead>Usuário</TableHead>
            <TableHead>Nome</TableHead>
            <TableHead>Setor</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {details.members.map((member: GroupMember) => (
            <TableRow key={member.login}>
              <TableCell className="font-mono text-[12.5px]">{member.login}</TableCell>
              <TableCell>{member.displayName ?? '—'}</TableCell>
              <TableCell>{member.department ?? '—'}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {details.members.length < details.membersTotal ? (
        <p className="border-t px-3 py-2 text-xs text-muted-foreground">
          A lista mostra só pessoas, até 200. O AD informa {plural(details.membersTotal, 'membro direto', 'membros diretos')}, contando também grupos dentro deste.
        </p>
      ) : null}
    </>
  )
}

function Relations({ details }: { details: GroupDetails }) {
  if (!details.parentGroups.length && !details.nestedGroups.length) return null
  const links = (items: { name: string; sid: string }[]) =>
    items.map((item) => (
      <Link key={item.sid} to={`/grupos/${encodeURIComponent(item.sid)}`} className="rounded-full outline-none focus-visible:ring-2 focus-visible:ring-ring" title="Abrir este grupo">
        <Chip>{item.name}</Chip>
      </Link>
    ))
  return (
    <dl className="grid gap-2 text-sm">
      {details.parentGroups.length ? (
        <div className="flex flex-wrap items-center gap-1.5">
          <dt className="text-muted-foreground">Está dentro de</dt>
          <dd className="flex flex-wrap gap-1.5">{links(details.parentGroups)}</dd>
        </div>
      ) : null}
      {details.nestedGroups.length ? (
        <div className="flex flex-wrap items-center gap-1.5">
          <dt className="text-muted-foreground">Contém os grupos</dt>
          <dd className="flex flex-wrap gap-1.5">{links(details.nestedGroups)}</dd>
        </div>
      ) : null}
    </dl>
  )
}

function DetailsView({ details }: { details: GroupDetails }) {
  const [params, setParams] = useSearchParams()
  const requested = params.get('aba')
  const tab: Tab = TABS.includes(requested as Tab) ? (requested as Tab) : 'liberados'
  const { group } = details

  const direct = details.apps.filter((app) => app.origin === 'group').length
  const nested = details.apps.filter((app) => app.origin === 'nested').length
  const everyone = details.apps.filter((app) => app.origin === 'everyone').length
  const how = [direct && `${direct} pelo grupo`, nested && `${nested} via outro grupo`, everyone && `${everyone} para todos`].filter(Boolean).join(' · ')

  async function copySid() {
    if (await copyText(group.sid)) toast.success('SID copiado.')
    else toast.error('Não foi possível copiar. Selecione o texto e copie.')
  }

  return (
    <div className="grid gap-4">
      <section aria-label={`Resumo de ${group.name}`} className="grid gap-3 rounded-xl border bg-card p-4 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="grid min-w-0 gap-0.5">
            <h2 className="text-lg font-semibold">{group.name}</h2>
            {group.description ? <p className="text-muted-foreground">{group.description}</p> : null}
            <div className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
              <span>SID</span>
              <code className="font-mono break-all">{group.sid}</code>
              <Button type="button" variant="ghost" size="icon-xs" aria-label="Copiar o SID do grupo" title="Copiar o SID" onClick={() => void copySid()}>
                <CopyIcon aria-hidden="true" />
              </Button>
            </div>
          </div>
          <Button asChild variant="secondary">
            <Link to={`/aplicativos?grupo=${encodeURIComponent(group.sid)}`}>Ver na lista de aplicativos</Link>
          </Button>
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <StatCard
            label="Membros diretos"
            value={details.membersListed ? details.membersTotal.toLocaleString('pt-BR') : '—'}
            tone={details.membersListed && details.membersTotal === 0 ? 'warning' : 'default'}
            hint={details.membersListed ? (details.membersTotal === 0 ? 'Isso não prova que ninguém tem acesso' : 'Pessoas e grupos dentro dele') : 'O AD não lista (grupo primário)'}
          />
          <StatCard label="Aplicativos liberados" value={details.apps.length} hint={how || 'Nenhum aplicativo chega a este grupo'} />
          <StatCard label="Aplicativos que não vê" value={details.withoutAccess.length} hint="Não estão liberados para este grupo" />
        </div>

        <Relations details={details} />
      </section>

      <Tabs value={tab} onValueChange={(value) => setParams(value === 'liberados' ? {} : { aba: value }, { replace: true })}>
        <TabsList className="max-w-full justify-start overflow-x-auto">
          <TabsTrigger value="liberados" className="px-3">
            Aplicativos liberados · {details.apps.length}
          </TabsTrigger>
          <TabsTrigger value="sem-acesso" className="px-3">
            Não vê · {details.withoutAccess.length}
          </TabsTrigger>
          <TabsTrigger value="membros" className="px-3">
            Membros diretos
          </TabsTrigger>
        </TabsList>
        <TabsContent value="liberados" className="overflow-hidden rounded-lg border bg-card">
          <AppsTab rows={details.apps} withHow empty="Nenhum aplicativo está liberado para este grupo" />
        </TabsContent>
        <TabsContent value="sem-acesso" className="overflow-hidden rounded-lg border bg-card">
          <AppsTab rows={details.withoutAccess} withHow={false} empty="Este grupo vê todos os aplicativos da loja" />
        </TabsContent>
        <TabsContent value="membros" className="overflow-hidden rounded-lg border bg-card">
          <MembersTab details={details} />
        </TabsContent>
      </Tabs>
    </div>
  )
}

/** Grupos que os aplicativos da loja já usam, com a quantidade de aplicativos de cada um. Serve de atalho. */
function UsedGroups() {
  const apps = useApi<AdminApp[]>('/apps')
  const used = useMemo(() => {
    const bySid = new Map<string, { name: string; sid: string; count: number }>()
    for (const app of apps.data ?? []) {
      for (const group of app.groupRefs) {
        if (!group.sid) continue
        const entry = bySid.get(group.sid)
        if (entry) entry.count++
        else bySid.set(group.sid, { name: group.name, sid: group.sid, count: 1 })
      }
    }
    return [...bySid.values()].sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'))
  }, [apps.data])

  if (apps.loading && !apps.data) return <Skeleton className="h-16 w-full" />
  if (used.length === 0) return null
  return (
    <section aria-label="Grupos que a loja usa" className="grid gap-2">
      <h2 className="text-[12.5px] font-medium text-muted-foreground">Grupos que a loja já usa</h2>
      <ul className="flex flex-wrap gap-2">
        {used.map((group) => (
          <li key={group.sid}>
            <Link to={`/grupos/${encodeURIComponent(group.sid)}`} className="inline-flex items-center gap-1.5 rounded-lg border bg-card px-3 py-1.5 outline-none hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring">
              <strong className="text-[13px]">{group.name}</strong>
              <span className="text-xs text-muted-foreground">{plural(group.count, 'aplicativo')}</span>
            </Link>
          </li>
        ))}
      </ul>
    </section>
  )
}

/**
 * Tela Grupos (`#/grupos/SID`): escolhe um grupo do AD e mostra o que a loja libera para quem está nele — pelo próprio grupo,
 * por um grupo que o contém, ou por ser liberado para todos — e o que ele não vê. Só leitura.
 */
export function GroupsPage() {
  const { sid } = useParams()
  const navigate = useNavigate()
  const details = useApi<GroupDetails>(sid ? `/groups/${encodeURIComponent(sid)}` : null)

  // Ao trocar de grupo o hook guarda o dado anterior até chegar o novo: só vale o que é do grupo da URL.
  const current = details.data && sid && details.data.group.sid.toLowerCase() === sid.toLowerCase() ? details.data : null

  return (
    <div className="grid gap-4">
      <PageHeader title="Grupos" />

      <div className="grid max-w-xl gap-1.5">
        <label htmlFor="grupo-escolhido" className="text-[12.5px] text-muted-foreground">
          Grupo do AD
        </label>
        <GroupPicker
          id="grupo-escolhido"
          value={sid ? { name: current?.group.name ?? 'Carregando…', sid } : null}
          onChange={(group) => navigate(group?.sid ? `/grupos/${encodeURIComponent(group.sid)}` : '/grupos')}
          placeholder="Escolher um grupo…"
          aria-label="Grupo do AD"
        />
      </div>

      {!sid ? (
        <>
          <UsedGroups />
          <div className="rounded-lg border bg-card">
            <EmptyState
              icon={UsersRoundIcon}
              title="Escolha um grupo"
              description="Veja quais aplicativos a loja libera para quem está nele, quem são os membros diretos e o que o grupo não vê."
            />
          </div>
        </>
      ) : details.error && !current ? (
        <div className="rounded-lg border bg-card">
          <ErrorState title="Não foi possível abrir o grupo" message={details.error} onRetry={details.reload} />
        </div>
      ) : current ? (
        <DetailsView key={current.group.sid} details={current} />
      ) : (
        <div className="grid gap-3" aria-busy="true">
          <Skeleton className="h-40 w-full" />
          <Skeleton className="h-48 w-full" />
        </div>
      )}
    </div>
  )
}
