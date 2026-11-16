import { useMemo, useState } from 'react'
import { CheckIcon, ChevronsUpDownIcon, XIcon } from 'lucide-react'
import { groupOption } from '@/components/group-select'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { useGroupSearch } from '@/hooks/use-group-search'
import type { GroupRef } from '@/lib/types'
import { cn } from '@/lib/utils'

type Props = {
  value: GroupRef | null
  onChange: (group: GroupRef | null) => void
  placeholder?: string
  /** Texto do botão que limpa a escolha. Sem ele, a escolha não pode ser desfeita pelo seletor. */
  clearLabel?: string
  id?: string
  className?: string
  'aria-label'?: string
}

/**
 * Escolha de UM grupo do AD (tela Grupos e filtros por grupo do usuário). Mesma busca do GroupSelect:
 * só LOJA-* por padrão, com a opção de procurar em todo o domínio. O resultado leva o SID junto com o nome.
 */
export function GroupPicker({ value, onChange, placeholder = 'Escolher um grupo…', clearLabel, id, className, 'aria-label': ariaLabel }: Props) {
  const [open, setOpen] = useState(false)
  const { query, setQuery, showAll, setShowAll, state, seen } = useGroupSearch()
  const options = useMemo(() => state.groups.map(groupOption), [state.groups])
  const needMoreLetters = showAll && query.trim().length < 2

  function pick(sid: string) {
    const ref = seen.current.get(sid)
    if (ref) onChange(ref)
    setOpen(false)
  }

  return (
    <div className={cn('flex items-center gap-1.5', className)}>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            id={id}
            type="button"
            variant="secondary"
            role="combobox"
            aria-expanded={open}
            aria-haspopup="listbox"
            aria-label={ariaLabel ? `${ariaLabel}: ${value ? value.name : 'nenhum'}` : undefined}
            className="min-w-0 flex-1 justify-between font-normal"
          >
            <span className={cn('truncate', !value && 'text-muted-foreground')}>{value ? value.name : placeholder}</span>
            <ChevronsUpDownIcon className="text-muted-foreground" aria-hidden="true" />
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-96 max-w-[calc(100vw-2rem)] p-0">
          <Command shouldFilter={false}>
            <CommandInput placeholder="Buscar grupo…" onValueChange={setQuery} />
            <CommandList>
              {state.loading ? (
                <div role="status" className="py-6 text-center text-sm text-muted-foreground">
                  Buscando…
                </div>
              ) : (
                <CommandEmpty>{needMoreLetters ? 'Digite ao menos 2 letras para buscar em todo o domínio.' : 'Nenhum grupo encontrado.'}</CommandEmpty>
              )}
              {!state.loading ? (
                <CommandGroup>
                  {options.map((option) => {
                    const selected = value?.sid?.toLowerCase() === option.value.toLowerCase()
                    return (
                      <CommandItem key={option.value} value={option.value} keywords={[option.label, option.description ?? '']} data-checked={selected} onSelect={() => pick(option.value)}>
                        <div className="min-w-0 flex-1">
                          <div className="truncate font-medium">{option.label}</div>
                          {option.description ? <div className="truncate text-xs text-muted-foreground">{option.description}</div> : null}
                        </div>
                        {option.hint ? <span className={cn('shrink-0 text-xs', option.hintTone === 'warning' ? 'text-warning' : 'text-muted-foreground')}>{option.hint}</span> : null}
                        {selected ? <CheckIcon className="size-4 shrink-0" aria-hidden="true" /> : null}
                        {selected ? <span className="sr-only">selecionado</span> : null}
                      </CommandItem>
                    )
                  })}
                </CommandGroup>
              ) : null}
            </CommandList>
            <div className="grid gap-2 border-t p-2">
              {state.error ? (
                <p role="alert" className="text-xs text-destructive">
                  {state.error}
                </p>
              ) : null}
              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={showAll} onCheckedChange={(checked) => setShowAll(checked === true)} />
                Mostrar todos os grupos do domínio
              </label>
            </div>
          </Command>
        </PopoverContent>
      </Popover>
      {clearLabel && value ? (
        <Button type="button" variant="ghost" size="icon" aria-label={clearLabel} title={clearLabel} onClick={() => onChange(null)}>
          <XIcon aria-hidden="true" />
        </Button>
      ) : null}
    </div>
  )
}
