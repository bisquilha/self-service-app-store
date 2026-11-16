import { Skeleton } from "@/components/ui/skeleton"
import { TableBody, TableCell, TableRow } from "@/components/ui/table"

type TableSkeletonProps = {
  rows?: number
  columns: number
}

/** Corpo de tabela enquanto os dados carregam. Use no lugar do `TableBody` e leia-se "Carregando" no `aria-busy` da tabela. */
function TableSkeleton({ rows = 5, columns }: TableSkeletonProps) {
  return (
    <TableBody aria-hidden="true">
      {Array.from({ length: rows }, (_, row) => (
        <TableRow key={row} className="hover:bg-transparent">
          {Array.from({ length: columns }, (_, column) => (
            <TableCell key={column}>
              <Skeleton className={column === 0 ? "h-4 w-40" : "h-4 w-20"} />
            </TableCell>
          ))}
        </TableRow>
      ))}
    </TableBody>
  )
}

export { TableSkeleton }
