import { RotateCwIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { StatusDot } from '@/components/ui/status-dot'
import { useApi } from '@/hooks/use-api'
import type { AdminStatus } from '@/lib/types'

/**
 * Saúde das integrações (Tactical, Chocolatey, feed interno, banco, atualização automática). O servidor confere tudo na
 * hora, o que pode demorar alguns segundos: por isso este cartão carrega sozinho, sem segurar os números da página.
 */
export function HealthCard() {
  const { data, error, loading, reload } = useApi<AdminStatus>('/status')
  const failing = data?.checks.filter((check) => !check.ok).length ?? 0

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Saúde do sistema</CardTitle>
        <CardDescription>{data ? (failing === 0 ? 'Tudo respondendo.' : `${failing} ${failing === 1 ? 'item pede' : 'itens pedem'} atenção.`) : 'Conferindo as integrações…'}</CardDescription>
        <CardAction>
          <Button variant="ghost" size="sm" loading={loading && data !== null} onClick={reload}>
            <RotateCwIcon aria-hidden="true" />
            Conferir de novo
          </Button>
        </CardAction>
      </CardHeader>
      <CardContent>
        {error && !data ? (
          <ErrorState title="Não foi possível conferir" message={error} onRetry={reload} className="py-6" />
        ) : !data ? (
          <div className="grid gap-4 md:grid-cols-2" aria-busy="true">
            {Array.from({ length: 4 }, (_, index) => (
              <div key={index} className="grid gap-1.5">
                <Skeleton className="h-4 w-44" />
                <Skeleton className="h-4 w-full" />
              </div>
            ))}
          </div>
        ) : (
          <ul className="grid gap-x-8 gap-y-4 md:grid-cols-2">
            {data.checks.map((check) => (
              <li key={check.name} className="grid content-start gap-0.5">
                <StatusDot tone={check.ok ? 'success' : 'destructive'} className="font-medium">
                  {check.name}
                  <span className="sr-only">: {check.ok ? 'funcionando' : 'com problema'}</span>
                </StatusDot>
                <p className="pl-3.5 text-[13px] text-muted-foreground">{check.detail}</p>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
