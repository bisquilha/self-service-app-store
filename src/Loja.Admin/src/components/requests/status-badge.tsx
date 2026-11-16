import { Badge } from '@/components/ui/badge'
import { STATUS_META } from '@/lib/requests'

/** Situação de um pedido (Concluído, Falhou, Negado...). O texto diz o estado; a cor só reforça. */
export function RequestStatusBadge({ status }: { status: string }) {
  const meta = STATUS_META[status]
  return <Badge variant={meta?.tone ?? 'muted'}>{meta?.label ?? status}</Badge>
}
