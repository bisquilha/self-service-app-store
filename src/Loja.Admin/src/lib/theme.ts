import { useSyncExternalStore } from 'react'

/**
 * Tema do painel. Por padrão segue o sistema (claro ou escuro) e liga a classe `.dark` no <html>.
 * `setThemeChoice` existe para a galeria de componentes poder conferir os dois temas sem mexer no sistema;
 * a escolha não é guardada: ao recarregar volta a seguir o sistema.
 */
export type ThemeChoice = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

const media = window.matchMedia('(prefers-color-scheme: dark)')
const listeners = new Set<() => void>()
let choice: ThemeChoice = 'system'

function resolve(): ResolvedTheme {
  return choice === 'system' ? (media.matches ? 'dark' : 'light') : choice
}

function apply(): void {
  document.documentElement.classList.toggle('dark', resolve() === 'dark')
  listeners.forEach((listener) => listener())
}

export function followSystemTheme(): void {
  apply()
  media.addEventListener('change', apply)
}

export function setThemeChoice(next: ThemeChoice): void {
  choice = next
  apply()
}

export function getThemeChoice(): ThemeChoice {
  return choice
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** Tema que está valendo agora. Muda sozinho quando o sistema troca. */
export function useResolvedTheme(): ResolvedTheme {
  return useSyncExternalStore(subscribe, resolve)
}

/** A escolha atual (sistema, claro ou escuro). */
export function useThemeChoice(): ThemeChoice {
  return useSyncExternalStore(subscribe, getThemeChoice)
}
