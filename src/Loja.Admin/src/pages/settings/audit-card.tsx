import { useEffect, useState } from 'react'
import { AuditTable } from '@/components/audit/audit-table'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { PaginationBar } from '@/components/ui/pagination-bar'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { useApi } from '@/hooks/use-api'
import type { AuditPage } from '@/lib/types'

const PAGE_SIZE = 25
const ALL = 'all'
const TYPES: [string, string][] = [
  ['app', 'Aplicativos'],
  ['settings', 'Parâmetros'],
  ['release', 'Versões do app da loja'],
  ['action', 'Ações em várias máquinas'],
]

/** Auditoria das alterações feitas pelo painel. Os filtros são do servidor, então valem em todas as páginas. */
export function AuditCard() {
  const [text, setText] = useState('')
  const [debounced, setDebounced] = useState('')
  const [type, setType] = useState(ALL)
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)

  // A busca espera a pessoa parar de digitar.
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebounced(text.trim())
      setPage(1)
    }, 350)
    return () => clearTimeout(timer)
  }, [text])

  const query = new URLSearchParams()
  if (debounced) query.set('text', debounced)
  if (type !== ALL) query.set('entity', type)
  if (from) query.set('from', from)
  if (to) query.set('to', to)
  query.set('size', String(PAGE_SIZE))
  query.set('page', String(page))
  const { data, error, loading, reload } = useApi<AuditPage>(`/audit?${query}`)
  const filtering = debounced !== '' || type !== ALL || from !== '' || to !== ''

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Auditoria</CardTitle>
        <CardDescription>Registra quem alterou o quê no painel, com o valor de antes e o de depois. Os registros não são apagados.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-[minmax(14rem,1fr)_13rem_10rem_10rem]">
          <div className="grid content-start gap-1.5">
            <Label htmlFor="audit-busca" className="text-[12.5px] font-normal text-muted-foreground">
              Buscar
            </Label>
            <Input id="audit-busca" type="search" placeholder="Pessoa, aplicativo, grupo ou valor…" value={text} onChange={(event) => setText(event.target.value)} />
          </div>
          <div className="grid content-start gap-1.5">
            <Label htmlFor="audit-tipo" className="text-[12.5px] font-normal text-muted-foreground">
              O que foi alterado
            </Label>
            <Select
              value={type}
              onValueChange={(value) => {
                setType(value)
                setPage(1)
              }}
            >
              <SelectTrigger id="audit-tipo" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>Tudo</SelectItem>
                {TYPES.map(([value, label]) => (
                  <SelectItem key={value} value={value}>
                    {label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="grid content-start gap-1.5">
            <Label htmlFor="audit-de" className="text-[12.5px] font-normal text-muted-foreground">
              De
            </Label>
            <Input
              id="audit-de"
              type="date"
              value={from}
              max={to || undefined}
              onChange={(event) => {
                setFrom(event.target.value)
                setPage(1)
              }}
            />
          </div>
          <div className="grid content-start gap-1.5">
            <Label htmlFor="audit-ate" className="text-[12.5px] font-normal text-muted-foreground">
              Até
            </Label>
            <Input
              id="audit-ate"
              type="date"
              value={to}
              min={from || undefined}
              onChange={(event) => {
                setTo(event.target.value)
                setPage(1)
              }}
            />
          </div>
        </div>

        {filtering ? (
          <div>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                setText('')
                setDebounced('')
                setType(ALL)
                setFrom('')
                setTo('')
                setPage(1)
              }}
            >
              Limpar filtros
            </Button>
          </div>
        ) : null}

        {error && !data ? (
          <div className="rounded-lg border">
            <ErrorState title="Não foi possível carregar a auditoria" message={error} onRetry={reload} />
          </div>
        ) : loading && !data ? (
          <div className="grid gap-2" aria-busy="true">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : data ? (
          <div className={loading ? 'opacity-60 transition-opacity' : 'transition-opacity'}>
            <AuditTable rows={data.items} empty={filtering ? 'Nenhuma alteração com esses filtros.' : 'Nenhuma alteração registrada ainda.'} />
          </div>
        ) : null}

        {data && data.total > PAGE_SIZE ? <PaginationBar total={data.total} page={page} pageSize={PAGE_SIZE} onPageChange={setPage} singular="registro" /> : null}
      </CardContent>
    </Card>
  )
}
