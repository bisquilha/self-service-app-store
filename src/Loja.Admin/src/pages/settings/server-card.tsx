import type { ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Chip } from '@/components/ui/chip'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { useApi } from '@/hooks/use-api'
import type { AdminInfo } from '@/lib/types'

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid content-start gap-1">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="break-words">{children}</dd>
    </div>
  )
}

function YesNo({ value, noIsRisk = false }: { value: boolean; noIsRisk?: boolean }) {
  if (value) return <Badge variant="success">Sim</Badge>
  return <Badge variant={noIsRisk ? 'warning' : 'muted'}>Não</Badge>
}

/** O que está no appsettings.Production.json do servidor. Só leitura, de propósito. */
export function ServerCard() {
  const { data: info, error, loading, reload } = useApi<AdminInfo>('/info')
  const testMode = info?.adminGroups.includes('*') ?? false

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Servidor</CardTitle>
        <CardDescription>
          Somente leitura. Estes itens ficam no arquivo <span className="font-mono text-xs">appsettings.Production.json</span> do servidor, de propósito: mudar os grupos de administração ou o Tactical pelo navegador seria arriscado.
        </CardDescription>
      </CardHeader>
      <CardContent>
        {error && !info ? (
          <ErrorState message={error} onRetry={reload} />
        ) : loading && !info ? (
          <div className="grid gap-3" aria-busy="true">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        ) : info ? (
          <dl className="grid gap-x-6 gap-y-4 sm:grid-cols-2">
            <Item label="Versão do servidor">{info.serverVersion ?? '?'}</Item>
            <Item label="Seu usuário">{info.user ?? '?'}</Item>
            <Item label="Grupos de administração">
              {testMode ? (
                <span className="inline-flex flex-wrap items-center gap-2">
                  <Badge variant="warning">Todos os usuários (modo de teste)</Badge>
                  <span className="text-xs text-muted-foreground">Em produção, só o grupo LOJA-Admins.</span>
                </span>
              ) : (
                <span className="flex flex-wrap gap-1.5">
                  {info.adminGroups.map((group) => (
                    <Chip key={group}>{group}</Chip>
                  ))}
                </span>
              )}
            </Item>
            <Item label="Tactical RMM">
              {info.tacticalUrl === '(simulado)' ? (
                <span className="inline-flex flex-wrap items-center gap-2">
                  <Badge variant="warning">Simulado</Badge>
                  <span className="text-xs text-muted-foreground">Nada é instalado de verdade.</span>
                </span>
              ) : (
                <>
                  {info.tacticalUrl} <span className="text-muted-foreground">· script {info.tacticalScriptId}</span>
                </>
              )}
            </Item>
            <Item label="Feed interno (NAS)">{info.internalFeed ? <span className="font-mono text-[12.5px]">{info.internalFeed}</span> : <Badge variant="muted">Não configurado</Badge>}</Item>
            <Item label="Exige usuário com sessão aberta">
              <YesNo value={info.requireLoggedUser} noIsRisk />
            </Item>
            <Item label="Bloqueia Windows Server">
              <YesNo value={info.blockServers} noIsRisk />
            </Item>
          </dl>
        ) : null}
      </CardContent>
    </Card>
  )
}
