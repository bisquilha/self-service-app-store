import { RefreshCwIcon } from 'lucide-react'
import { AutoUpdateButton } from '@/components/auto-update-button'
import { RequestsTable } from '@/components/requests/requests-table'
import { PageHeader } from '@/components/shell/page-header'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { StatCard } from '@/components/ui/stat-card'
import { useApi } from '@/hooks/use-api'
import { localDay, plural } from '@/lib/format'
import { PROBLEM_STATUSES } from '@/lib/requests'
import type { Overview } from '@/lib/types'
import { AdoptionCard } from './adoption-card'
import { CatalogHealthCard } from './catalog-health-card'
import { HealthCard } from './health-card'
import { RequestsChart } from './requests-chart'

export function OverviewPage() {
  const overview = useApi<Overview>('/overview')
  const o = overview.data
  const refreshing = overview.loading && o !== null

  return (
    <div className="grid gap-4">
      <PageHeader
        title="Visão geral"
        actions={
          <>
            <Button variant="secondary" loading={refreshing} onClick={overview.reload}>
              <RefreshCwIcon aria-hidden="true" />
              Atualizar
            </Button>
            <AutoUpdateButton />
          </>
        }
      />

      {overview.error && !o ? (
        <div className="rounded-lg border bg-card">
          <ErrorState title="Não foi possível carregar a visão geral" message={overview.error} onRetry={overview.reload} />
        </div>
      ) : (
        <>
          {/* Cada número leva à lista de onde ele vem, já filtrada. */}
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
            {o ? (
              <>
                <StatCard label="Aplicativos na loja" value={o.activeApps} hint={plural(o.apps, 'cadastrado')} href="#/aplicativos" />
                <StatCard label="Máquinas com apps da loja" value={o.machinesWithStore} hint={`${plural(o.machines, 'máquina verificada', 'máquinas verificadas')}`} href="#/maquinas?mostrar=store" />
                <StatCard label="Pedidos hoje" value={o.requestsToday} hint={`${plural(o.requests7d, 'pedido')} nos últimos 7 dias, contando hoje`} href={`#/historico?from=${localDay()}`} />
                <StatCard
                  label="Falhas (7 dias)"
                  value={o.failures7d}
                  tone={o.failures7d > 0 ? 'destructive' : 'default'}
                  hint="inclui “sem retorno”"
                  href={`#/historico?status=${PROBLEM_STATUSES}&from=${localDay(6)}`}
                />
                <StatCard
                  label="Atualizações pendentes"
                  value={o.pendingUpdates}
                  tone={o.pendingUpdates > 0 ? 'warning' : 'default'}
                  hint="aplicativos desatualizados nas máquinas"
                  href="#/maquinas?mostrar=outdated"
                />
              </>
            ) : (
              Array.from({ length: 5 }, (_, index) => <Skeleton key={index} className="h-[88px] rounded-xl" />)
            )}
          </div>

          <Card>
            <CardHeader>
              <CardTitle className="text-[15px]">Pedidos por dia</CardTitle>
              <CardDescription>Instalações, atualizações e desinstalações pedidas, por resultado.</CardDescription>
            </CardHeader>
            <CardContent>{o ? <RequestsChart days={o.perDay} /> : <Skeleton className="h-72 w-full" />}</CardContent>
          </Card>

          <HealthCard />

          <CatalogHealthCard />

          <AdoptionCard />

          <Card>
            <CardHeader>
              <CardTitle className="text-[15px]">Últimos pedidos</CardTitle>
              <CardAction>
                <Button asChild variant="link" size="sm">
                  <a href="#/historico">Ver histórico completo</a>
                </Button>
              </CardAction>
            </CardHeader>
            <CardContent>{o ? <RequestsTable rows={o.recent} onRetried={overview.reload} /> : <Skeleton className="h-48 w-full" />}</CardContent>
          </Card>
        </>
      )}
    </div>
  )
}
