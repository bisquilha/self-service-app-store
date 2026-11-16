import { useMemo } from "react"
import { Checkbox } from "@/components/ui/checkbox"
import { MultiCombobox, type ComboOption } from "@/components/ui/multi-combobox"
import { useGroupSearch } from "@/hooks/use-group-search"
import { plural } from "@/lib/format"
import type { AdGroup, GroupRef } from "@/lib/types"

export const EVERYONE: ComboOption = { value: "*", label: "Todos os usuários", description: "Qualquer pessoa do domínio" }

/** Chave do item no combobox: o SID (identidade do grupo); "*" para todos; grupo antigo sem SID usa o nome. */
export function groupKey(group: GroupRef): string {
  return group.name === "*" && !group.sid ? "*" : (group.sid ?? `nome:${group.name.toLowerCase()}`)
}

/** Dois conjuntos de grupos são iguais quando têm os mesmos SIDs (ou nomes, nos grupos antigos). */
export function sameGroupSet(a: GroupRef[], b: GroupRef[]): boolean {
  const keys = new Set(b.map(groupKey))
  return a.length === b.length && a.every((group) => keys.has(groupKey(group)))
}

/** "Todos os usuários": caso especial, sem SID. */
export function isEveryone(group: GroupRef): boolean {
  return group.name === "*" && !group.sid
}

/** Nome para mostrar: "*" é "Todos os usuários". */
export function groupLabel(group: GroupRef): string {
  return group.name === "*" && !group.sid ? EVERYONE.label : group.name
}

/** Como um grupo do AD aparece numa lista de escolha: nome, descrição e membros diretos. */
export function groupOption(group: AdGroup): ComboOption {
  const empty = group.memberCount === 0
  return {
    value: group.sid,
    label: group.name,
    description: group.description ?? undefined,
    // Em grupos primários (Domain Users) o AD não informa membros: sem texto, para não afirmar nada errado.
    hint: group.memberCount === null ? undefined : plural(group.memberCount, "membro"),
    hintTone: empty ? "warning" : "default",
  }
}

type GroupSelectProps = {
  value: GroupRef[]
  onChange: (groups: GroupRef[]) => void
  /** Nomes de grupos já salvos que não existem mais no AD: o chip fica em âmbar. */
  missing?: string[]
  disabled?: boolean
  "aria-label"?: string
}

/**
 * Seletor de grupos do AD (substitui o texto livre). Busca no servidor (GET /api/admin/groups): por padrão só os grupos
 * da loja (LOJA-*), com a opção de procurar em todo o domínio. "Todos os usuários" (`*`) é exclusivo.
 * O valor guarda o SID de cada grupo junto com o nome; o servidor confere tudo de novo ao salvar.
 */
function GroupSelect({ value, onChange, missing = [], disabled, "aria-label": ariaLabel = "Grupos do AD" }: GroupSelectProps) {
  const { query, setQuery, showAll, setShowAll, state, seen } = useGroupSearch()

  const options = useMemo(() => {
    const text = query.trim().toLowerCase()
    const everyone = !text || EVERYONE.label.toLowerCase().includes(text) ? [EVERYONE] : []
    return [...everyone, ...state.groups.map(groupOption)]
  }, [query, state.groups])

  const needMoreLetters = showAll && query.trim().length < 2
  const byKey = new Map(value.map((group) => [groupKey(group), group]))
  const warnKeys = value.filter((group) => missing.some((name) => name.toLowerCase() === group.name.toLowerCase())).map(groupKey)

  function change(keys: string[]) {
    onChange(
      keys.flatMap((key) => {
        if (key === "*") return [{ name: "*", sid: null }]
        const ref = byKey.get(key) ?? seen.current.get(key)
        return ref ? [ref] : []
      })
    )
  }

  return (
    <MultiCombobox
      aria-label={ariaLabel}
      addLabel="Adicionar grupo"
      searchPlaceholder="Buscar grupo…"
      emptyText={needMoreLetters ? "Digite ao menos 2 letras para buscar em todo o domínio." : "Nenhum grupo encontrado."}
      options={options}
      value={value.map(groupKey)}
      onChange={change}
      exclusiveValue="*"
      loading={state.loading}
      onSearchChange={setQuery}
      warnValues={warnKeys}
      warnLabel="Grupo não encontrado no AD"
      labelFor={(key) => (key === "*" ? EVERYONE.label : byKey.get(key)?.name)}
      disabled={disabled}
      footer={
        <div className="grid gap-2">
          {state.error ? (
            <p role="alert" className="text-xs text-destructive">
              {state.error}
            </p>
          ) : null}
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={showAll} onCheckedChange={(checked) => setShowAll(checked === true)} />
            Mostrar todos os grupos do domínio
          </label>
          {needMoreLetters ? <p className="text-xs text-muted-foreground">Digite ao menos 2 letras para buscar em todo o domínio.</p> : null}
        </div>
      }
    />
  )
}

export { GroupSelect }
