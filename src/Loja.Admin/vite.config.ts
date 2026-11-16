import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// O painel é servido pelo próprio servidor da loja em /admin (mesma origem, login do Windows).
// O resultado do build vai direto para o wwwroot do servidor e apaga o que havia lá: o painel antigo está
// arquivado em loja-de-apps/legado.
export default defineConfig({
  base: '/admin/',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, 'src') },
  },
  build: {
    outDir: '../Loja.Server/wwwroot/admin',
    emptyOutDir: true,
    sourcemap: false,
  },
})
