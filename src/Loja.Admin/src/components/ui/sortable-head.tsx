import { ArrowDownIcon, ArrowUpIcon, ChevronsUpDownIcon } from "lucide-react"
import { TableHead } from "@/components/ui/table"
import type { SortState } from "@/lib/sort"
import { cn } from "@/lib/utils"

type SortableHeadProps = {
  label: string
  sortKey: string
  sort: SortState | null
  onSort: (key: string) => void
  className?: string
}

/** Cabeçalho de coluna que ordena ao clicar. Usa `aria-sort` e é um botão de verdade (Tab e Enter funcionam). Veja `useSort`. */
function SortableHead({ label, sortKey, sort, onSort, className }: SortableHeadProps) {
  const direction = sort?.key === sortKey ? sort.direction : null
  const Icon = direction === "asc" ? ArrowUpIcon : direction === "desc" ? ArrowDownIcon : ChevronsUpDownIcon

  return (
    <TableHead aria-sort={direction === "asc" ? "ascending" : direction === "desc" ? "descending" : "none"} className={className}>
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        className="-mx-1.5 inline-flex items-center gap-1 rounded-md px-1.5 py-1 font-semibold outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring"
      >
        {label}
        <Icon className={cn("size-3.5", direction ? "text-foreground" : "opacity-50")} aria-hidden="true" />
      </button>
    </TableHead>
  )
}

export { SortableHead }
