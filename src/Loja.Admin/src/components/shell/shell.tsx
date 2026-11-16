import { Suspense } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Skeleton } from '@/components/ui/skeleton'
import { useAdminInfo } from '@/hooks/use-admin-info'
import { cn } from '@/lib/utils'
import { routes } from '@/routes'

export function Shell() {
  const { info, error, loading } = useAdminInfo()

  return (
    <div className="min-h-screen lg:grid lg:grid-cols-[236px_1fr]">
      <aside className="flex flex-col bg-sidebar text-sidebar-foreground lg:sticky lg:top-0 lg:h-screen">
        <div className="flex items-center gap-3 px-[18px] pt-5 pb-4">
          <div
            aria-hidden="true"
            className="grid size-9 shrink-0 place-items-center rounded-[9px] bg-sidebar-primary text-lg font-bold text-sidebar-primary-foreground"
          >
            L
          </div>
          <div className="min-w-0">
            <strong className="block text-[14.5px] text-sidebar-accent-foreground">Loja de Aplicativos</strong>
            <span className="text-xs text-sidebar-muted">Painel da TI</span>
          </div>
        </div>

        <nav aria-label="Seções do painel" className="flex gap-0.5 overflow-x-auto px-2.5 py-2 [scrollbar-width:thin] lg:flex-1 lg:flex-col lg:overflow-visible">
          {routes.map(({ path, label, icon: Icon }) => (
            <NavLink
              key={path}
              to={`/${path}`}
              className={({ isActive }) =>
                cn(
                  'flex items-center gap-2.5 rounded-lg px-3 py-2 font-medium whitespace-nowrap text-sidebar-foreground/90 outline-none',
                  'hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-2 focus-visible:ring-sidebar-ring',
                  isActive && 'bg-sidebar-accent text-sidebar-accent-foreground shadow-[inset_3px_0_0_var(--sidebar-primary)]',
                )
              }
            >
              <Icon className="size-[18px] shrink-0" aria-hidden="true" />
              {label}
            </NavLink>
          ))}
        </nav>

        <div className="grid gap-0.5 border-t border-sidebar-border px-[18px] py-3.5 text-xs text-sidebar-muted">
          <span className="truncate text-sidebar-foreground" aria-live="polite">
            {loading ? 'Carregando…' : error ? 'Não foi possível identificar o usuário' : info?.user}
          </span>
          {info?.serverVersion ? <span>Servidor {info.serverVersion}</span> : null}
        </div>
      </aside>

      <main className="min-w-0 px-4 py-5 pb-10 lg:px-7">
        {error ? (
          <Alert variant="destructive" className="mb-4">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        ) : null}
        <Suspense
          fallback={
            <div className="grid gap-3" aria-busy="true">
              <Skeleton className="h-8 w-48" />
              <Skeleton className="h-40 w-full" />
            </div>
          }
        >
          <Outlet />
        </Suspense>
      </main>
    </div>
  )
}
