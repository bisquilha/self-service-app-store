import type { ReactNode } from "react"
import { InboxIcon, type LucideIcon } from "lucide-react"
import { cn } from "@/lib/utils"

type EmptyStateProps = {
  title: string
  description?: ReactNode
  icon?: LucideIcon
  /** Botão ou link da ação que resolve o vazio (por exemplo, "Adicionar aplicativo"). */
  action?: ReactNode
  className?: string
}

/** Lista ou busca sem resultado. Diz o que houve e, quando der, o que fazer. */
function EmptyState({ title, description, icon: Icon = InboxIcon, action, className }: EmptyStateProps) {
  return (
    <div data-slot="empty-state" className={cn("flex flex-col items-center gap-2 px-6 py-10 text-center", className)}>
      <Icon className="size-8 text-muted-foreground" aria-hidden="true" />
      <p className="font-medium">{title}</p>
      {description ? <p className="max-w-md text-muted-foreground">{description}</p> : null}
      {action ? <div className="mt-2">{action}</div> : null}
    </div>
  )
}

export { EmptyState }
