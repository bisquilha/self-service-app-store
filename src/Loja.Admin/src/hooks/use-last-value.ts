import { useState } from 'react'

/**
 * Devolve o último valor que não era nulo. Serve para um modal continuar mostrando o texto da ação enquanto
 * fecha (a animação de saída roda com o estado já limpo).
 */
export function useLastValue<T>(value: T | null): T | null {
  const [last, setLast] = useState<T | null>(value)
  if (value !== null && value !== last) setLast(value)
  return value ?? last
}
