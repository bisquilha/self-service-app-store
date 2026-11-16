import { useRef, useState, type ReactNode } from "react"
import { PlusIcon, TriangleAlertIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Chip } from "@/components/ui/chip"
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command"
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover"
import { cn } from "@/lib/utils"

export type ComboOption = {
  value: string
  label: string
  description?: string
  /** Texto curto à direita, como "0 membros". */
  hint?: string
  hintTone?: "default" | "warning"
}

type MultiComboboxProps = {
  /** Opções mostradas na lista. Com `onSearchChange`, quem chama busca e troca a lista. */
  options: ComboOption[]
  value: string[]
  onChange: (value: string[]) => void
  /**
   * Valor exclusivo, como "*" (todos): marcá-lo limpa os outros, e marcar outro valor tira este.
   */
  exclusiveValue?: string
  addLabel?: string
  searchPlaceholder?: string
  emptyText?: string
  loading?: boolean
  /** Busca remota: se informado, a lista não é filtrada aqui, só por quem chama. */
  onSearchChange?: (query: string) => void
  /** Valores selecionados com problema (por exemplo, grupo que não existe mais no AD): o chip fica em âmbar. */
  warnValues?: string[]
  warnLabel?: string
  /** Texto de um valor selecionado que não está (mais) em `options`. */
  labelFor?: (value: string) => string | undefined
  /** Conteúdo fixo no fim da lista, como uma caixa "mostrar todos os grupos". */
  footer?: ReactNode
  disabled?: boolean
  "aria-label"?: string
  className?: string
}

/**
 * Seleção de vários itens com busca, mostrados como chips removíveis. Serve para os grupos do AD de cada aplicativo.
 * Teclado: Enter no botão abre a lista, setas escolhem, Enter marca ou desmarca, Esc fecha.
 */
function MultiCombobox({
  options,
  value,
  onChange,
  exclusiveValue,
  addLabel = "Adicionar",
  searchPlaceholder = "Buscar…",
  emptyText = "Nada encontrado.",
  loading = false,
  onSearchChange,
  warnValues = [],
  warnLabel = "Atenção",
  labelFor,
  footer,
  disabled,
  className,
  ...rest
}: MultiComboboxProps) {
  const [open, setOpen] = useState(false)
  // Guarda o nome dos itens já escolhidos: o chip continua certo mesmo depois de a lista mudar por causa de uma busca.
  const known = useRef(new Map<string, string>())

  const display = (v: string) => options.find((o) => o.value === v)?.label ?? known.current.get(v) ?? labelFor?.(v) ?? v

  function toggle(option: ComboOption) {
    known.current.set(option.value, option.label)
    if (value.includes(option.value)) return onChange(value.filter((v) => v !== option.value))
    if (exclusiveValue !== undefined && option.value === exclusiveValue) return onChange([option.value])
    onChange([...value.filter((v) => v !== exclusiveValue), option.value])
  }

  return (
    <div
      data-slot="multi-combobox"
      className={cn("flex min-h-9 flex-wrap items-center gap-1.5 rounded-lg border border-input bg-card p-1.5", disabled && "opacity-60", className)}
    >
      {value.map((v) => {
        const warn = warnValues.includes(v)
        return (
          <Chip
            key={v}
            tone={warn ? "warning" : "default"}
            title={warn ? warnLabel : undefined}
            removeLabel={`Remover ${display(v)}`}
            onRemove={disabled ? undefined : () => onChange(value.filter((x) => x !== v))}
          >
            {warn ? <TriangleAlertIcon className="size-3 shrink-0" aria-hidden="true" /> : null}
            {display(v)}
            {warn ? <span className="sr-only"> ({warnLabel})</span> : null}
          </Chip>
        )
      })}

      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button
            type="button"
            variant="ghost"
            size="xs"
            disabled={disabled}
            role="combobox"
            aria-expanded={open}
            aria-haspopup="listbox"
            aria-label={rest["aria-label"] ? `${addLabel}: ${rest["aria-label"]}` : addLabel}
            className="text-muted-foreground"
          >
            <PlusIcon aria-hidden="true" />
            {addLabel}
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-80 p-0">
          <Command shouldFilter={!onSearchChange}>
            <CommandInput placeholder={searchPlaceholder} onValueChange={onSearchChange} />
            <CommandList>
              {loading ? (
                <div role="status" className="py-6 text-center text-sm text-muted-foreground">
                  Buscando…
                </div>
              ) : (
                <CommandEmpty>{emptyText}</CommandEmpty>
              )}
              {!loading ? (
                <CommandGroup>
                  {options.map((option) => {
                    const selected = value.includes(option.value)
                    return (
                      <CommandItem
                        key={option.value}
                        value={option.value}
                        keywords={[option.label, option.description ?? ""]}
                        data-checked={selected}
                        onSelect={() => toggle(option)}
                      >
                        <div className="min-w-0 flex-1">
                          <div className="truncate font-medium">{option.label}</div>
                          {option.description ? <div className="truncate text-xs text-muted-foreground">{option.description}</div> : null}
                        </div>
                        {option.hint ? (
                          <span className={cn("shrink-0 text-xs", option.hintTone === "warning" ? "text-warning" : "text-muted-foreground")}>{option.hint}</span>
                        ) : null}
                        {selected ? <span className="sr-only">selecionado</span> : null}
                      </CommandItem>
                    )
                  })}
                </CommandGroup>
              ) : null}
            </CommandList>
            {footer ? <div className="border-t p-2">{footer}</div> : null}
          </Command>
        </PopoverContent>
      </Popover>
    </div>
  )
}

export { MultiCombobox }
