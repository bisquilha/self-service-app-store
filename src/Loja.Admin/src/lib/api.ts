/**
 * Cliente único da API do painel (/api/admin).
 * Mesma origem do servidor da loja: o navegador manda o login do Windows (Negotiate) sozinho.
 */

/** Toda requisição que não seja GET para /api/admin precisa deste cabeçalho (proteção contra CSRF do servidor). */
const ADMIN_HEADERS = { 'X-Requested-With': 'loja-admin' } as const

export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** Texto para mostrar a quem usa o painel quando uma chamada falha. */
export function messageOf(error: unknown, fallback = 'Algo deu errado. Tente de novo.'): string {
  return error instanceof Error && error.message ? error.message : fallback
}

type RequestOptions = {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  /** Objeto vira JSON. FormData vai como está (envio de arquivo). */
  body?: unknown
  signal?: AbortSignal
}

/** `path` é relativo a /api/admin, por exemplo `/apps` ou `/machines/PC-HQ-521`. */
export async function api<T>(path: string, { method = 'GET', body, signal }: RequestOptions = {}): Promise<T> {
  const headers = new Headers(ADMIN_HEADERS)
  let payload: BodyInit | undefined
  if (body instanceof FormData) {
    payload = body
  } else if (body !== undefined) {
    headers.set('Content-Type', 'application/json; charset=utf-8')
    payload = JSON.stringify(body)
  }

  let response: Response
  try {
    response = await fetch(`/api/admin${path}`, { method, headers, body: payload, signal, credentials: 'same-origin' })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError('Não foi possível falar com o servidor da loja.', 0)
  }

  if (!response.ok) throw new ApiError(await errorMessage(response), response.status)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/** O servidor responde `{ "message": "..." }` nos erros da API; o portão do /admin responde texto puro. */
async function errorMessage(response: Response): Promise<string> {
  try {
    const data = (await response.json()) as { message?: string }
    if (data?.message) return data.message
  } catch {
    // corpo vazio ou que não é JSON
  }
  if (response.status === 401) return 'Sessão não autenticada. Abra o painel pelo endereço da loja.'
  if (response.status === 403) return 'Acesso restrito à TI (grupo de administração da loja).'
  return `Erro ${response.status} no servidor.`
}
