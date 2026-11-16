import { useEffect, useMemo, useState } from 'react'
import { toast } from 'sonner'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { useApi } from '@/hooks/use-api'
import { api, messageOf } from '@/lib/api'
import type { SettingField, SettingKey, Settings } from '@/lib/types'

type Values = Record<SettingKey, string>

/** Texto de cada campo. Rótulo e faixa vêm do servidor (GET /settings/meta); aqui ficam só a unidade e a explicação. */
const GROUPS: { title: string; description: string; fields: { key: SettingKey; unit: string; help: string }[] }[] = [
  {
    title: 'Atualização automática',
    description: 'Atualiza sozinho os aplicativos marcados como "Auto-atualizar", só dentro desta janela de horário.',
    fields: [
      { key: 'autoUpdateStartHour', unit: 'h', help: 'Hora do dia, no horário do servidor.' },
      { key: 'autoUpdateEndHour', unit: 'h', help: 'A janela vai do início até a hora anterior ao fim.' },
      { key: 'autoUpdateMaxPerCycle', unit: 'máquinas', help: 'Quantas atualizações por vez, a cada 10 minutos.' },
    ],
  },
  {
    title: 'Pedidos e dados',
    description: 'Quando a loja pede uma lista nova de programas, quando desiste de um pedido e de quanto em quanto tempo busca os dados dos aplicativos.',
    fields: [
      { key: 'inventoryMaxAgeMinutes', unit: 'min', help: 'Ao abrir a loja, pede uma lista nova se a última for mais antiga que isso.' },
      { key: 'requestTimeoutMinutes', unit: 'min', help: 'Sem retorno do computador nesse tempo, o pedido vira "Sem retorno".' },
      { key: 'metadataRefreshHours', unit: 'h', help: 'Nome, versão e ícone vindos do Chocolatey e do feed interno.' },
    ],
  },
]

function toValues(settings: Settings): Values {
  return Object.fromEntries(Object.entries(settings).map(([key, value]) => [key, String(value)])) as Values
}

function problem(text: string, field: SettingField | undefined): string | null {
  if (text.trim() === '' || !/^-?\d+$/.test(text.trim())) return 'Informe um número inteiro.'
  const n = Number(text)
  if (field && (n < field.min || n > field.max)) return `Use um valor entre ${field.min} e ${field.max}.`
  return null
}

function NumberField({ id, label, unit, help, value, error, onChange }: { id: string; label: string; unit: string; help: string; value: string; error: string | null; onChange: (value: string) => void }) {
  const helpId = `${id}-ajuda`
  const errorId = `${id}-erro`
  return (
    <div className="grid content-start gap-1.5">
      <Label htmlFor={id}>
        {label}
        <span className="sr-only"> (em {unit})</span>
      </Label>
      <div className="relative">
        <Input
          id={id}
          type="number"
          inputMode="numeric"
          value={value}
          onChange={(event) => onChange(event.target.value)}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${errorId} ${helpId}` : helpId}
          className="pr-[4.5rem] [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none [&::-webkit-outer-spin-button]:appearance-none"
        />
        <span aria-hidden="true" className="pointer-events-none absolute inset-y-0 right-3 flex items-center text-xs text-muted-foreground">
          {unit}
        </span>
      </div>
      {error ? (
        <p id={errorId} role="alert" className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
      <p id={helpId} className="text-xs text-muted-foreground">
        {help}
      </p>
    </div>
  )
}

export function ParametersCard() {
  const settings = useApi<Settings>('/settings')
  const meta = useApi<SettingField[]>('/settings/meta')
  const [baseline, setBaseline] = useState<Values | null>(null)
  const [values, setValues] = useState<Values | null>(null)
  const [saving, setSaving] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)

  useEffect(() => {
    if (settings.data && !baseline) {
      const initial = toValues(settings.data)
      setBaseline(initial)
      setValues(initial)
    }
  }, [settings.data, baseline])

  const fields = useMemo(() => new Map((meta.data ?? []).map((f) => [f.key, f])), [meta.data])
  const errors = useMemo(() => {
    const result = {} as Record<SettingKey, string | null>
    if (!values) return result
    for (const key of Object.keys(values) as SettingKey[]) result[key] = problem(values[key], fields.get(key))
    return result
  }, [values, fields])

  const dirty = !!values && !!baseline && (Object.keys(values) as SettingKey[]).some((key) => values[key] !== baseline[key])
  const valid = !!values && Object.values(errors).every((e) => e === null)

  // Avisa antes de fechar ou recarregar a página com alterações por salvar.
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const window24 = (() => {
    if (!values || errors.autoUpdateStartHour || errors.autoUpdateEndHour) return null
    const start = Number(values.autoUpdateStartHour)
    const end = Number(values.autoUpdateEndHour)
    if (start === end) return { tone: 'warning' as const, text: 'Início e fim iguais: a janela fica vazia e a atualização automática nunca roda.' }
    if (start > end) return { tone: 'info' as const, text: `A atualização automática roda das ${start}h às ${end}h do dia seguinte, passando da meia-noite.` }
    return { tone: 'info' as const, text: `A atualização automática roda das ${start}h às ${end}h, todos os dias (horário do servidor).` }
  })()

  async function save() {
    if (!values) return
    setSaving(true)
    setServerError(null)
    try {
      const body = Object.fromEntries((Object.keys(values) as SettingKey[]).map((key) => [key, Number(values[key])]))
      const saved = await api<Settings>('/settings', { method: 'PUT', body })
      const next = toValues(saved)
      setBaseline(next)
      setValues(next)
      toast.success('Configurações salvas. Já valem, sem reiniciar.')
    } catch (error) {
      setServerError(messageOf(error))
      toast.error(messageOf(error))
    } finally {
      setSaving(false)
    }
  }

  const failed = settings.error ?? meta.error
  const ready = values !== null && meta.data !== null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Parâmetros</CardTitle>
        <CardDescription>Valem na hora que você salva, sem reiniciar o servidor. Ficam guardados no banco da loja.</CardDescription>
      </CardHeader>

      <CardContent className="grid gap-6">
        {failed && !ready ? (
          <ErrorState message={failed} onRetry={() => { settings.reload(); meta.reload() }} />
        ) : !ready ? (
          <div className="grid gap-3" aria-busy="true">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        ) : (
          GROUPS.map((group) => (
            <fieldset key={group.title} className="grid gap-3 border-0 p-0">
              <div>
                <legend className="p-0 font-semibold">{group.title}</legend>
                <p className="text-xs text-muted-foreground">{group.description}</p>
              </div>
              <div className="grid gap-4 md:grid-cols-3">
                {group.fields.map((f) => (
                  <NumberField
                    key={f.key}
                    id={`param-${f.key}`}
                    label={fields.get(f.key)?.label ?? f.key}
                    unit={f.unit}
                    help={f.help}
                    value={values[f.key]}
                    error={errors[f.key] ?? null}
                    onChange={(next) => setValues({ ...values, [f.key]: next })}
                  />
                ))}
              </div>
              {group.fields.some((f) => f.key === 'autoUpdateStartHour') && window24 ? (
                <p className={window24.tone === 'warning' ? 'text-xs font-medium text-warning' : 'text-xs text-muted-foreground'} role={window24.tone === 'warning' ? 'note' : undefined}>
                  {window24.text}
                </p>
              ) : null}
            </fieldset>
          ))
        )}

        {serverError ? (
          <Alert variant="destructive">
            <AlertDescription>{serverError}</AlertDescription>
          </Alert>
        ) : null}
      </CardContent>

      <CardFooter className="flex flex-wrap items-center justify-between gap-3 border-t">
        <span aria-live="polite" className="text-sm">
          {dirty ? <Badge variant="warning">Alterações não salvas</Badge> : <span className="text-muted-foreground">Tudo salvo</span>}
        </span>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            disabled={!dirty || saving}
            onClick={() => {
              setValues(baseline)
              setServerError(null)
            }}
          >
            Descartar
          </Button>
          <Button disabled={!ready || !dirty || !valid} loading={saving} onClick={save}>
            Salvar configurações
          </Button>
        </div>
      </CardFooter>
    </Card>
  )
}
