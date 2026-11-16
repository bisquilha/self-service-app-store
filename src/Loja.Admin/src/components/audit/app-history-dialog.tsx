import { AuditTable } from '@/components/audit/audit-table'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { ErrorState } from '@/components/ui/error-state'
import { Skeleton } from '@/components/ui/skeleton'
import { useApi } from '@/hooks/use-api'
import { plural } from '@/lib/format'
import type { AdminApp, AuditPage } from '@/lib/types'

function Body({ app }: { app: AdminApp }) {
  const { data, error, loading, reload } = useApi<AuditPage>(`/audit?entity=app&entityId=${app.id}&size=100`)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Histórico de alterações · {app.title}</DialogTitle>
        <DialogDescription>
          Tudo o que o painel mudou neste aplicativo: grupos, interruptores e a adição à loja.
          {data ? ` ${plural(data.total, 'registro')}.` : ''}
        </DialogDescription>
      </DialogHeader>
      {error && !data ? (
        <div className="rounded-lg border">
          <ErrorState title="Não foi possível carregar o histórico" message={error} onRetry={reload} />
        </div>
      ) : loading && !data ? (
        <div className="grid gap-2" aria-busy="true">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-10 w-full" />
        </div>
      ) : data ? (
        <AuditTable rows={data.items} showEntity={false} empty="Nenhuma alteração registrada neste aplicativo." />
      ) : null}
    </>
  )
}

/** Histórico de alterações de UM aplicativo (a trilha de auditoria filtrada por ele). */
export function AppHistoryDialog({ app, open, onOpenChange }: { app: AdminApp | null; open: boolean; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-4xl">{app ? <Body key={app.id} app={app} /> : null}</DialogContent>
    </Dialog>
  )
}
