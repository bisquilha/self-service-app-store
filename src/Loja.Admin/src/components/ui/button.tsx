import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "cn"
import { Slot } from "radix-ui"
import { Loader2Icon } from "lucide-react"

const secondary = "border-border bg-card text-foreground hover:bg-muted aria-expanded:bg-muted"

/**
 * Botão do painel.
 *
 * Alturas (as mesmas dos campos): `default` = 36 px e `sm` = 32 px (ações dentro de tabela e de cartão).
 * Todo botão de texto tem largura mínima, para ações irmãs (Atualizar, Desinstalar, Instalar) não ficarem desiguais.
 *
 * Variantes:
 * - `default`: primário. No máximo um por tela ou por região.
 * - `secondary`: secundário, com borda. `outline` é igual e existe porque componentes do shadcn usam esse nome.
 * - `destructive`: confirma uma ação perigosa (em geral dentro de um modal de confirmação).
 * - `ghost`: discreto (ícones, "Limpar").
 * - `link`: texto de link, dentro de frases.
 */
const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-lg border border-transparent bg-clip-padding text-sm font-medium whitespace-nowrap transition-colors outline-none select-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90",
        secondary,
        outline: secondary,
        destructive: "bg-destructive text-destructive-foreground hover:bg-destructive/90 focus-visible:ring-destructive/30",
        ghost: "text-foreground hover:bg-muted aria-expanded:bg-muted",
        link: "text-primary underline-offset-4 hover:underline",
      },
      size: {
        default: "h-9 min-w-20 gap-1.5 px-3.5",
        sm: "h-8 min-w-16 gap-1 px-3 text-[13px]",
        xs: "h-7 gap-1 px-2 text-xs [&_svg:not([class*='size-'])]:size-3.5",
        lg: "h-10 min-w-24 gap-2 px-4",
        icon: "size-9",
        "icon-sm": "size-8",
        "icon-xs": "size-7",
        "icon-lg": "size-10",
      },
    },
    compoundVariants: [{ variant: "link", className: "h-auto min-w-0 px-0" }],
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
)

function Button({
  className,
  variant = "default",
  size = "default",
  asChild = false,
  loading = false,
  disabled,
  children,
  ...props
}: React.ComponentProps<"button"> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean
    /** Desabilita o botão e mostra um indicador de andamento (evita clique duplo em ações demoradas). */
    loading?: boolean
  }) {
  const Comp = asChild ? Slot.Root : "button"

  return (
    <Comp
      data-slot="button"
      data-variant={variant}
      data-size={size}
      data-loading={loading || undefined}
      aria-busy={loading || undefined}
      disabled={disabled || loading}
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    >
      {asChild ? (
        children
      ) : (
        <>
          {loading ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : null}
          {children}
        </>
      )}
    </Comp>
  )
}

export { Button, buttonVariants }
