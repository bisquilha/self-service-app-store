import { ChevronLeftIcon, ChevronRightIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import { plural } from "@/lib/format"

type PaginationBarProps = {
  total: number
  /** Página atual, a partir de 1. */
  page: number
  pageSize: number
  onPageChange: (page: number) => void
  /** O que está sendo contado, no singular e no plural: "pedido" e "pedidos". */
  singular: string
  pluralForm?: string
}

/** Rodapé de listas grandes: faixa mostrada, total e botões Anterior e Próxima. */
function PaginationBar({ total, page, pageSize, onPageChange, singular, pluralForm }: PaginationBarProps) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const first = total === 0 ? 0 : (page - 1) * pageSize + 1
  const last = Math.min(total, page * pageSize)

  return (
    <nav aria-label="Paginação" className="flex flex-wrap items-center justify-between gap-3 text-sm text-muted-foreground">
      <span aria-live="polite">
        {total === 0 ? plural(0, singular, pluralForm) : `${first.toLocaleString("pt-BR")}–${last.toLocaleString("pt-BR")} de ${plural(total, singular, pluralForm)}`}
      </span>
      <div className="flex items-center gap-2">
        <span>
          Página {page.toLocaleString("pt-BR")} de {pages.toLocaleString("pt-BR")}
        </span>
        <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
          <ChevronLeftIcon aria-hidden="true" />
          Anterior
        </Button>
        <Button variant="secondary" size="sm" disabled={page >= pages} onClick={() => onPageChange(page + 1)}>
          Próxima
          <ChevronRightIcon aria-hidden="true" />
        </Button>
      </div>
    </nav>
  )
}

export { PaginationBar }
