export type BadgeTone = 'info' | 'success' | 'destructive' | 'warning' | 'muted'

/** O que cada ação de um pedido se chama na tela. */
export const ACTION_LABELS: Record<string, string> = {
  install: 'Instalar',
  upgrade: 'Atualizar',
  uninstall: 'Desinstalar',
  list: 'Verificar',
}

/** Falha e "sem retorno" juntas: é o que os números de falha da Visão geral contam (`status=failed,timeout` no Histórico). */
export const PROBLEM_STATUSES = 'failed,timeout'

/** Situação de um pedido: texto e cor do selo. */
export const STATUS_META: Record<string, { label: string; tone: BadgeTone }> = {
  running: { label: 'Em andamento', tone: 'info' },
  success: { label: 'Concluído', tone: 'success' },
  failed: { label: 'Falhou', tone: 'destructive' },
  denied: { label: 'Negado', tone: 'warning' },
  timeout: { label: 'Sem retorno', tone: 'destructive' },
}

/** "failed,timeout" → "Falhou ou Sem retorno". O filtro do Histórico aceita mais de uma situação separada por vírgula. */
export function statusLabel(value: string): string {
  return value
    .split(',')
    .map((status) => STATUS_META[status]?.label ?? status)
    .join(' ou ')
}
