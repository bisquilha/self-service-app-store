import { lazy, type ComponentType, type LazyExoticComponent } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Shell } from '@/components/shell/shell'
import { routes } from '@/routes'

// Cada tela vai em um arquivo à parte, baixado só quando a pessoa entra nela. A chave é o `path` de routes.tsx.
const pages: Record<string, LazyExoticComponent<ComponentType>> = {
  'visao-geral': lazy(() => import('@/pages/overview').then((module) => ({ default: module.OverviewPage }))),
  aplicativos: lazy(() => import('@/pages/apps').then((module) => ({ default: module.AppsPage }))),
  atualizacoes: lazy(() => import('@/pages/updates').then((module) => ({ default: module.UpdatesPage }))),
  grupos: lazy(() => import('@/pages/groups').then((module) => ({ default: module.GroupsPage }))),
  maquinas: lazy(() => import('@/pages/machines').then((module) => ({ default: module.MachinesPage }))),
  'ver-como': lazy(() => import('@/pages/preview').then((module) => ({ default: module.PreviewPage }))),
  historico: lazy(() => import('@/pages/history').then((module) => ({ default: module.HistoryPage }))),
  configuracoes: lazy(() => import('@/pages/settings').then((module) => ({ default: module.SettingsPage }))),
}

// A galeria só serve para conferir componentes: arquivo à parte que o painel normal nunca baixa.
const Gallery = lazy(() => import('@/pages/gallery').then((module) => ({ default: module.Gallery })))

export default function App() {
  return (
    <Routes>
      <Route element={<Shell />}>
        {routes.map((route) => {
          const Page = pages[route.path]
          return <Route key={route.path} path={route.pattern} element={<Page />} />
        })}
        {/* Rota oculta (fora do menu): confere os componentes nos dois temas. */}
        <Route path="_galeria" element={<Gallery />} />
        <Route path="*" element={<Navigate to="/visao-geral" replace />} />
      </Route>
    </Routes>
  )
}
