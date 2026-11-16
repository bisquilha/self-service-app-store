import { RotateCwIcon, TriangleAlertIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"

type ErrorStateProps = {
  /** A mensagem que veio do servidor (ApiError.message). */
  message: string
  title?: string
  /** Se informado, mostra o botão "Tentar de novo". */
  onRetry?: () => void
  className?: string
}

/** Falha ao carregar uma tela ou um cartão. É anunciada por leitores de tela (role="alert"). */
function ErrorState({ message, title = "Não foi possível carregar", onRetry, className }: ErrorStateProps) {
  return (
    <div role="alert" data-slot="error-state" className={cn("flex flex-col items-center gap-2 px-6 py-10 text-center", className)}>
      <TriangleAlertIcon className="size-8 text-destructive" aria-hidden="true" />
      <p className="font-medium">{title}</p>
      <p className="max-w-md text-muted-foreground">{message}</p>
      {onRetry ? (
        <Button variant="secondary" className="mt-2" onClick={onRetry}>
          <RotateCwIcon aria-hidden="true" />
          Tentar de novo
        </Button>
      ) : null}
    </div>
  )
}

export { ErrorState }
