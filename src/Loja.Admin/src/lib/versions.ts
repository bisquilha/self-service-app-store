import type { CatalogItem, VersionState } from '@/lib/types'

export type StateView = {
  state: VersionState
  /** Frase curta do selo. Sempre diz o estado: a cor nunca vai sozinha. */
  label: string
  tone: 'success' | 'warning' | 'info' | 'muted'
  /** Linha de apoio (versões). */
  detail: string | null
  /** Veio de fora da loja: ganha o selo "Fora da loja". */
  outside: boolean
}

type Item = Pick<CatalogItem, 'versionState' | 'installedVersion' | 'version' | 'updateAvailable' | 'externalVersion'>

/**
 * Como um aplicativo aparece numa máquina. Quem decide o estado é o servidor (`versionState`); aqui só se escreve.
 * Com um servidor anterior à 1.6.0 (sem `versionState`) o estado sai de `installedVersion` e `updateAvailable`.
 */
export function describeState(item: Item): StateView {
  const installed = item.installedVersion
  const state: VersionState = item.versionState ?? (!installed ? 'none' : item.updateAvailable ? 'update' : 'current')
  switch (state) {
    case 'none':
      return { state, label: 'Não instalado', tone: 'muted', detail: item.version ? `Disponível: versão ${item.version}` : null, outside: false }
    case 'update':
      return { state, label: 'Atualização disponível', tone: 'warning', detail: `${installed} → ${item.version ?? 'versão mais recente'}`, outside: false }
    case 'newer':
      return { state, label: 'Instalado · mais novo que a loja', tone: 'muted', detail: `Instalada ${installed}; a loja tem ${item.version ?? '—'}. Não será trocada.`, outside: false }
    case 'unknown':
      return { state, label: 'Instalado · versão não conferida', tone: 'muted', detail: `Versão ${installed}. Não dá para comparar com a da loja.`, outside: false }
    case 'external':
      return {
        state,
        label: 'Já instalado no computador',
        tone: 'info',
        detail: item.externalVersion ? `Versão ${item.externalVersion}` : 'O programa não informa a versão',
        outside: true,
      }
    default:
      return { state, label: 'Instalado · Em dia', tone: 'success', detail: `Versão ${installed}`, outside: false }
  }
}
