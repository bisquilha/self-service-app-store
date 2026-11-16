// Confere o contraste (WCAG 2.x) dos pares de cor do tema, lendo os tokens de src/index.css.
// Uso: npm run check:contrast   (sai com código 1 se algum par falhar)
//
// Texto normal precisa de 4,5:1. Borda de campo, trilho de switch e anel de foco (componentes de interface) precisam de 3:1.
import { readFileSync } from 'node:fs'
import path from 'node:path'

const css = readFileSync(path.resolve(import.meta.dirname, '../src/index.css'), 'utf8')

function tokens(selector) {
  const match = css.match(new RegExp(`${selector}\\s*\\{([^}]*)\\}`))
  if (!match) throw new Error(`Bloco ${selector} não encontrado em index.css`)
  return Object.fromEntries([...match[1].matchAll(/--([\w-]+):\s*([^;]+);/g)].map(([, name, value]) => [name, value.trim()]))
}

function hex(value) {
  const h = value.replace('#', '')
  const full = h.length === 3 ? [...h].map((c) => c + c).join('') : h
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16))
}

/** Aceita #rrggbb e rgb(r g b / n%), que é como o tema escreve branco translúcido. Mistura o translúcido sobre `under`. */
function color(value, under) {
  if (value.startsWith('#')) return hex(value)
  const m = value.match(/rgb\(\s*(\d+)\s+(\d+)\s+(\d+)\s*\/\s*([\d.]+)%\s*\)/)
  if (!m) throw new Error(`Cor não suportada: ${value}`)
  const alpha = Number(m[4]) / 100
  return [1, 2, 3].map((i, k) => Math.round(Number(m[i]) * alpha + under[k] * (1 - alpha)))
}

function luminance([r, g, b]) {
  const [R, G, B] = [r, g, b].map((v) => {
    const s = v / 255
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * R + 0.7152 * G + 0.0722 * B
}

function ratio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (hi + 0.05) / (lo + 0.05)
}

// [primeiro plano, fundo, mínimo, onde é usado]
const pairs = [
  ['foreground', 'background', 4.5, 'texto na página'],
  ['foreground', 'card', 4.5, 'texto em cartões e tabelas'],
  ['muted-foreground', 'background', 4.5, 'texto de apoio na página'],
  ['muted-foreground', 'card', 4.5, 'texto de apoio e placeholder em cartões'],
  ['muted-foreground', 'muted', 4.5, 'cabeçalho de tabela e selo neutro'],
  ['foreground', 'muted', 4.5, 'chip'],
  ['foreground', 'accent', 4.5, 'aviso de informação'],
  ['primary', 'background', 4.5, 'link na página'],
  ['primary', 'card', 4.5, 'link em cartão'],
  ['primary', 'accent', 4.5, 'selo "info"'],
  ['primary-foreground', 'primary', 4.5, 'botão primário'],
  ['destructive-foreground', 'destructive', 4.5, 'botão destrutivo'],
  ['success', 'success-soft', 4.5, 'selo e aviso de sucesso'],
  ['warning', 'warning-soft', 4.5, 'selo e aviso de atenção'],
  ['destructive', 'destructive-soft', 4.5, 'selo e aviso de erro'],
  ['destructive', 'card', 4.5, 'mensagem de erro de campo'],
  ['sidebar-foreground', 'sidebar', 4.5, 'menu lateral'],
  ['sidebar-muted', 'sidebar', 4.5, 'texto de apoio do menu'],
  ['sidebar-accent-foreground', 'sidebar+sidebar-accent', 4.5, 'item ativo do menu'],
  ['sidebar-primary-foreground', 'sidebar-primary', 4.5, 'marca "L" do menu'],
  ['input', 'card', 3, 'borda de campo (componente de interface)'],
  ['input', 'background', 3, 'borda de campo direto na página'],
  ['switch-off', 'card', 3, 'trilho do switch desligado'],
  ['ring', 'card', 3, 'anel de foco'],
]

let failures = 0
for (const [theme, selector] of [['claro', ':root'], ['escuro', '\\.dark']]) {
  const t = tokens(selector)
  const get = (name) => {
    const [base, overlay] = name.split('+')
    const under = base ? color(t[base], [0, 0, 0]) : undefined
    return overlay ? color(t[overlay], under) : color(t[name], [0, 0, 0])
  }
  console.log(`\nTema ${theme}`)
  for (const [fg, bg, min, use] of pairs) {
    const value = ratio(get(fg), get(bg))
    const ok = value >= min
    if (!ok) failures++
    console.log(`  ${ok ? 'OK    ' : 'FALHA '} ${value.toFixed(2).padStart(5)}:1 (mín. ${min})  ${fg} sobre ${bg}  — ${use}`)
  }
}
console.log(failures ? `\n${failures} par(es) abaixo do mínimo.` : '\nTodos os pares passam.')
process.exit(failures ? 1 : 0)
