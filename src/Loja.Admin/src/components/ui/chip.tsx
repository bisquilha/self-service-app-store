import * as React from "react"
import { XIcon } from "lucide-react"
import { cn } from "@/lib/utils"

type ChipProps = React.ComponentProps<"span"> & {
  /** Mostra o botão de remover. */
  onRemove?: () => void
  /** Texto lido por leitores de tela no botão de remover (por exemplo, "Remover LOJA-TI"). */
  removeLabel?: string
  /** `warning` marca um valor com problema, como um grupo que não existe mais no AD. */
  tone?: "default" | "warning"
}

/** Etiqueta curta, como o nome de um grupo do AD. Com `onRemove` ganha um botão de remover acessível por teclado. */
function Chip({ className, tone = "default", onRemove, removeLabel = "Remover", children, ...props }: ChipProps) {
  return (
    <span
      data-slot="chip"
      className={cn(
        "inline-flex max-w-full items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs leading-5 font-medium",
        tone === "warning" ? "border-transparent bg-warning-soft text-warning" : "border-border bg-muted text-foreground",
        onRemove && "pr-1",
        className
      )}
      {...props}
    >
      <span className="inline-flex min-w-0 items-center gap-1 truncate">{children}</span>
      {onRemove ? (
        <button
          type="button"
          onClick={onRemove}
          aria-label={removeLabel}
          className="grid size-4 shrink-0 place-items-center rounded-full outline-none hover:bg-foreground/10 focus-visible:ring-2 focus-visible:ring-ring"
        >
          <XIcon className="size-3" aria-hidden="true" />
        </button>
      ) : null}
    </span>
  )
}

export { Chip }
