import { Link } from 'react-router-dom'
import { RotateCwIcon } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusDot } from '@/components/ui/status-dot'
import { useApi } from '@/hooks/use-api'
import { plural } from '@/lib/format'
import type { CatalogHealth, HealthIssue } from '@/lib/types'

/** Para onde cada problema leva: o aplicativo, o grupo ou a lista de atualizações. */
function fixFor(issue: HealthIssue): { to: string; label: string } {
  if (issue.kind === 'group-empty' && issue.groupSid) return { to: `/grupos/${encodeURIComponent(issue.groupSid)}`, label: 'Ver o grupo' }
  if (issue.kind === 'outdated') return { to: '/atualizacoes', label: 'Ver as atualizações' }
  return { to: `/aplicativos?q=${encodeURIComponent(issue.packageId)}`, label: 'Abrir em Aplicativos' }
}

const KIND_LABEL: Record<string, string> = {
  'no-group': 'Sem grupo',
  'group-missing': 'Grupo inexistente no AD',
  'group-unverified': 'Grupo não conferido',
  'group-empty': 'Grupo sem membros diretos',
  outdated: 'Desatualizado em muitas máquinas',
}

/**
 * Saúde do catálogo: o que está errado nos aplicativos e nos grupos deles. Fica ao lado da saúde das integrações.
 * "Inexistente" só aparece quando o AD respondeu que o grupo não existe; se o AD não respondeu, o item vira "não conferido".
 */
export function CatalogHealthCard() {
  const { data, error, loading, reload } = useApi<CatalogHealth>('/catalog-health')
  const errors = data?.issues.filter((issue) => issue.severity === 'error').length ?? 0

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Saúde do catálogo</CardTitle>
        <CardDescription>
          {data
            ? data.issues.length === 0
              ? `Tudo certo: ${plural(data.appsChecked, 'aplicativo ativo conferido', 'aplicativos ativos conferidos')}.`
              : `${plural(data.issues.length, 'item pede', 'itens pedem')} atenção${errors > 0 ? ` (${plural(errors, 'erro')})` : ''}.`
            : 'Conferindo aplicativos e grupos…'}
        </CardDescription>
        <CardAction>
          <Button variant="ghost" size="sm" loading={loading && data !== null} onClick={reload}>
            <RotateCwIcon aria-hidden="true" />
            Conferir de novo
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent className="grid gap-4">
        {error && !data ? (
          <ErrorState title="Não foi possível conferir o catálogo" message={error} onRetry={reload} className="py-6" />
        ) : !data ? (
          <div className="grid gap-3" aria-busy="true">
            <Skeleton className="h-4 w-60" />
            <Skeleton className="h-4 w-full" />
          </div>
        ) : (
          <>
            {data.adNote ? (
              <Alert variant="warning" role="note">
                <AlertDescription className="text-warning">{data.adNote}</AlertDescription>
              </Alert>
            ) : null}
            {data.issues.length > 0 ? (
              <ul className="grid gap-x-8 gap-y-4 md:grid-cols-2">
                {data.issues.map((issue) => {
                  const fix = fixFor(issue)
                  return (
                    <li key={`${issue.kind}|${issue.appId}|${issue.groupSid ?? issue.groupName ?? ''}`} className="grid content-start gap-0.5">
                      <StatusDot tone={issue.severity === 'error' ? 'destructive' : 'warning'} className="font-medium">
                        {issue.title}
                        <span className="font-normal text-muted-foreground"> · {KIND_LABEL[issue.kind] ?? issue.kind}</span>
                        <span className="sr-only">: {issue.severity === 'error' ? 'erro' : 'atenção'}</span>
                      </StatusDot>
                      <p className="pl-3.5 text-[13px] text-muted-foreground">{issue.message}</p>
                      <Link to={fix.to} className="pl-3.5 text-[13px] text-primary underline-offset-4 hover:underline focus-visible:underline">
                        {fix.label}
                      </Link>
                    </li>
                  )
                })}
              </ul>
            ) : null}
            <p className="text-xs text-muted-foreground">
              Entra na lista de desatualizados: {data.outdatedRule} Só aplicativos ativos são conferidos. Zero membros diretos não prova falta de acesso: quem está em grupos dentro do grupo também entra.
            </p>
          </>
        )}
      </CardContent>
    </Card>
  )
}
