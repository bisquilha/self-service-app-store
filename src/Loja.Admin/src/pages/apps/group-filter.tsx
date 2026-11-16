import { useState } from 'react'
import { CheckIcon, ChevronDownIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { plural } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Um grupo que aparece em algum aplicativo da loja. `key` é o SID (ou "*", ou o nome nos grupos antigos sem SID). */
export type GroupFilterOption = { key: string; name: string; count: number }

type Props = {
  options: GroupFilterOption[]
  value: string[]
  onChange: (keys: string[]) => void
}

/**
 * Filtro "Grupo" da lista de aplicativos: vários grupos de uma vez (basta ter um deles). Só oferece os grupos que a loja
 * já usa, então a lista é local e não consulta o AD. Aplicativos liberados para "Todos os usuários" também entram.
 */
export function GroupFilter({ options, value, onChange }: Props) {
  const [open, setOpen] = useState(false)
  const selected = new Set(value)

  function toggle(key: string) {
    onChange(selected.has(key) ? value.filter((k) => k !== key) : [...value, key])
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button type="button" variant="secondary" aria-haspopup="listbox" aria-expanded={open} aria-label={value.length ? `Grupo: ${plural(value.length, 'selecionado', 'selecionados')}` : 'Grupo'} className="w-44 justify-between font-normal">
          <span className="truncate">{value.length ? `Grupo · ${value.length}` : 'Todos os grupos'}</span>
          <ChevronDownIcon className="text-muted-foreground" aria-hidden="true" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-80 max-w-[calc(100vw-2rem)] p-0">
        <Command>
          <CommandInput placeholder="Buscar grupo…" />
          <CommandList>
            <CommandEmpty>Nenhum grupo usado pela loja combina com a busca.</CommandEmpty>
            <CommandGroup>
              {options.map((option) => {
                const on = selected.has(option.key)
                return (
                  <CommandItem key={option.key} value={option.key} keywords={[option.name]} data-checked={on} onSelect={() => toggle(option.key)}>
                    <span className={cn('grid size-4 shrink-0 place-items-center rounded-[4px] border', on ? 'border-primary bg-primary text-primary-foreground' : 'border-input')} aria-hidden="true">
                      {on ? <CheckIcon className="size-3.5" /> : null}
                    </span>
                    <span className="min-w-0 flex-1 truncate font-medium">{option.name}</span>
                    <span className="shrink-0 text-xs text-muted-foreground">{plural(option.count, 'aplicativo')}</span>
                    {on ? <span className="sr-only">selecionado</span> : null}
                  </CommandItem>
                )
              })}
            </CommandGroup>
          </CommandList>
          <div className="flex items-center justify-between gap-2 border-t p-2 text-xs text-muted-foreground">
            <span>Mostra também os aplicativos liberados para Todos os usuários.</span>
            {value.length ? (
              <Button type="button" variant="ghost" size="xs" onClick={() => onChange([])}>
                Limpar
              </Button>
            ) : null}
          </div>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
