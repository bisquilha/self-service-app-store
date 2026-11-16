import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { ChevronRightIcon, RotateCwIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { useApi } from '@/hooks/use-api'
import { plural } from '@/lib/format'
import type { Machine } from '@/lib/types'

const NO_SITE = 'Sem site'
const LIST_LIMIT = 40

type SiteRow = { site: string; total: number; withStore: number; without: string[] }

/** Estações por site e quantas já têm a loja. Só estações (a loja nunca age em servidores). Números reais do Tactical e do que as máquinas informaram. */
function bySite(machines: Machine[]): SiteRow[] {
  const sites = new Map<string, SiteRow>()
  for (const m of machines) {
    if (m.blockReason) continue
    const name = m.site?.trim() || NO_SITE
    const row = sites.get(name) ?? { site: name, total: 0, withStore: 0, without: [] }
    row.total++
    if (m.appVersion) row.withStore++
    else row.without.push(m.hostname)
    sites.set(name, row)
  }
  return [...sites.values()]
    .map((row) => ({ ...row, without: [...new Set(row.without)].sort((a, b) => a.localeCompare(b, 'pt-BR')) }))
    .sort((a, b) => b.total - a.total || a.site.localeCompare(b.site, 'pt-BR'))
}

/**
 * Adoção da loja por site. "Com a loja" = a loja (o app do funcionário) já foi aberta na máquina e informou a versão. Versões antigas
 * do app não informam, então essas máquinas entram como "sem a loja" até a loja ser aberta numa versão que informa.
 */
export function AdoptionCard() {
  const machines = useApi<Machine[]>('/machines')
  const [open, setOpen] = useState<Set<string>>(new Set())
  const rows = useMemo(() => bySite(machines.data ?? []), [machines.data])
  const total = rows.reduce((sum, row) => sum + row.total, 0)
  const withStore = rows.reduce((sum, row) => sum + row.withStore, 0)

  function toggle(site: string) {
    setOpen((current) => {
      const next = new Set(current)
      if (next.has(site)) next.delete(site)
      else next.add(site)
      return next
    })
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Adoção da loja por site</CardTitle>
        <CardDescription>
          {machines.data
            ? total === 0
              ? 'Nenhuma estação no Tactical.'
              : `${withStore} de ${plural(total, 'estação', 'estações')} já abriram a loja.`
            : 'Contando as estações do Tactical…'}
        </CardDescription>
        <CardAction>
          <Button variant="ghost" size="sm" loading={machines.loading && machines.data !== null} onClick={machines.reload}>
            <RotateCwIcon aria-hidden="true" />
            Atualizar
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent>
        {machines.error && !machines.data ? (
          <ErrorState title="Não foi possível carregar as máquinas" message={machines.error} onRetry={machines.reload} className="py-6" />
        ) : !machines.data ? (
          <div className="grid gap-3" aria-busy="true">
            {Array.from({ length: 3 }, (_, index) => (
              <Skeleton key={index} className="h-10 w-full" />
            ))}
          </div>
        ) : rows.length === 0 ? null : (
          <>
            <ul className="grid gap-3">
              {rows.map((row) => {
                const percent = Math.round((row.withStore / row.total) * 100)
                const expanded = open.has(row.site)
                const missing = row.total - row.withStore
                return (
                  <li key={row.site} className="grid gap-1.5">
                    <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5">
                      <strong>{row.site}</strong>
                      <span className="text-[13px] text-muted-foreground">
                        {row.withStore} de {plural(row.total, 'estação', 'estações')} · {percent}%
                      </span>
                    </div>
                    <div className="h-2 overflow-hidden rounded-full bg-muted" role="img" aria-label={`${row.site}: ${row.withStore} de ${plural(row.total, 'estação', 'estações')} com a loja (${percent}%)`}>
                      <div className="h-full rounded-full bg-primary" style={{ width: `${percent}%` }} />
                    </div>
                    {missing > 0 ? (
                      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                        <Button variant="ghost" size="xs" aria-expanded={expanded} onClick={() => toggle(row.site)} className="-ml-2 text-muted-foreground">
                          <ChevronRightIcon className={expanded ? 'rotate-90 transition-transform' : 'transition-transform'} aria-hidden="true" />
                          {plural(missing, 'estação ainda sem a loja', 'estações ainda sem a loja')}
                        </Button>
                        {row.site !== NO_SITE ? (
                          <Link to={`/maquinas?site=${encodeURIComponent(row.site)}&loja=sem`} className="text-[13px] text-primary underline-offset-4 hover:underline focus-visible:underline">
                            Abrir na lista de Máquinas
                          </Link>
                        ) : null}
                      </div>
                    ) : null}
                    {expanded ? (
                      <ul aria-label={`Estações sem a loja em ${row.site}`} className="flex flex-wrap gap-1.5 pb-1">
                        {row.without.slice(0, LIST_LIMIT).map((host) => (
                          <li key={host}>
                            <Link to={`/maquinas/${encodeURIComponent(host)}`} className="rounded-full border bg-muted px-2.5 py-0.5 font-mono text-xs outline-none hover:bg-muted/70 focus-visible:ring-2 focus-visible:ring-ring">
                              {host}
                            </Link>
                          </li>
                        ))}
                        {row.without.length > LIST_LIMIT ? <li className="px-1 text-xs text-muted-foreground">e mais {row.without.length - LIST_LIMIT}</li> : null}
                      </ul>
                    ) : null}
                  </li>
                )
              })}
            </ul>
            <p className="mt-4 text-xs text-muted-foreground">
              Conta as estações em que a loja já foi aberta e informou a versão. Máquinas com uma versão antiga da loja, que não informa, aparecem como “sem a loja” até ela ser aberta de novo.
            </p>
          </>
        )}
      </CardContent>
    </Card>
  )
}
