import { useState } from 'react'
import { plural } from '@/lib/format'
import type { DayStat } from '@/lib/types'
import { cn } from '@/lib/utils'

type Range = 7 | 14

const RANGES: Range[] = [7, 14]
const SERIES = [
  { key: 'success', label: 'Concluídos', singular: 'concluído', plural: 'concluídos', color: 'bg-success' },
  { key: 'failed', label: 'Falha ou sem retorno', singular: 'com falha', plural: 'com falha', color: 'bg-destructive' },
  { key: 'denied', label: 'Negados', singular: 'negado', plural: 'negados', color: 'bg-warning' },
] as const

/** Maior valor "redondo" (1, 2, 5, 10, 20, 50...) que cabe o dado, com no mínimo 4: o eixo não fica com 1 e 2. */
function niceMax(value: number): number {
  if (value <= 4) return 4
  const power = 10 ** Math.floor(Math.log10(value))
  for (const step of [1, 2, 5, 10]) if (value <= step * power) return step * power
  return 10 * power
}

const total = (day: DayStat) => day.success + day.failed + day.denied

/**
 * Pedidos por dia, empilhados por resultado. O número de cada dia fica escrito sobre a barra, o eixo tem valores e o
 * período alterna entre 7 e 14 dias. Passar o mouse mostra o detalhe do dia; leitores de tela recebem uma tabela.
 */
export function RequestsChart({ days }: { days: DayStat[] }) {
  const [range, setRange] = useState<Range>(7)
  const [hovered, setHovered] = useState<number | null>(null)

  const shown = days.slice(-range)
  const max = niceMax(Math.max(0, ...shown.map(total)))
  const ticks = [0, max / 2, max]
  const sums = SERIES.map((series) => ({ ...series, sum: shown.reduce((acc, day) => acc + day[series.key], 0) }))
  const grand = sums.reduce((acc, series) => acc + series.sum, 0)

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p aria-live="polite" className="text-sm text-muted-foreground">
          <strong className="text-base font-semibold text-foreground tabular-nums">{grand.toLocaleString('pt-BR')}</strong> {grand === 1 ? 'pedido' : 'pedidos'} nos últimos {range} dias
        </p>
        <div role="group" aria-label="Período do gráfico" className="inline-flex rounded-lg border bg-muted/50 p-0.5">
          {RANGES.map((option) => (
            <button
              key={option}
              type="button"
              aria-pressed={range === option}
              onClick={() => {
                setRange(option)
                setHovered(null)
              }}
              className={cn(
                'h-7 rounded-md px-3 text-[13px] outline-none focus-visible:ring-2 focus-visible:ring-ring',
                range === option ? 'bg-card font-medium text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
              )}
            >
              {option} dias
            </button>
          ))}
        </div>
      </div>

      <div aria-hidden="true" className="flex gap-2">
        <div className="relative h-52 w-7 shrink-0 text-right text-xs text-muted-foreground tabular-nums">
          {ticks.map((tick) => (
            <span key={tick} className="absolute right-0 translate-y-1/2" style={{ bottom: `${(tick / max) * 100}%` }}>
              {tick}
            </span>
          ))}
        </div>
        <div className="relative min-w-0 flex-1">
          <div className="pointer-events-none absolute inset-x-0 top-0 h-52">
            {ticks.map((tick) => (
              <div key={tick} className={cn('absolute inset-x-0 border-t', tick === 0 ? 'border-border' : 'border-dashed border-border/70')} style={{ bottom: `${(tick / max) * 100}%` }} />
            ))}
          </div>
          <div className="relative flex gap-1.5 sm:gap-3">
            {shown.map((day, index) => {
              const sum = total(day)
              return (
                <div key={day.day} className="flex min-w-0 flex-1 flex-col" onMouseEnter={() => setHovered(index)} onMouseLeave={() => setHovered(null)}>
                  <div className="relative h-52">
                    {sum > 0 ? (
                      <>
                        <span className="absolute inset-x-0 text-center text-xs font-medium tabular-nums" style={{ bottom: `calc(${(sum / max) * 100}% + 3px)` }}>
                          {sum}
                        </span>
                        <div className="absolute inset-x-[12%] bottom-0 flex flex-col-reverse overflow-hidden rounded-t-sm" style={{ height: `${(sum / max) * 100}%` }}>
                          {SERIES.map((series) => (day[series.key] > 0 ? <div key={series.key} className={series.color} style={{ height: `${(day[series.key] / sum) * 100}%` }} /> : null))}
                        </div>
                      </>
                    ) : (
                      <div className="absolute inset-x-[12%] bottom-0 h-0.5 rounded-full bg-border" />
                    )}
                    {hovered === index ? (
                      <div
                        className={cn(
                          'absolute bottom-full z-10 mb-1 w-max min-w-36 rounded-lg border bg-popover px-3 py-2 text-xs text-popover-foreground shadow-md',
                          index === 0 ? 'left-0' : index === shown.length - 1 ? 'right-0' : 'left-1/2 -translate-x-1/2',
                        )}
                      >
                        <p className="mb-1 font-semibold">{day.day}</p>
                        {SERIES.map((series) => (
                          <p key={series.key} className="flex items-center gap-1.5 tabular-nums">
                            <span className={cn('size-2 rounded-sm', series.color)} />
                            {plural(day[series.key], series.singular, series.plural)}
                          </p>
                        ))}
                      </div>
                    ) : null}
                  </div>
                  {/* com 14 dias, no celular, só um rótulo em dois para não se sobrepor */}
                  <span className={cn('mt-1.5 truncate text-center text-xs text-muted-foreground tabular-nums', range === 14 && index % 2 === 1 && 'max-sm:invisible')}>{day.day}</span>
                </div>
              )
            })}
          </div>
        </div>
      </div>

      <ul className="flex flex-wrap gap-x-5 gap-y-1 text-[13px]">
        {sums.map((series) => (
          <li key={series.key} className="flex items-center gap-1.5">
            <span className={cn('size-2.5 rounded-sm', series.color)} aria-hidden="true" />
            {series.label}
            <span className="text-muted-foreground tabular-nums">({series.sum.toLocaleString('pt-BR')})</span>
          </li>
        ))}
      </ul>

      <table className="sr-only">
        <caption>Pedidos por dia nos últimos {range} dias</caption>
        <thead>
          <tr>
            <th scope="col">Dia</th>
            <th scope="col">Concluídos</th>
            <th scope="col">Com falha ou sem retorno</th>
            <th scope="col">Negados</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((day) => (
            <tr key={day.day}>
              <th scope="row">{day.day}</th>
              <td>{day.success}</td>
              <td>{day.failed}</td>
              <td>{day.denied}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
