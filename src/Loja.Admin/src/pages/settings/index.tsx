import { useSearchParams } from 'react-router-dom'
import { PageHeader } from '@/components/shell/page-header'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { AuditCard } from './audit-card'
import { ConfigCard } from './config-card'
import { LocalGroupsCard, LocalUsersCard } from './identity-cards'
import { ParametersCard } from './parameters-card'
import { ServerCard } from './server-card'
import { ToolsCard } from './tools-card'

const TABS = ['geral', 'fontes', 'identidade', 'auditoria'] as const
type Tab = (typeof TABS)[number]

/**
 * Configurações em quatro abas: Geral (parâmetros, servidor, ferramentas), Fontes e agente e Identidade (motor de configurações,
 * desenhado pelo servidor) e Auditoria. O app da loja não tem aba de versões: é uma coisa só e muda quando sai uma atualização.
 * A aba fica na URL (`#/configuracoes?aba=auditoria`). Cada cartão carrega e falha sozinho. Geral e Versões ficam montados
 * (só escondidos) para trocar de aba não perder o que está sendo editado nem o arquivo escolhido.
 */
export function SettingsPage() {
  const [params, setParams] = useSearchParams()
  const requested = params.get('aba')
  const tab: Tab = TABS.includes(requested as Tab) ? (requested as Tab) : 'geral'

  return (
    <div className="grid gap-4">
      <PageHeader title="Configurações" />
      <Tabs value={tab} onValueChange={(value) => setParams(value === 'geral' ? {} : { aba: value }, { replace: true })}>
        <TabsList className="max-w-full justify-start overflow-x-auto">
          <TabsTrigger value="geral" className="px-3">
            Geral
          </TabsTrigger>
          <TabsTrigger value="fontes" className="px-3">
            Fontes e agente
          </TabsTrigger>
          <TabsTrigger value="identidade" className="px-3">
            Identidade
          </TabsTrigger>
          <TabsTrigger value="auditoria" className="px-3">
            Auditoria
          </TabsTrigger>
        </TabsList>

        <TabsContent value="geral" forceMount className="grid gap-4 pt-2 data-[state=inactive]:hidden">
          <ParametersCard />
          <div className="grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
            <ServerCard />
            <ToolsCard />
          </div>
        </TabsContent>
        <TabsContent value="fontes" forceMount className="grid gap-4 pt-2 data-[state=inactive]:hidden">
          <ConfigCard groups={['sources', 'agent']} title="Fontes de aplicativos e agente" description="Tudo aqui pode ser ligado, desligado ou trocado a qualquer momento, sem reinstalar e sem reiniciar. Vale também para os agentes já instalados." />
        </TabsContent>
        <TabsContent value="identidade" forceMount className="grid gap-4 pt-2 data-[state=inactive]:hidden">
          <ConfigCard groups={['identity']} title="Como as pessoas entram" description="O login do Windows (AD) continua como está. Ligue o login local e a ponte do agente para usar a loja sem AD. O administrador de emergência entra sempre." />
          <LocalUsersCard />
          <LocalGroupsCard />
        </TabsContent>
        <TabsContent value="auditoria" className="pt-2">
          <AuditCard />
        </TabsContent>
      </Tabs>
    </div>
  )
}
