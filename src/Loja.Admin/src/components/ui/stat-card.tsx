import type { ReactNode } from "react"
import { cn } from "@/lib/utils"

type StatCardProps = {
  label: string
  value: ReactNode
  hint?: ReactNode
  tone?: "default" | "warning" | "destructive"
  /** Com `onClick` o cartão vira um botão (atalho para filtrar a lista) e mostra `active` quando o filtro está ligado. */
  onClick?: () => void
  active?: boolean
  /** Com `href` (por exemplo, "#/historico?status=failed") o cartão vira um link para outra tela. */
  href?: string
  className?: string
}

const tones = { default: "", warning: "text-warning", destructive: "text-destructive" } as const

/** Indicador grande (número + rótulo + dica). Pode ser um botão que filtra a lista ou um link para outra tela. */
function StatCard({ label, value, hint, tone = "default", onClick, active, href, className }: StatCardProps) {
  const body = (
    <>
      <span className="block text-[12.5px] text-muted-foreground">{label}</span>
      <span className={cn("mt-0.5 block text-[28px] leading-tight font-semibold tabular-nums", tones[tone])}>{value}</span>
      {hint ? <span className="block text-xs text-muted-foreground">{hint}</span> : null}
    </>
  )
  // flex coluna alinhada ao topo: um <button> centraliza o conteúdo na vertical, e os cartões sem dica ficavam desalinhados
  const base = "flex flex-col items-start justify-start rounded-xl border bg-card px-4 py-3.5 text-left shadow-sm"
  const interactive = "cursor-pointer outline-none transition-colors hover:bg-muted/60 focus-visible:ring-3 focus-visible:ring-ring/50"

  if (href) {
    return (
      <a data-slot="stat-card" href={href} className={cn(base, interactive, className)}>
        {body}
      </a>
    )
  }
  if (!onClick) {
    return (
      <div data-slot="stat-card" className={cn(base, className)}>
        {body}
      </div>
    )
  }
  return (
    <button type="button" data-slot="stat-card" aria-pressed={active} onClick={onClick} className={cn(base, interactive, active && "border-primary ring-2 ring-primary/30", className)}>
      {body}
    </button>
  )
}

export { StatCard }
