import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { HashRouter } from 'react-router-dom'
import './index.css'
import App from './App'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { followSystemTheme } from '@/lib/theme'

followSystemTheme()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <TooltipProvider delayDuration={300}>
      {/* Rotas por hash (#/aplicativos), como no painel antigo: o servidor só precisa entregar o index.html. */}
      <HashRouter>
        <App />
      </HashRouter>
      <Toaster position="bottom-right" />
    </TooltipProvider>
  </StrictMode>,
)
