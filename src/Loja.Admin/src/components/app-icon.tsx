import { useState } from 'react'

/** Cor estável por nome, escura o bastante para o texto branco passar de 4,5:1. */
function avatarColor(title: string): string {
  let hash = 0
  for (const char of title) hash = (hash * 31 + char.charCodeAt(0)) % 360
  return `hsl(${hash} 45% 30%)`
}

/**
 * Ícone do aplicativo, vindo de /api/icons/{id} (fora de /api/admin). Sem ícone, mostra a inicial do nome numa cor
 * que depende do nome, para os aplicativos sem ícone não ficarem todos iguais.
 */
export function AppIcon({ id, title }: { id: number; title: string }) {
  const [failed, setFailed] = useState(false)

  if (failed) {
    return (
      <div aria-hidden="true" className="grid size-8 shrink-0 place-items-center rounded-[7px] font-bold text-white" style={{ background: avatarColor(title) }}>
        {title.charAt(0).toUpperCase() || '?'}
      </div>
    )
  }
  return <img src={`/api/icons/${id}`} alt="" className="size-8 shrink-0 rounded-[7px] object-contain" onError={() => setFailed(true)} />
}
