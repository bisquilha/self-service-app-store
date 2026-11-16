import { useEffect, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ChevronDownIcon, CopyIcon, Loader2Icon } from 'lucide-react'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { FileDropzone } from '@/components/ui/file-dropzone'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { useApi } from '@/hooks/use-api'
import { useLastValue } from '@/hooks/use-last-value'
import { api, messageOf } from '@/lib/api'
import { copyText } from '@/lib/clipboard'
import { formatBytes, formatDateTime, plural, shortLogin } from '@/lib/format'
import type { ClientInspect, ClientRelease } from '@/lib/types'

type Inspect = { state: 'idle' } | { state: 'checking' } | { state: 'ok'; data: ClientInspect } | { state: 'error'; message: string }

type Pending =
  | { kind: 'publish'; file: File; mandatory: boolean; inspect: ClientInspect }
  | { kind: 'current'; release: ClientRelease; mandatory: boolean }
  | { kind: 'delete'; release: ClientRelease }

/** Ao escolher o arquivo, o servidor confere e diz a versão e o efeito de publicar. Nada é gravado nessa etapa. */
function useInspect(file: File | null): Inspect {
  const [inspect, setInspect] = useState<Inspect>({ state: 'idle' })

  useEffect(() => {
    if (!file) {
      setInspect({ state: 'idle' })
      return
    }
    const controller = new AbortController()
    setInspect({ state: 'checking' })
    const form = new FormData()
    form.append('file', file)
    api<ClientInspect>('/client/releases/inspect', { method: 'POST', body: form, signal: controller.signal })
      .then((data) => setInspect({ state: 'ok', data }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setInspect({ state: 'error', message: messageOf(error, 'Não foi possível conferir o arquivo.') })
      })
    return () => controller.abort()
  }, [file])

  return inspect
}

function Sha({ value, version }: { value: string; version: string }) {
  async function copy() {
    if (await copyText(value)) toast.success(`SHA-256 da versão ${version} copiado.`)
    else toast.error('O navegador não deixou copiar o SHA-256.')
  }
  return (
    <span className="inline-flex items-center gap-1">
      <code className="font-mono text-[12.5px]" title={value}>
        {value.slice(0, 12)}…
      </code>
      <Button variant="ghost" size="icon-xs" aria-label={`Copiar o SHA-256 completo da versão ${version}`} onClick={copy}>
        <CopyIcon aria-hidden="true" />
      </Button>
    </span>
  )
}

function InspectResult({ inspect, file }: { inspect: Inspect; file: File }) {
  if (inspect.state === 'checking') {
    return (
      <p role="status" className="flex items-center gap-2 text-muted-foreground">
        <Loader2Icon className="size-4 animate-spin" aria-hidden="true" />
        Conferindo {file.name}…
      </p>
    )
  }
  if (inspect.state === 'error') {
    return (
      <Alert variant="destructive">
        <AlertDescription>{inspect.message}</AlertDescription>
      </Alert>
    )
  }
  if (inspect.state !== 'ok') return null

  const { data } = inspect
  const warnings: ReactNode[] = []
  if (data.alreadyPublished) {
    warnings.push(
      <>
        A versão <strong>{data.version}</strong> já está publicada{data.isCurrent ? ' e é a atual' : ''}. Publicar de novo troca o arquivo no servidor, mas as lojas que já usam{' '}
        {data.version} não baixam outra vez, porque o número é o mesmo. Aumente a versão no <code className="font-mono text-xs">Loja.App.csproj</code> antes de gerar o .exe.
      </>,
    )
  }
  if (data.olderThanCurrent) {
    warnings.push(
      <>
        A versão {data.version} é mais antiga que a atual ({data.currentVersion}). Para voltar atrás, use <strong>Tornar atual</strong> na lista abaixo, sem reenviar o arquivo.
      </>,
    )
  }

  return (
    <div className="grid gap-3" aria-live="polite">
      <dl className="grid gap-x-6 gap-y-1 rounded-lg border bg-muted/40 p-3 sm:grid-cols-3">
        <div>
          <dt className="text-xs text-muted-foreground">Versão detectada</dt>
          <dd className="font-mono font-semibold">{data.version}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Tamanho</dt>
          <dd>{formatBytes(data.size)}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">SHA-256</dt>
          <dd>
            <Sha value={data.sha256} version={data.version} />
          </dd>
        </div>
      </dl>
      {warnings.map((text, index) => (
        <Alert key={index} variant="warning" role="note">
          <AlertDescription className="text-warning">{text}</AlertDescription>
        </Alert>
      ))}
    </div>
  )
}

export function ReleasesCard() {
  const releases = useApi<ClientRelease[]>('/client/releases')
  const [file, setFile] = useState<File | null>(null)
  const [mandatory, setMandatory] = useState(false)
  const inspect = useInspect(file)
  const [pending, setPending] = useState<Pending | null>(null)
  const shown = useLastValue(pending)

  // A atual primeiro; as outras da mais recente para a mais antiga.
  const list = [...(releases.data ?? [])].sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent) || b.publishedUtc.localeCompare(a.publishedUtc))
  const current = list.find((r) => r.isCurrent)

  async function publish(p: Extract<Pending, { kind: 'publish' }>) {
    const form = new FormData()
    form.append('file', p.file)
    try {
      const release = await api<ClientRelease>(`/client/releases?mandatory=${p.mandatory}`, { method: 'POST', body: form })
      toast.success(`Versão ${release.version} publicada. As lojas vão baixar ao abrir.`)
      setFile(null)
      setMandatory(false)
      releases.reload()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  async function makeCurrent(p: Extract<Pending, { kind: 'current' }>) {
    try {
      await api(`/client/releases/${encodeURIComponent(p.release.version)}/current?mandatory=${p.mandatory}`, { method: 'POST' })
      toast.success(`A versão ${p.release.version} agora é a atual.`)
      releases.reload()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  async function remove(p: Extract<Pending, { kind: 'delete' }>) {
    try {
      await api(`/client/releases/${encodeURIComponent(p.release.version)}`, { method: 'DELETE' })
      toast.success(`Versão ${p.release.version} apagada do servidor.`)
      releases.reload()
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  const dialog = (() => {
    if (!shown) return null
    switch (shown.kind) {
      case 'publish':
        return {
          title: `Publicar a versão ${shown.inspect.version}`,
          description: (
            <>
              Publicar <strong>{shown.file.name}</strong> como atualização{' '}
              {shown.mandatory ? (
                <strong>obrigatória</strong>
              ) : (
                <strong>normal</strong>
              )}
              .{' '}
              {shown.mandatory
                ? 'As lojas abertas avisam e reiniciam sozinhas na versão nova.'
                : 'Cada pessoa passa a usar a versão nova na próxima vez que abrir a loja.'}
              {shown.inspect.alreadyPublished ? ' Atenção: esta versão já está publicada e será substituída.' : ''}
            </>
          ),
          confirmLabel: 'Publicar',
          destructive: false,
          run: () => publish(shown),
        }
      case 'current':
        return {
          title: 'Tornar versão atual',
          description: `Fazer todas as lojas usarem a versão ${shown.release.version}${shown.mandatory ? ', como atualização obrigatória (as lojas abertas reiniciam sozinhas)' : ''}?`,
          confirmLabel: 'Tornar atual',
          destructive: false,
          run: () => makeCurrent(shown),
        }
      case 'delete':
        return {
          title: 'Apagar versão',
          description: `Apagar a versão ${shown.release.version} do servidor? Ela deixa de poder ser baixada ou escolhida como atual. Quem já usa essa versão continua usando.`,
          confirmLabel: 'Apagar',
          destructive: true,
          run: () => remove(shown),
        }
    }
  })()

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Versão do app da loja</CardTitle>
        <CardDescription>
          Envie o <span className="font-mono text-xs">LojaDeAplicativos.exe</span> novo (da pasta <span className="font-mono text-xs">publicar\app</span>). Cada loja confere ao abrir, baixa em segundo plano e confere o SHA-256 antes de usar.
        </CardDescription>
        <CardAction>
          {current ? (
            <Badge variant="success">
              Atual: {current.version}
              {current.mandatory ? ' (obrigatória)' : ''}
            </Badge>
          ) : releases.loading ? null : (
            <Badge variant="muted">Nenhuma publicada ainda</Badge>
          )}
        </CardAction>
      </CardHeader>

      <CardContent className="grid gap-5">
        <div className="grid gap-3">
          <FileDropzone
            label="Arquivo do app da loja"
            accept=".exe"
            file={file}
            onFileChange={setFile}
            hint="Só arquivos .exe. O servidor confere o arquivo assim que você escolhe, antes de publicar."
          />
          {file ? <InspectResult inspect={inspect} file={file} /> : null}

          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="flex max-w-xl items-start gap-3">
              <Switch id="obrigatoria" checked={mandatory} onCheckedChange={setMandatory} aria-describedby="obrigatoria-ajuda" className="mt-0.5" />
              <div className="grid gap-0.5">
                <Label htmlFor="obrigatoria">Atualização obrigatória</Label>
                <p id="obrigatoria-ajuda" className="text-xs text-muted-foreground">
                  {mandatory
                    ? 'As lojas abertas avisam e reiniciam sozinhas em 15 segundos, depois de terminar um pedido em andamento.'
                    : 'Normal: cada pessoa passa a usar a versão nova na próxima vez que abrir a loja.'}
                </p>
              </div>
            </div>
            <Button
              disabled={inspect.state !== 'ok'}
              onClick={() => inspect.state === 'ok' && file && setPending({ kind: 'publish', file, mandatory, inspect: inspect.data })}
            >
              {inspect.state === 'ok' ? `Publicar versão ${inspect.data.version}` : 'Publicar nova versão'}
            </Button>
          </div>
        </div>

        <div className="overflow-hidden rounded-lg border">
          <Table aria-busy={releases.loading}>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead>Versão</TableHead>
                <TableHead>Publicada</TableHead>
                <TableHead>Por</TableHead>
                <TableHead>Tamanho</TableHead>
                <TableHead title="Máquinas que abriram a loja nesta versão">Máquinas</TableHead>
                <TableHead>SHA-256</TableHead>
                <TableHead className="w-32">
                  <span className="sr-only">Ações</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            {releases.loading && !releases.data ? (
              <TableSkeleton columns={7} rows={3} />
            ) : (
              <TableBody>
                {list.map((r) => (
                  <TableRow key={r.version}>
                    <TableCell>
                      <span className="inline-flex items-center gap-2">
                        <strong className="font-mono">{r.version}</strong>
                        {r.isCurrent ? <Badge variant="success">{r.mandatory ? 'atual · obrigatória' : 'atual'}</Badge> : null}
                      </span>
                    </TableCell>
                    <TableCell className="whitespace-nowrap">{formatDateTime(r.publishedUtc)}</TableCell>
                    <TableCell title={r.publishedBy}>{shortLogin(r.publishedBy)}</TableCell>
                    <TableCell className="whitespace-nowrap">{formatBytes(r.size)}</TableCell>
                    <TableCell>{r.machines ? plural(r.machines, 'máquina') : <span className="text-muted-foreground">—</span>}</TableCell>
                    <TableCell>
                      <Sha value={r.sha256} version={r.version} />
                    </TableCell>
                    <TableCell className="text-right">
                      {r.isCurrent ? null : (
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="secondary" size="sm" aria-label={`Ações da versão ${r.version}`}>
                              Ações
                              <ChevronDownIcon aria-hidden="true" />
                            </Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent align="end">
                            <DropdownMenuItem onSelect={() => setPending({ kind: 'current', release: r, mandatory: false })}>Tornar atual</DropdownMenuItem>
                            <DropdownMenuItem onSelect={() => setPending({ kind: 'current', release: r, mandatory: true })}>Tornar atual como obrigatória</DropdownMenuItem>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem variant="destructive" onSelect={() => setPending({ kind: 'delete', release: r })}>
                              Apagar versão…
                            </DropdownMenuItem>
                          </DropdownMenuContent>
                        </DropdownMenu>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            )}
          </Table>
          {releases.error ? <ErrorState message={releases.error} onRetry={releases.reload} /> : null}
          {!releases.loading && !releases.error && list.length === 0 ? (
            <EmptyState title="Nenhuma versão publicada" description="Escolha o LojaDeAplicativos.exe acima para publicar a primeira. Até lá, as lojas continuam na versão que já têm." />
          ) : null}
        </div>
      </CardContent>

      {dialog ? (
        <ConfirmDialog
          open={pending !== null}
          onOpenChange={(open) => !open && setPending(null)}
          title={dialog.title}
          description={dialog.description}
          confirmLabel={dialog.confirmLabel}
          destructive={dialog.destructive}
          onConfirm={dialog.run}
        />
      ) : null}
    </Card>
  )
}
