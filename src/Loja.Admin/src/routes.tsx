import { History, LayoutDashboard, Monitor, Package, RefreshCw, Settings, UserSearch, UsersRound, type LucideIcon } from 'lucide-react'

export type AppRoute = {
  /** Segmento da URL (#/aplicativos). Mesmos nomes do painel antigo, para links guardados continuarem valendo. */
  path: string
  /** Padrão do React Router; as telas com parâmetro opcional ganham `/:host?` ou `/:user?`. */
  pattern: string
  label: string
  icon: LucideIcon
  description: string
}

export const routes: AppRoute[] = [
  { path: 'visao-geral', pattern: 'visao-geral', label: 'Visão geral', icon: LayoutDashboard, description: 'Indicadores do dia, pedidos por dia e saúde das integrações.' },
  { path: 'aplicativos', pattern: 'aplicativos', label: 'Aplicativos', icon: Package, description: 'Catálogo da loja, grupos do AD e interruptores de cada aplicativo.' },
  { path: 'atualizacoes', pattern: 'atualizacoes', label: 'Atualizações', icon: RefreshCw, description: 'Aplicativos com máquinas em versão antiga e atualização em todas de uma vez.' },
  { path: 'grupos', pattern: 'grupos/:sid?', label: 'Grupos', icon: UsersRound, description: 'O que a loja libera para quem está em cada grupo do AD, e quem são os membros.' },
  { path: 'maquinas', pattern: 'maquinas/:host?', label: 'Máquinas', icon: Monitor, description: 'Estações do Tactical, programas instalados e ações remotas.' },
  { path: 'ver-como', pattern: 'ver-como/:user?', label: 'Ver como usuário', icon: UserSearch, description: 'A loja como outra pessoa a vê, a partir dos grupos do AD.' },
  { path: 'historico', pattern: 'historico', label: 'Histórico', icon: History, description: 'Todos os pedidos, com filtros e exportação para Excel.' },
  { path: 'configuracoes', pattern: 'configuracoes', label: 'Configurações', icon: Settings, description: 'Versões do app da loja, parâmetros e ferramentas de manutenção.' },
]
