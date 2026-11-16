import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { RotateCwIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { SortableHead } from '@/components/ui/sortable-head'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { formatDateTime, shortLogin } from '@/lib/format'
import { ACTION_LABELS } from '@/lib/requests'
import type { SortState } from '@/lib/sort'
import type { RequestRow } from '@/lib/types'
import { RequestStatusBadge } from './status-badge'

/** Só pedido de instalar, atualizar ou desinstalar que falhou (ou ficou sem retorno) pode ser reexecutado; o servidor confere tudo de novo. */
const canRetry = (request: RequestRow) =>
  (request.status === 'failed' || request.status === 'timeout') && ['install', 'upgrade', 'uninstall'].includes(request.action) && request.appId !== null

/** Detalhe de um pedido: quem, onde, quando e o que o Chocolatey respondeu. Com `onRetried`, pedido que falhou ganha "Reexecutar". */
export function RequestDetailsDialog({
  request,
  open,
  onOpenChange,
  onRetried,
}: {
  request: RequestRow | null
  open: boolean
  onOpenChange: (open: boolean) => void
  onRetried?: () => void
}) {
  const [confirming, setConfirming] = useState(false)

  async function retry() {
    if (!request) return
    try {
      const created = await api<RequestRow>(`/requests/${request.id}/retry`, { method: 'POST' })
      toast.success(`Pedido ${created.id} enviado a ${created.hostname} (reexecução do pedido ${request.id}).`)
      onOpenChange(false)
      onRetried?.()
    } catch (error) {
      // Aplicativo que saiu da loja, computador offline, desinstalação não liberada hoje…: o servidor diz o motivo.
      toast.error(messageOf(error))
      throw error
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        {request ? (
          <>
            <DialogHeader>
              <DialogTitle>
                {ACTION_LABELS[request.action] ?? request.action} · {request.title}
              </DialogTitle>
              <DialogDescription>
                Pedido {request.id}
                {request.retryOf ? ` · reexecução do pedido ${request.retryOf}` : ''}
              </DialogDescription>
            </DialogHeader>
            <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
              <Item label="Situação">
                <RequestStatusBadge status={request.status} />
              </Item>
              <Item label="Computador">{request.hostname}</Item>
              <Item label="Usuário">{request.user}</Item>
              <Item label="Pacote">
                <span className="font-mono text-[12.5px]">{request.packageId}</span>
              </Item>
              <Item label="Pedido em">{formatDateTime(request.createdUtc)}</Item>
              <Item label="Terminou em">{formatDateTime(request.finishedUtc)}</Item>
              {request.exitCode !== null && request.exitCode !== undefined ? (
                <Item label="Código de saída">
                  <span className="font-mono text-[12.5px]">{request.exitCode}</span>
                </Item>
              ) : null}
            </dl>
            {request.message ? <p>{request.message}</p> : null}
            {request.output ? (
              <div className="grid gap-1.5">
                <strong>Saída do Chocolatey</strong>
                <pre className="max-h-72 overflow-auto rounded-lg bg-muted p-3 font-mono text-xs whitespace-pre-wrap">{request.output}</pre>
              </div>
            ) : (
              <p className="text-muted-foreground">Sem saída registrada.</p>
            )}
            {onRetried && canRetry(request) ? (
              <div className="flex flex-wrap items-center gap-3 border-t pt-3">
                <Button variant="secondary" onClick={() => setConfirming(true)}>
                  <RotateCwIcon aria-hidden="true" />
                  Reexecutar
                </Button>
                <span className="text-xs text-muted-foreground">Cria um novo pedido pelo painel, ligado a este. A loja confere de novo o aplicativo e o computador.</span>
              </div>
            ) : null}
            <ConfirmDialog
              open={confirming}
              onOpenChange={setConfirming}
              title={`Reexecutar o pedido ${request.id}?`}
              description={`${ACTION_LABELS[request.action] ?? request.action} “${request.title}” em ${request.hostname}. O pedido novo fica no histórico como ação do painel, ligado ao pedido ${request.id}. Antes de enviar, a loja confere se o aplicativo ainda está na loja, se a desinstalação continua liberada e se o computador está online.`}
              confirmLabel="Reexecutar"
              onConfirm={retry}
            />
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

function Item({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="grid content-start gap-0.5">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd>{children}</dd>
    </div>
  )
}

type RequestsTableProps = {
  rows: RequestRow[]
  /** Esconde o computador quando a lista já é de uma máquina só. */
  showHost?: boolean
  /** Com `onSort` os cabeçalhos viram botões que ordenam. As chaves são as do servidor: created, user, host, app, action, status. */
  sort?: SortState | null
  onSort?: (key: string) => void
  /** Troca a mensagem de "nenhum pedido" (por exemplo, quando há filtro ligado). */
  empty?: ReactNode
  /** Primeira carga: mostra linhas de esqueleto no lugar da tabela. */
  loading?: boolean
  /** Com isto, o detalhe de um pedido que falhou oferece "Reexecutar"; é chamado depois de enviado, para a tela recarregar. */
  onRetried?: () => void
}

/**
 * Tabela de pedidos. Clicar na linha (ou no botão Ver) abre o detalhe.
 */
export function RequestsTable({ rows, showHost = true, sort = null, onSort, empty, loading = false, onRetried }: RequestsTableProps) {
  const [selected, setSelected] = useState<RequestRow | null>(null)
  const shown = useLastValue(selected)

  const head = (label: string, sortKey: string) =>
    onSort ? <SortableHead label={label} sortKey={sortKey} sort={sort} onSort={onSort} /> : <TableHead>{label}</TableHead>

  if (rows.length === 0 && !loading) {
    return empty ?? <EmptyState title="Nenhum pedido ainda" description="Os pedidos de instalar, atualizar e desinstalar aparecem aqui." />
  }

  return (
    <>
      <div className="overflow-hidden rounded-lg border">
        <Table aria-busy={loading}>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {head('Data', 'created')}
              {head('Usuário', 'user')}
              {showHost ? head('Computador', 'host') : null}
              {head('Aplicativo', 'app')}
              {head('Ação', 'action')}
              {head('Situação', 'status')}
              <TableHead>Detalhe</TableHead>
              <TableHead className="w-16">
                <span className="sr-only">Ver</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          {loading && rows.length === 0 ? (
            <TableSkeleton columns={showHost ? 8 : 7} rows={8} />
          ) : (
          <TableBody>
            {rows.map((request) => (
              <TableRow key={request.id} className="cursor-pointer" onClick={() => setSelected(request)}>
                <TableCell className="whitespace-nowrap">{formatDateTime(request.createdUtc)}</TableCell>
                <TableCell title={request.user}>{shortLogin(request.user)}</TableCell>
                {showHost ? <TableCell className="whitespace-nowrap">{request.hostname}</TableCell> : null}
                <TableCell>{request.title}</TableCell>
                <TableCell>{ACTION_LABELS[request.action] ?? request.action}</TableCell>
                <TableCell>
                  <RequestStatusBadge status={request.status} />
                </TableCell>
                <TableCell className="max-w-72 text-[13px] text-muted-foreground">
                  <span className="line-clamp-2" title={request.message ?? undefined}>
                    {request.message ?? ''}
                  </span>
                </TableCell>
                <TableCell className="text-right">
                  <Button
                    variant="ghost"
                    size="xs"
                    aria-label={`Ver detalhes do pedido ${request.id}: ${ACTION_LABELS[request.action] ?? request.action} ${request.title}`}
                    onClick={(event) => {
                      event.stopPropagation()
                      setSelected(request)
                    }}
                  >
                    Ver
                  </Button>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
          )}
        </Table>
      </div>
      <RequestDetailsDialog request={shown} open={selected !== null} onOpenChange={(open) => !open && setSelected(null)} onRetried={onRetried} />
    </>
  )
}
