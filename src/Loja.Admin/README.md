# Loja.Admin: painel novo da TI

Painel de administração da Loja de Aplicativos em React, servido pelo próprio servidor da loja em **`/admin`** (servidor 1.5.0 em diante). Substituiu o painel em JavaScript puro das versões até 1.4.1, que está arquivado em `loja-de-apps/legado/` (com instruções para voltar atrás). O endereço da prévia, `/admin/v2`, redireciona para `/admin/`.

Estado: **as oito telas estão prontas** (abaixo; Atualizações e Grupos entraram na 1.6.0). Casca com menu lateral, rotas por hash (`#/aplicativos`, `#/maquinas/HOST`, ...), cliente de API único, tema claro e escuro e biblioteca de componentes.

Para ver todos os componentes nos dois temas, abra a **galeria**: `/admin/#/_galeria` (rota oculta, fora do menu, carregada à parte).

## Telas

| Tela | Estado | Onde |
|---|---|---|
| Configurações | **Pronta** | `src/pages/settings/` (versões do app, parâmetros, servidor, ferramentas e a aba Auditoria) |
| Aplicativos | **Pronta** | `src/pages/apps/` (tabela com filtros, filtro por grupo e chips, seleção e edição de grupos em lote com Desfazer, chaves com desfazer, editar grupos, nomes no Windows, adicionar app). Filtros na URL (`?grupo=SID&q=...`) |
| Atualizações | **Pronta (1.6.0)** | `src/pages/updates/` (aplicativos com máquinas em versão antiga, máquinas por aplicativo e "Atualizar em todas" com o alcance explícito) |
| Grupos | **Pronta (1.6.0)** | `src/pages/groups/` (`#/grupos/SID`: aplicativos liberados e os que o grupo não vê, membros diretos, grupos relacionados) |
| Máquinas | **Pronta** | `src/pages/machines/` (KPIs que filtram, filtros de site, status, grupo do usuário, só online e só com a loja na URL, seleção em lote com Verificar programas, gaveta com abas Apps / Programas (Chocolatey e Windows) / Histórico e ações com confirmação) |
| Histórico | **Pronta** | `src/pages/history/` (filtros que aplicam sozinhos e ficam na URL, grupo do usuário, chips de filtros ligados, ordenação no servidor, paginação, exportação CSV, detalhe do pedido com código de saída e Reexecutar) |
| Ver como usuário | **Pronta** | `src/pages/preview/` (busca de pessoas com sugestões, grupos com ícones, cartões só de leitura com o estado de versão, apps ocultos com o motivo) |
| Visão geral | **Pronta** | `src/pages/overview/` (números que levam às listas filtradas, gráfico por dia com 7/14 dias, saúde das integrações, saúde do catálogo, adoção por site, últimos pedidos, atualização automática com confirmação) |

Cada tela é um arquivo à parte (`React.lazy` em `App.tsx`). Para uma tela nova: crie `src/pages/<tela>/`, exporte a página, registre-a em `routes.tsx` (menu e endereço) e em `pages` no `App.tsx`. Carregue dados com `useApi('/caminho')` (cancela ao sair e tem `reload()`) e altere com `api(...)` e `toast`; ações perigosas passam por `ConfirmDialog`.

Configurações usa dois endpoints novos do servidor: `POST /api/admin/client/releases/inspect` (confere o `.exe` e diz a versão e o efeito de publicar, sem gravar nada) e `GET /api/admin/settings/meta` (rótulo e faixa de cada parâmetro, para o formulário não duplicar a regra).

## Stack

Vite + React 19 + TypeScript, Tailwind CSS v4 e shadcn/ui (base Radix, preset Nova), `react-router-dom` com `HashRouter`, ícones Lucide. Fonte do sistema (Segoe UI), sem fonte embutida.

## Comandos

Precisa do Node 20.19+ ou 22.12+ **só no PC de desenvolvimento**. O servidor de produção (DC01) não tem Node: recebe os arquivos já prontos.

| Comando | O que faz |
|---|---|
| `..\..\deploy\Build-Admin.ps1` | Instala o que faltar (`npm ci`) e gera o painel em `Loja.Server\wwwroot\admin`. O `Publicar.ps1` já chama este script. |
| `npm run build` | `tsc -b` (confere os tipos) e `vite build`. |
| `npm run build:watch` | Regera a cada alteração. Deixe rodando junto com o servidor. |
| `npm run typecheck` | Só a conferência de tipos. |
| `npm run check:contrast` | Confere o contraste (WCAG) dos pares de cor do tema claro e do escuro, lendo os tokens de `src/index.css`. Roda a cada mudança de cor. |

### Desenvolvimento

Não há servidor do Vite de propósito: o login automático do Windows (Negotiate) não passa direito por proxy. O fluxo é:

1. `npm run build:watch` neste diretório.
2. `dotnet run --project ..\Loja.Server` (ambiente Development: Tactical simulado, `AdminGroups = "*"`).
3. Abrir `http://localhost:5080/admin/`.

O servidor entrega os arquivos com `Cache-Control: no-cache`, então basta recarregar a página.

## Regras que não podem quebrar

- **Sem CDN e sem requisição externa.** Tudo (código, ícones, estilos) vai dentro do bundle. O painel funciona sem internet.
- **Toda chamada passa por `src/lib/api.ts`.** Ele manda o cabeçalho `X-Requested-With: loja-admin`, que o servidor exige em qualquer alteração de `/api/admin` (proteção contra CSRF). Não use `fetch` direto nas telas.
- **Dado externo é texto.** Nomes do Chocolatey, do AD, do Tactical e mensagens de erro são exibidos como texto do React. Nunca use `dangerouslySetInnerHTML`.
- **Tema** segue o sistema (`prefers-color-scheme`): `src/lib/theme.ts` liga a classe `.dark` no `<html>`. Cores em `src/index.css`, vindas do painel atual.
- **Texto da interface** em português do Brasil, formas neutras de gênero ("usuário com sessão aberta", não "logado") e plural correto (nada de "(s)").

## Estrutura

```
src/
  main.tsx, App.tsx          entrada, HashRouter e rotas
  routes.tsx                 lista das telas (menu e padrões de URL)
  index.css                  tokens do tema (claro e escuro) e Tailwind
  lib/api.ts                 cliente único da API
  lib/theme.ts               tema do sistema + escolha manual (só a galeria usa)
  lib/format.ts              plural() e formatBytes(): nada de "pedido(s)"
  lib/versions.ts            describeState(): como escrever o estado de versão (o servidor decide, a tela só escreve)
  lib/sort.ts                useSort(): ordenação de tabelas (texto em português, números naturais)
  lib/types.ts, lib/utils.ts
  hooks/use-admin-info.ts    usuário e versão do servidor
  hooks/use-group-search.ts  busca de grupos do AD (GroupSelect e GroupPicker)
  components/shell/          casca (menu lateral) e cabeçalho de página
  components/ui/             componentes (do shadcn e próprios, abaixo)
  pages/                     uma tela por rota (gallery.tsx = galeria)
scripts/check-contrast.mjs   verificador de contraste
```

## Padrão visual (Fase 3)

**Alturas.** Botão padrão, campo de texto, data, select e campo desabilitado têm **36 px**; botão pequeno (`size="sm"`, ações dentro de tabela) tem **32 px**. Botões de texto têm largura mínima, e ações irmãs em tabela podem ganhar `className="min-w-24"` para ficarem iguais.

**Variantes do `Button`.** `default` = primário (no máximo um por tela ou região) · `secondary` = secundário, com borda · `destructive` = confirma ação perigosa, em modal · `ghost` = discreto (ícones, "Limpar") · `link`. Use `loading` em ações demoradas (desabilita e mostra andamento). `outline` é igual a `secondary` e só existe porque componentes do shadcn usam esse nome.

**Cores.** Tokens em `src/index.css`, vindos do painel atual. Texto precisa de 4,5:1 e borda de campo, trilho do switch e anel de foco de 3:1 (`npm run check:contrast`). Por isso a borda de campo (`--input`) é mais escura que a de cartão (`--border`).

| Componente | Para que serve |
|---|---|
| `Badge` | Situação: `success`, `warning`, `destructive`, `info`, `muted`. O texto sempre diz o estado. |
| `StatusDot` | Ponto + texto na mesma linha (online, offline...). |
| `Chip` | Etiqueta curta, removível por teclado; `tone="warning"` marca valor com problema. |
| `GroupSelect` (`src/components/group-select.tsx`) | Seletor de grupos do AD ligado a `GET /api/admin/groups`: busca, "mostrar todos os grupos do domínio", aviso de grupo sem membros, "Todos os usuários" exclusivo e chip em âmbar para grupo que sumiu. Use sempre que a tela pedir grupos. |
| `GroupPicker` (`src/components/group-picker.tsx`) | Escolha de **um** grupo do AD (tela Grupos e filtros por grupo do usuário). Usa a mesma busca do `GroupSelect` (`useGroupSearch`). |
| `MultiCombobox` | Vários itens com busca e chips; `exclusiveValue="*"` faz "Todos" limpar os outros. Base do `GroupSelect`. |
| `FileDropzone` | Arrastar ou escolher o `.exe` (valida a extensão, mostra nome e tamanho). |
| `SortableHead` + `useSort` | Cabeçalho que ordena, com `aria-sort`. Defina os `accessors` fora do componente. |
| `PaginationBar` | Faixa, total e Anterior/Próxima. |
| `EmptyState`, `ErrorState`, `TableSkeleton` | Vazio, falha (com "Tentar de novo") e carregando. |
| `ConfirmDialog` | "Tem certeza?" com ação assíncrona: o botão mostra andamento e o modal só fecha ao terminar. |
| `StatCard` | Indicador grande; com `onClick` vira botão que liga um filtro da lista (`aria-pressed`). |
| `Dialog`, `Sheet` (drawer, 576 px; o de Máquinas usa 768 px e ocupa a tela toda no celular), `DropdownMenu`, `Tooltip`, `Select`, `Tabs`, `Table`, `Card`, `Alert` | Do shadcn, já ajustados ao padrão. `Alert` tem as variantes `info`, `success`, `warning` e `destructive`. |
| `toast` | `import { toast } from "sonner"`; o `<Toaster />` já está em `main.tsx`. |

Textos do shadcn que vinham em inglês ("Close") foram traduzidos para leitores de tela. Ao adicionar um componente novo, confira se não trouxe texto em inglês nem o estilo antigo de campo (`bg-input/30`).

## Adicionar um componente do shadcn

```powershell
npx shadcn@latest add dialog
```

O comando copia o código para `src/components/ui` (não vira dependência de execução). Confira o que ele instalar no `package.json` e rode `npm audit` antes de gerar o pacote.

## Notas

- O `shadcn init` trouxe o pacote `cn`, que é da própria organização shadcn-ui e substitui `clsx` + `tailwind-merge`. Por isso `src/lib/utils.ts` só reexporta `cn`.
- O `node_modules` (~6 mil arquivos, ~150 MB) fica nesta pasta. Se ela for sincronizada por algum serviço de nuvem, mova o projeto para fora dela e aponte o `Build-Admin.ps1` para lá.
- O build vai direto para `Loja.Server\wwwroot\admin` e é recriado a cada execução (`emptyOutDir`): qualquer outra coisa que estiver nessa pasta é apagada. Não edite nada ali.
