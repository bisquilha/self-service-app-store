import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { CircleCheckIcon, CircleXIcon, EyeOffIcon, PackageOpenIcon, UserSearchIcon } from 'lucide-react'
import { AppIcon } from '@/components/app-icon'
import { PageHeader } from '@/components/shell/page-header'
import { Badge } from '@/components/ui/badge'
import { Chip } from '@/components/ui/chip'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useApi } from '@/hooks/use-api'
import { api } from '@/lib/api'
import { formatAgo, plural, shortLogin } from '@/lib/format'
import type { AdUser, CatalogItem, Preview } from '@/lib/types'
import { describeState } from '@/lib/versions'
import { UserList, UserSearch } from './user-search'

/** Endereço da tela para uma pessoa (e, se houver, a estação). O `@@` separa as duas partes. */
function previewPath(login: string, host?: string): string {
  return `/ver-como/${encodeURIComponent(login)}${host ? `@@${encodeURIComponent(host)}` : ''}`
}

export function PreviewPage() {
  const { user: param } = useParams()
  const navigate = useNavigate()
  const [login = '', host = ''] = (param ?? '').split('@@')
  // Escolher a mesma pessoa de novo recarrega a tela (a rota não muda).
  const [round, setRound] = useState(0)

  function open(next: string, nextHost?: string) {
    const target = previewPath(next, nextHost)
    if (target === previewPath(login, host || undefined)) setRound((current) => current + 1)
    else navigate(target)
  }

  return (
    <div className="grid gap-4">
      <PageHeader title="Ver como usuário" />
      <UserSearch initial={login} onSubmit={(value) => open(value)} />
      <div className="max-w-3xl text-sm text-muted-foreground">
        <p>Mostra a loja como essa pessoa a vê no app: os mesmos filtros de grupo do AD e, se ela estiver com sessão aberta numa estação, o que já está instalado nela.</p>
        <p>Só leitura: nada é feito em nome da pessoa.</p>
      </div>

      {login ? (
        <PreviewResult key={`${login}|${host}|${round}`} login={login} host={host} onOpen={open} />
      ) : (
        <div className="rounded-lg border bg-card">
          <EmptyState icon={UserSearchIcon} title="Escolha uma pessoa" description="Busque pelo nome, sobrenome ou login para ver quais aplicativos aparecem na loja dela e por que os outros ficam de fora." />
        </div>
      )}
    </div>
  )
}

function PreviewResult({ login, host, onOpen }: { login: string; host: string; onOpen: (login: string, host?: string) => void }) {
  const preview = useApi<Preview>(`/preview?user=${encodeURIComponent(login)}${host ? `&host=${encodeURIComponent(host)}` : ''}`)
  const p = preview.data
  const person = usePerson(p?.user)
  const similar = useSimilarNames(preview.error ? login : null)

  if (preview.error && !p) {
    return (
      <div className="rounded-lg border bg-card">
        <ErrorState title="Não foi possível ver a loja dessa pessoa" message={preview.error} onRetry={preview.reload} />
        {similar.length > 0 ? (
          <div className="grid justify-items-center gap-2 border-t px-6 py-5">
            <p className="text-sm text-muted-foreground">Você quis dizer:</p>
            <UserList users={similar} onPick={(user) => onOpen(user.login)} />
          </div>
        ) : null}
      </div>
    )
  }

  if (!p) {
    return (
      <div className="grid gap-4" aria-busy="true">
        <Skeleton className="h-32 w-full" />
        <Skeleton className="h-6 w-72" />
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          <Skeleton className="h-36" />
          <Skeleton className="h-36" />
          <Skeleton className="h-36" />
        </div>
      </div>
    )
  }

  const short = shortLogin(p.user)
  const known = p.hostname !== null
  const withUpdate = p.visible.filter((item) => item.updateAvailable).length

  return (
    <div className="grid gap-5">
      <section aria-label="A pessoa" className="grid gap-3 rounded-xl border bg-card p-4 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="grid gap-1">
            <h2 className="text-lg font-semibold">{person?.displayName ?? p.user}</h2>
            <p className="text-[13px] text-muted-foreground">{[p.user, person?.department, person?.title].filter(Boolean).join(' · ')}</p>
          </div>
          {p.isAdmin ? (
            <Badge variant="info" title="Administradores veem todos os aplicativos ativos">
              Admin da loja: vê todos os aplicativos
            </Badge>
          ) : null}
        </div>

        {known ? (
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
            <span>Sessão aberta agora em</span>
            {p.loggedOn.length > 1 ? (
              <Select value={p.hostname ?? undefined} onValueChange={(next) => onOpen(login, next)}>
                <SelectTrigger aria-label="Estação em que a pessoa está com sessão aberta" size="sm" className="min-w-40">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {p.loggedOn.map((machine) => (
                    <SelectItem key={machine} value={machine}>
                      {machine}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : (
              <strong>{p.hostname}</strong>
            )}
            <span className="text-muted-foreground">· programas da loja verificados {formatAgo(p.inventoryUtc)}</span>
            <span className="text-muted-foreground">
              · {p.programsUtc ? `programas do Windows coletados ${formatAgo(p.programsUtc)}` : 'programas do Windows ainda não coletados (nada se afirma sobre o que está fora da loja)'}
            </span>
          </div>
        ) : (
          <p className="text-sm text-muted-foreground">Sem sessão aberta em nenhuma estação agora: os cartões aparecem sem o estado “instalado”.</p>
        )}

        <div className="grid gap-1.5">
          <p className="text-[12.5px] text-muted-foreground">Grupos que a loja usa: os marcados são os que a pessoa tem.</p>
          {p.storeGroups.length > 0 ? (
            <ul className="flex flex-wrap gap-1.5" aria-label="Grupos da loja">
              {p.storeGroups.map((g) => (
                <li key={g.group}>
                  <Badge variant={g.member ? 'success' : 'muted'}>
                    {g.member ? <CircleCheckIcon aria-hidden="true" /> : <CircleXIcon aria-hidden="true" />}
                    {g.group}
                    <span className="sr-only">{g.member ? ': faz parte' : ': não faz parte'}</span>
                  </Badge>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted-foreground">Nenhum grupo específico em uso na loja (só “todos”).</p>
          )}
        </div>
      </section>

      <section aria-labelledby="visiveis" className="grid gap-3">
        <h2 id="visiveis" className="text-[15px] font-semibold">
          Assim {short} vê a loja ({plural(p.visible.length, 'aplicativo')})
        </h2>
        {known && withUpdate > 0 ? (
          <p className="flex items-center gap-2 text-sm" role="status">
            <Badge variant="warning">{plural(withUpdate, 'aplicativo com atualização', 'aplicativos com atualização')}</Badge>
            <span className="text-muted-foreground">no computador {p.hostname}</span>
          </p>
        ) : null}
        {p.visible.length > 0 ? (
          <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {p.visible.map((item) => (
              <li key={item.id} className="grid">
                <StoreCard item={item} known={known} />
              </li>
            ))}
          </ul>
        ) : (
          <div className="rounded-lg border bg-card">
            <EmptyState icon={PackageOpenIcon} title="A loja aparece vazia para essa pessoa" description="Nenhum aplicativo está liberado para os grupos dela." />
          </div>
        )}
      </section>

      {p.hidden.length > 0 ? (
        <section aria-labelledby="ocultos" className="grid gap-3">
          <h2 id="ocultos" className="flex items-center gap-2 text-[15px] font-semibold">
            <EyeOffIcon className="size-4 text-muted-foreground" aria-hidden="true" />
            Aplicativos que não aparecem para essa pessoa ({p.hidden.length})
          </h2>
          <div className="overflow-hidden rounded-lg border bg-card">
            <Table>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <TableHead>Aplicativo</TableHead>
                  <TableHead>Motivo</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {p.hidden.map((app) => (
                  <TableRow key={app.appId}>
                    <TableCell>
                      <div className="flex min-w-48 items-center gap-2.5">
                        <AppIcon id={app.appId} title={app.title} />
                        <div className="grid min-w-0">
                          <strong>{app.title}</strong>
                          <span className="font-mono text-xs text-muted-foreground">{app.packageId}</span>
                        </div>
                      </div>
                    </TableCell>
                    <TableCell>
                      {app.disabled ? (
                        <Badge variant="muted">Aplicativo desativado na loja</Badge>
                      ) : (
                        <div className="flex flex-wrap items-center gap-1.5">
                          <span>Precisa estar em um destes grupos:</span>
                          {app.groups.map((group) => (
                            <Chip key={group}>{group}</Chip>
                          ))}
                        </div>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </section>
      ) : null}
    </div>
  )
}

/**
 * Cartão só de leitura, com o mesmo conteúdo do cartão do app de desktop. Não tem botões nem efeito de passar o mouse:
 * é o estado do aplicativo para aquela pessoa, não uma ação.
 */
function StoreCard({ item, known }: { item: CatalogItem; known: boolean }) {
  const view = describeState(item)
  return (
    <article className="flex flex-col gap-3 rounded-xl border bg-card p-4 shadow-sm">
      <div className="flex items-center gap-3">
        <AppIcon id={item.id} title={item.title} />
        <div className="grid min-w-0">
          <strong className="truncate">{item.title}</strong>
          <span className="text-xs text-muted-foreground">
            {item.source === 'internal' ? 'Interno' : 'Internet'}
            {item.version ? ` · versão ${item.version}` : ''}
          </span>
        </div>
      </div>
      <p className="line-clamp-3 text-[13px] text-muted-foreground">{item.description ?? ''}</p>
      {known ? (
        <div className="mt-auto grid gap-1.5 border-t pt-3">
          <div className="flex flex-wrap items-center gap-1.5">
            <Badge variant={view.tone}>{view.label}</Badge>
            {view.outside ? <Badge variant="outline">Fora da loja</Badge> : null}
          </div>
          <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
            {view.detail ? <span className="font-mono text-xs text-muted-foreground">{view.detail}</span> : <span />}
            {item.installedVersion && item.allowUninstall ? <span className="text-xs text-muted-foreground">Pode desinstalar</span> : null}
          </div>
        </div>
      ) : null}
    </article>
  )
}

/** Nome completo, departamento e cargo da pessoa, vindos do AD (opcional: sem isso a tela mostra só o login). */
function usePerson(login: string | undefined): AdUser | null {
  const [person, setPerson] = useState<AdUser | null>(null)
  useEffect(() => {
    if (!login) return
    const short = shortLogin(login)
    const controller = new AbortController()
    api<AdUser[]>(`/users?q=${encodeURIComponent(short)}`, { signal: controller.signal })
      .then((users) => setPerson(users.find((user) => user.login.toLowerCase() === short.toLowerCase()) ?? null))
      .catch(() => undefined)
    return () => controller.abort()
  }, [login])
  return person
}

/** Nomes parecidos para "Você quis dizer", buscados só quando a pessoa digitada não foi encontrada. */
function useSimilarNames(login: string | null): AdUser[] {
  const [users, setUsers] = useState<AdUser[]>([])
  useEffect(() => {
    if (!login) return
    const controller = new AbortController()
    api<AdUser[]>(`/users?q=${encodeURIComponent(login.split(/[.\s]/)[0])}`, { signal: controller.signal })
      .then((found) => setUsers(found.slice(0, 8)))
      .catch(() => undefined)
    return () => controller.abort()
  }, [login])
  return users
}
