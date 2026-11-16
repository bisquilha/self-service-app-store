import { useState } from 'react'
import { toast } from 'sonner'
import { AutoUpdateButton } from '@/components/auto-update-button'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'

type Progress = { done: number; total: number } | null

/** Ações manuais de manutenção. A saúde das integrações fica na Visão geral, não aqui. */
export function ToolsCard() {
  const [progress, setProgress] = useState<Progress>(null)

  async function refreshAll() {
    setProgress({ done: 0, total: 0 })
    try {
      const apps = await api<{ id: number }[]>('/apps')
      if (apps.length === 0) {
        toast.info('Ainda não há aplicativos na loja.')
        return
      }
      let ok = 0
      for (const [index, app] of apps.entries()) {
        setProgress({ done: index, total: apps.length })
        try {
          await api(`/apps/${app.id}/refresh`, { method: 'POST' })
          ok++
        } catch {
          // um aplicativo que sumiu do repositório não pode parar os outros
        }
      }
      const text = `Dados atualizados em ${ok} de ${plural(apps.length, 'aplicativo')}.`
      if (ok === apps.length) toast.success(text)
      else toast.warning(`${text} Os demais não foram achados no repositório.`)
    } catch (error) {
      toast.error(messageOf(error))
    } finally {
      setProgress(null)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Ferramentas</CardTitle>
        <CardDescription>Ações manuais de manutenção.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        <div className="grid justify-items-start gap-1.5">
          <AutoUpdateButton />
          <p className="text-xs text-muted-foreground">Atualiza já os aplicativos marcados como "Auto-atualizar", fora da janela de horário.</p>
        </div>
        <div className="grid justify-items-start gap-1.5">
          <Button variant="secondary" loading={progress !== null} onClick={refreshAll}>
            {progress ? (progress.total ? `Atualizando ${progress.done + 1} de ${progress.total}…` : 'Buscando a lista…') : 'Atualizar dados de todos os apps'}
          </Button>
          <p className="text-xs text-muted-foreground">Busca de novo nome, versão e ícone de cada aplicativo no Chocolatey e no feed interno.</p>
        </div>
      </CardContent>
    </Card>
  )
}
