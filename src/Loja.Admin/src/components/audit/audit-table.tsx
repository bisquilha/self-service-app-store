import { ArrowRightIcon, Undo2Icon } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { formatDateTime, shortLogin } from '@/lib/format'
import type { AuditRow } from '@/lib/types'

const KIND: Record<string, { label: string; variant: 'success' | 'destructive' | 'muted' | 'info' | 'warning' }> = {
  create: { label: 'Criado', variant: 'success' },
  delete: { label: 'Removido', variant: 'destructive' },
  update: { label: 'Alterado', variant: 'muted' },
  undo: { label: 'Desfeito', variant: 'warning' },
  run: { label: 'Executado', variant: 'info' },
}

type Props = {
  rows: AuditRow[]
  /** Na história de um aplicativo só, o nome dele já está no título: a coluna some. */
  showEntity?: boolean
  empty?: string
}

/** Trilha de auditoria: quando, quem, o que mudou e os valores de antes e de depois. */
export function AuditTable({ rows, showEntity = true, empty = 'Nenhuma alteração registrada.' }: Props) {
  if (rows.length === 0) {
    return (
      <div className="rounded-lg border">
        <EmptyState title={empty} description="As alterações feitas pelo painel (aplicativos, parâmetros, versões do app) aparecem aqui." />
      </div>
    )
  }

  return (
    <div className="overflow-hidden rounded-lg border bg-card">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead>Data</TableHead>
            <TableHead>Responsável</TableHead>
            {showEntity ? <TableHead>Item</TableHead> : null}
            <TableHead>O que mudou</TableHead>
            <TableHead>Antes</TableHead>
            <TableHead>Depois</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((row) => {
            const kind = KIND[row.kind] ?? { label: row.kind, variant: 'muted' as const }
            return (
              <TableRow key={row.id}>
                <TableCell className="whitespace-nowrap">{formatDateTime(row.utc)}</TableCell>
                <TableCell title={row.user}>{shortLogin(row.user)}</TableCell>
                {showEntity ? <TableCell className="max-w-56 whitespace-normal">{row.entityLabel ?? '—'}</TableCell> : null}
                <TableCell className="whitespace-normal">
                  <span className="flex flex-wrap items-center gap-1.5">
                    {row.field ?? '—'}
                    {row.undo ? (
                      <Badge variant="warning" title="Esta alteração foi um Desfazer">
                        <Undo2Icon aria-hidden="true" />
                        Revertida
                      </Badge>
                    ) : row.kind === 'create' || row.kind === 'delete' ? (
                      <Badge variant={kind.variant}>{kind.label}</Badge>
                    ) : null}
                  </span>
                </TableCell>
                <TableCell className="max-w-64 whitespace-normal text-[13px] text-muted-foreground">{row.oldValue ?? '—'}</TableCell>
                <TableCell className="max-w-64 whitespace-normal text-[13px]">
                  <span className="flex items-start gap-1.5">
                    <ArrowRightIcon className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <span className="sr-only">Depois: </span>
                    {row.newValue ?? '—'}
                  </span>
                </TableCell>
              </TableRow>
            )
          })}
        </TableBody>
      </Table>
    </div>
  )
}
