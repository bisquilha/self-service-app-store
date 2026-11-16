import * as React from "react"
import { cn } from "@/lib/utils"

type StatusDotProps = React.ComponentProps<"span"> & {
  tone?: "success" | "destructive" | "warning" | "muted"
}

const tones = {
  success: "bg-success",
  destructive: "bg-destructive",
  warning: "bg-warning",
  muted: "bg-muted-foreground",
} as const

/** Ponto de situação + texto na MESMA linha (corrige o ponto solto acima de "online" no detalhe da máquina). */
function StatusDot({ tone = "muted", className, children, ...props }: StatusDotProps) {
  return (
    <span data-slot="status-dot" className={cn("inline-flex items-center gap-1.5", className)} {...props}>
      <span className={cn("size-2 shrink-0 rounded-full", tones[tone])} aria-hidden="true" />
      {children}
    </span>
  )
}

export { StatusDot }
