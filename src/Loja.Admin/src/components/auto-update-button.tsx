import { useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { api, messageOf } from '@/lib/api'
import { plural } from '@/lib/format'

/**
 * "Rodar atualização automática agora": manda atualizações a várias máquinas de uma vez, então sempre pede confirmação.
 * Existe na Visão geral e em Configurações; os dois usam este componente para não divergirem.
 */
export function AutoUpdateButton() {
  const [open, setOpen] = useState(false)

  async function run() {
    try {
      const { started } = await api<{ started: number }>('/autoupdate/run', { method: 'POST' })
      if (started > 0) toast.success(`${plural(started, 'atualização enviada', 'atualizações enviadas')} às máquinas.`)
      else toast.info('Nenhuma atualização pendente nas máquinas elegíveis.')
    } catch (error) {
      toast.error(messageOf(error))
      throw error
    }
  }

  return (
    <>
      <Button variant="secondary" onClick={() => setOpen(true)}>
        Rodar atualização automática agora
      </Button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title="Rodar a atualização automática agora?"
        description="Envia atualizações para as máquinas com aplicativos desatualizados que estão marcados como Auto-atualizar, mesmo fora da janela de horário. Respeita o limite de máquinas por ciclo dos parâmetros."
        confirmLabel="Rodar agora"
        onConfirm={run}
      />
    </>
  )
}
