import type { ReactNode } from 'react'

type Props = {
  title: string
  /** Ações da tela (botões) à direita do título. Regra do painel: no máximo uma primária. */
  actions?: ReactNode
}

export function PageHeader({ title, actions }: Props) {
  return (
    <header className="mb-[18px] flex flex-wrap items-center justify-between gap-4">
      <h1 className="text-[22px] font-semibold tracking-[-0.01em]">{title}</h1>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </header>
  )
}
