import { useMemo, useState } from 'react'
import { RotateCcw } from 'lucide-react'
import { toast } from 'sonner'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { useApi } from '@/hooks/use-api'
import { api, messageOf } from '@/lib/api'
import type { ConfigResponse, ConfigSetting } from '@/lib/types'

type Value = boolean | number | string
type Draft = Record<string, Value>

/** Erro de um campo, com a mesma regra do servidor (que confere de novo ao salvar). Nulo = está certo. */
function problem(setting: ConfigSetting, value: Value): string | null {
  if (setting.type === 'int') {
    const text = String(value).trim()
    if (!/^-?\d+$/.test(text)) return 'Informe um número inteiro.'
    const n = Number(text)
    if (setting.min !== null && setting.max !== null && (n < setting.min || n > setting.max)) return `Use um valor entre ${setting.min} e ${setting.max}.`
  }
  if (setting.type === 'url') {
    try {
      const url = new URL(String(value).trim())
      if (url.protocol !== 'http:' && url.protocol !== 'https:') return 'Use um endereço http ou https.'
    } catch {
      return 'Use um endereço http ou https.'
    }
  }
  return null
}

function sameValue(a: Value, b: Value) {
  return String(a) === String(b)
}

/**
 * Página de configurações desenhada pelo servidor: cada chave chega com tipo, rótulo, ajuda, faixa e dependência (GET /config).
 * Para criar ou mudar uma configuração não existe tela para escrever: acrescenta-se uma linha no servidor e ela aparece aqui.
 * Salva só o que mudou, tudo ou nada; vale na hora e chega aos agentes no próximo batimento.
 */
export function ConfigCard({ groups, title, description }: { groups: string[]; title: string; description: string }) {
  const config = useApi<ConfigResponse>('/config')
  const [draft, setDraft] = useState<Draft>({})
  const [saving, setSaving] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)

  const settings = useMemo(() => (config.data?.groups ?? []).flatMap((g) => g.settings), [config.data])
  const current = (s: ConfigSetting): Value => (s.key in draft ? draft[s.key] : s.value)

  const changed = settings.filter((s) => s.key in draft && !sameValue(draft[s.key], s.value))
  const errors = new Map(changed.map((s) => [s.key, problem(s, draft[s.key])] as const))
  const dirty = changed.length > 0
  const valid = [...errors.values()].every((e) => e === null)

  function edit(setting: ConfigSetting, value: Value) {
    setDraft((previous) => ({ ...previous, [setting.key]: value }))
  }

  async function save() {
    setSaving(true)
    setServerError(null)
    try {
      const values = Object.fromEntries(
        changed.map((s) => {
          const v = draft[s.key]
          return [s.key, s.type === 'int' ? Number(v) : s.type === 'bool' ? Boolean(v) : String(v).trim()]
        }),
      )
      const saved = await api<ConfigResponse>('/config', { method: 'PUT', body: { values } })
      setDraft({})
      config.reload()
      toast.success(`Configuração salva (versão ${saved.version}). Já vale; os agentes recebem no próximo batimento.`)
    } catch (error) {
      setServerError(messageOf(error))
      toast.error(messageOf(error))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>

      <CardContent className="grid gap-6">
        {config.error && !config.data ? (
          <ErrorState message={config.error} onRetry={config.reload} />
        ) : !config.data ? (
          <div className="grid gap-3" aria-busy="true">
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        ) : (
          config.data.groups.filter((group) => groups.includes(group.id)).map((group) => (
            <fieldset key={group.id} className="grid gap-4 border-0 p-0">
              <div>
                <legend className="p-0 font-semibold">{group.label}</legend>
                <p className="text-xs text-muted-foreground">{group.help}</p>
              </div>

              <div className="grid gap-5">
                {group.settings.map((s) => {
                  const id = `cfg-${s.key.replace(/[^a-z0-9]/gi, '-')}`
                  const value = current(s)
                  const parent = s.dependsOn ? settings.find((p) => p.key === s.dependsOn) : undefined
                  const blocked = parent ? current(parent) === false : false
                  const error = errors.get(s.key) ?? null
                  const isDefault = sameValue(value, s.default)

                  return (
                    <div key={s.key} className="grid content-start gap-1.5">
                      <div className="flex flex-wrap items-center gap-2">
                        <Label htmlFor={id} className={blocked ? 'text-muted-foreground' : undefined}>
                          {s.label}
                        </Label>
                        {s.appliesToAgents ? <Badge variant="muted">Vale para os agentes</Badge> : null}
                        {!isDefault ? <Badge variant="info">Mudado do padrão</Badge> : null}
                        {!isDefault ? (
                          <Button variant="ghost" size="sm" className="h-6 px-1.5 text-xs" onClick={() => edit(s, s.default)} aria-label={`Restaurar o padrão de ${s.label}`}>
                            <RotateCcw aria-hidden="true" className="size-3" /> Restaurar padrão
                          </Button>
                        ) : null}
                      </div>

                      {s.type === 'bool' ? (
                        <div className="flex items-center gap-2">
                          <Switch id={id} checked={value === true} onCheckedChange={(next) => edit(s, next)} aria-describedby={`${id}-ajuda`} />
                          <span className="text-sm">{value === true ? 'Ligado' : 'Desligado'}</span>
                        </div>
                      ) : (
                        <Input
                          id={id}
                          type={s.type === 'int' ? 'number' : 'text'}
                          inputMode={s.type === 'int' ? 'numeric' : undefined}
                          value={String(value)}
                          disabled={blocked}
                          onChange={(event) => edit(s, event.target.value)}
                          aria-invalid={error ? true : undefined}
                          aria-describedby={error ? `${id}-erro ${id}-ajuda` : `${id}-ajuda`}
                          className="max-w-xl"
                        />
                      )}

                      {error ? (
                        <p id={`${id}-erro`} role="alert" className="text-xs text-destructive">
                          {error}
                        </p>
                      ) : null}
                      <p id={`${id}-ajuda`} className="text-xs text-muted-foreground">
                        {s.help}
                        {s.type === 'int' && s.min !== null && s.max !== null ? ` (de ${s.min} a ${s.max})` : ''}
                        {blocked ? ' Desabilitado enquanto a opção acima estiver desligada.' : ''}
                      </p>
                    </div>
                  )
                })}
              </div>
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
          {dirty ? (
            <Badge variant="warning">Alterações não salvas</Badge>
          ) : (
            <span className="text-muted-foreground">Tudo salvo{config.data ? ` · versão da configuração ${config.data.version}` : ''}</span>
          )}
        </span>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            disabled={!dirty || saving}
            onClick={() => {
              setDraft({})
              setServerError(null)
            }}
          >
            Descartar
          </Button>
          <Button disabled={!dirty || !valid} loading={saving} onClick={save}>
            Salvar
          </Button>
        </div>
      </CardFooter>
    </Card>
  )
}
