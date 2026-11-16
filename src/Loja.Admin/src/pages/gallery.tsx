import { useEffect, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ChevronDownIcon, DownloadIcon, PackageIcon, PlusIcon, RefreshCwIcon, Trash2Icon } from 'lucide-react'
import { PageHeader } from '@/components/shell/page-header'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Chip } from '@/components/ui/chip'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorState } from '@/components/ui/error-state'
import { FileDropzone } from '@/components/ui/file-dropzone'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { MultiCombobox, type ComboOption } from '@/components/ui/multi-combobox'
import { PaginationBar } from '@/components/ui/pagination-bar'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { SortableHead } from '@/components/ui/sortable-head'
import { StatusDot } from '@/components/ui/status-dot'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { TableSkeleton } from '@/components/ui/table-skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { plural } from '@/lib/format'
import { useSort } from '@/lib/sort'
import { setThemeChoice, useThemeChoice, type ThemeChoice } from '@/lib/theme'

// Tudo aqui é exemplo: nomes e números inventados, nada vem do servidor.
type Machine = { host: string; site: string; status: 'online' | 'overdue' | 'offline'; apps: number; pending: number; loja: string }
const machines: Machine[] = [
  { host: 'PC-HQ-521', site: 'Matriz', status: 'online', apps: 4, pending: 2, loja: '1.2.1' },
  { host: 'PC-HQ-9', site: 'Matriz', status: 'online', apps: 0, pending: 0, loja: '1.2.0' },
  { host: 'PC-BR1-229', site: 'Filial 1', status: 'overdue', apps: 1, pending: 1, loja: '1.2.0' },
  { host: 'PC-HQ-10', site: 'Matriz', status: 'offline', apps: 0, pending: 0, loja: '' },
  { host: 'PC-BR2-625', site: 'Filial 2', status: 'online', apps: 3, pending: 0, loja: '1.2.1' },
  { host: 'PC-BR3-152', site: 'Filial 3', status: 'online', apps: 2, pending: 1, loja: '1.2.1' },
  { host: 'PC-BR4-511', site: 'Filial 4', status: 'offline', apps: 0, pending: 0, loja: '' },
  { host: 'PC-HQ-467', site: 'Matriz', status: 'online', apps: 5, pending: 0, loja: '1.2.1' },
]
const accessors = {
  host: (m: Machine) => m.host,
  site: (m: Machine) => m.site,
  status: (m: Machine) => m.status,
  apps: (m: Machine) => m.apps,
  pending: (m: Machine) => m.pending,
  loja: (m: Machine) => m.loja,
}
const statusTone = { online: 'success', overdue: 'warning', offline: 'muted' } as const

const EVERYONE: ComboOption = { value: '*', label: 'Todos os usuários', description: 'Qualquer pessoa do domínio' }
const lojaGroups: ComboOption[] = [
  { value: 'LOJA-Admins', label: 'LOJA-Admins', description: 'Administradores da loja', hint: '2 membros' },
  { value: 'LOJA-Engenharia', label: 'LOJA-Engenharia', description: 'Equipe de engenharia', hint: '0 membros', hintTone: 'warning' },
  { value: 'LOJA-TI', label: 'LOJA-TI', description: 'Tecnologia da informação', hint: '5 membros' },
  { value: 'LOJA-Todos', label: 'LOJA-Todos', description: 'Todos os funcionários', hint: '1 membro' },
]
const otherGroups: ComboOption[] = [
  { value: 'Suporte', label: 'Suporte', description: 'Grupo fora do prefixo da loja', hint: '8 membros' },
  { value: 'Financeiro', label: 'Financeiro', description: 'Grupo fora do prefixo da loja', hint: '12 membros' },
]

function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">{title}</CardTitle>
        {description ? <CardDescription>{description}</CardDescription> : null}
      </CardHeader>
      <CardContent className="grid gap-5">{children}</CardContent>
    </Card>
  )
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="grid gap-2">
      <p className="text-xs font-semibold text-muted-foreground">{label}</p>
      <div className="flex flex-wrap items-center gap-2">{children}</div>
    </div>
  )
}

function Field({ label, htmlFor, children, hint }: { label: string; htmlFor?: string; children: ReactNode; hint?: string }) {
  return (
    <div className="grid content-start gap-1.5">
      <Label htmlFor={htmlFor}>{label}</Label>
      {children}
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
    </div>
  )
}

function ThemeSwitcher() {
  const choice = useThemeChoice()
  const options: { value: ThemeChoice; label: string }[] = [
    { value: 'system', label: 'Sistema' },
    { value: 'light', label: 'Claro' },
    { value: 'dark', label: 'Escuro' },
  ]
  return (
    <div role="group" aria-label="Tema" className="inline-flex rounded-lg border border-border bg-card p-0.5">
      {options.map((option) => (
        <Button
          key={option.value}
          size="sm"
          variant={choice === option.value ? 'default' : 'ghost'}
          aria-pressed={choice === option.value}
          className="min-w-0"
          onClick={() => setThemeChoice(option.value)}
        >
          {option.label}
        </Button>
      ))}
    </div>
  )
}

/** Grupos do AD, como será na tela de aplicativos: lista com busca (aqui simulada), chips e opção exclusiva "Todos". */
function GroupDemo() {
  const [value, setValue] = useState<string[]>(['LOJA-Todos', 'LOJA-Antigo'])
  const [query, setQuery] = useState('')
  const [showAll, setShowAll] = useState(false)
  const [loading, setLoading] = useState(false)
  const [options, setOptions] = useState<ComboOption[]>([EVERYONE, ...lojaGroups])

  useEffect(() => {
    setLoading(true)
    const timer = setTimeout(() => {
      const q = query.trim().toLowerCase()
      const pool = [EVERYONE, ...lojaGroups, ...(showAll ? otherGroups : [])]
      setOptions(pool.filter((o) => !q || o.label.toLowerCase().includes(q) || o.description?.toLowerCase().includes(q)))
      setLoading(false)
    }, 250)
    return () => clearTimeout(timer)
  }, [query, showAll])

  return (
    <div className="grid gap-1.5">
      <Label>Grupos do AD que podem instalar</Label>
      <MultiCombobox
        aria-label="Grupos do AD"
        addLabel="Adicionar grupo"
        searchPlaceholder="Buscar grupo…"
        emptyText="Nenhum grupo encontrado."
        options={options}
        value={value}
        onChange={setValue}
        exclusiveValue="*"
        loading={loading}
        onSearchChange={setQuery}
        warnValues={['LOJA-Antigo']}
        warnLabel="Grupo não encontrado no AD"
        labelFor={(v) => (v === '*' ? EVERYONE.label : undefined)}
        footer={
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={showAll} onCheckedChange={(checked) => setShowAll(checked === true)} />
            Mostrar todos os grupos do domínio
          </label>
        }
      />
      <p className="text-xs text-muted-foreground">Escolher "Todos os usuários" limpa os outros grupos. O âmbar marca um grupo que sumiu do AD.</p>
    </div>
  )
}

export function Gallery() {
  const [file, setFile] = useState<File | null>(null)
  const [page, setPage] = useState(1)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const { sorted, sort, toggle } = useSort(machines, accessors, { key: 'host', direction: 'asc' })
  const pageSize = 4
  const visible = sorted.slice((page - 1) * pageSize, page * pageSize)

  return (
    <div className="grid gap-4">
      <PageHeader title="Galeria de componentes" actions={<ThemeSwitcher />} />

      <Alert variant="info" role="note">
        <AlertTitle>Tela de conferência</AlertTitle>
        <AlertDescription>
          Rota oculta (<code className="font-mono text-xs">#/_galeria</code>), fora do menu. Serve para ver cada componente nos temas claro e escuro.
          Todos os dados são de exemplo.
        </AlertDescription>
      </Alert>

      <Section title="Botões" description="Altura 36 px (padrão) e 32 px (pequeno), a mesma dos campos. No máximo um botão primário por tela.">
        <Row label="Variantes, tamanho padrão">
          <Button>Primário</Button>
          <Button variant="secondary">Secundário</Button>
          <Button variant="destructive">Destrutivo</Button>
          <Button variant="ghost">Discreto</Button>
          <Button variant="link">Link</Button>
        </Row>
        <Row label="Variantes, tamanho pequeno (ações dentro de tabelas)">
          <Button size="sm">Primário</Button>
          <Button size="sm" variant="secondary">Secundário</Button>
          <Button size="sm" variant="destructive">Destrutivo</Button>
          <Button size="sm" variant="ghost">Discreto</Button>
        </Row>
        <Row label="Com ícone, só ícone, carregando e desabilitado">
          <Button><PlusIcon aria-hidden="true" />Adicionar aplicativo</Button>
          <Button variant="secondary"><DownloadIcon aria-hidden="true" />Exportar para Excel (CSV)</Button>
          <Button variant="secondary" size="icon" aria-label="Atualizar lista"><RefreshCwIcon aria-hidden="true" /></Button>
          <Button variant="ghost" size="icon-sm" aria-label="Remover"><Trash2Icon aria-hidden="true" /></Button>
          <Button loading>Publicando…</Button>
          <Button disabled>Desabilitado</Button>
        </Row>
        <Row label="Ações irmãs ficam com a mesma altura e largura mínima (como no detalhe da máquina)">
          <Button size="sm">Atualizar</Button>
          <Button size="sm" variant="secondary">Desinstalar</Button>
          <Button size="sm">Instalar</Button>
        </Row>
      </Section>

      <Section title="Campos" description="Borda de campo com contraste 3:1. Todo campo tem rótulo; switches ganham nome acessível.">
        <div className="grid gap-4 md:grid-cols-3">
          <Field label="Busca" htmlFor="g-busca"><Input id="g-busca" type="search" placeholder="Usuário, computador, app ou mensagem…" /></Field>
          <Field label="Situação">
            <Select defaultValue="todas">
              <SelectTrigger className="w-full" aria-label="Situação"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="todas">Todas as situações</SelectItem>
                <SelectItem value="success">Concluído</SelectItem>
                <SelectItem value="failed">Falhou</SelectItem>
                <SelectItem value="denied">Negado</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <Field label="Com erro" htmlFor="g-erro" hint="Mensagem de erro fica junto do campo e é lida por leitores de tela.">
            <Input id="g-erro" aria-invalid="true" aria-describedby="g-erro-msg" defaultValue="LOJA-Tdos" />
            <p id="g-erro-msg" role="alert" className="text-xs text-destructive">O grupo "LOJA-Tdos" não existe no AD.</p>
          </Field>
          <Field label="De" htmlFor="g-de"><Input id="g-de" type="date" defaultValue="2026-10-01" /></Field>
          <Field label="Até" htmlFor="g-ate"><Input id="g-ate" type="date" defaultValue="2026-10-21" /></Field>
          <Field label="Desabilitado" htmlFor="g-off"><Input id="g-off" disabled defaultValue="Somente leitura" /></Field>
        </div>
        <Field label="Observação" htmlFor="g-obs"><Textarea id="g-obs" rows={2} placeholder="Texto livre…" /></Field>
        <div className="flex flex-wrap items-center gap-x-8 gap-y-3">
          <label className="flex items-center gap-2"><Checkbox /> só online</label>
          <label className="flex items-center gap-2"><Checkbox defaultChecked /> obrigatória</label>
          <label className="flex items-center gap-2"><Switch aria-label="Ativo: 7-Zip" defaultChecked /> Ativo</label>
          <label className="flex items-center gap-2"><Switch aria-label="Auto-atualizar: 7-Zip" /> Auto-atualizar</label>
          <label className="flex items-center gap-2"><Switch aria-label="Desabilitado" disabled /> Desabilitado</label>
        </div>
        <Field label="Versão nova do app da loja">
          <FileDropzone label="Arquivo do app da loja" accept=".exe" file={file} onFileChange={setFile} hint="Só arquivos .exe. A TI confere o SHA-256 antes de publicar." />
        </Field>
        <GroupDemo />
      </Section>

      <Section title="Selos, pontos de situação e chips" description="A cor nunca vai sozinha: o texto sempre diz o estado.">
        <Row label="Selos">
          <Badge variant="success">Concluído</Badge>
          <Badge variant="warning">Negado</Badge>
          <Badge variant="destructive">Falhou</Badge>
          <Badge variant="info">Interno</Badge>
          <Badge variant="muted">registro antigo</Badge>
          <Badge variant="warning">{plural(1, 'desatualizada')}</Badge>
          <Badge variant="warning">{plural(3, 'desatualizada')}</Badge>
        </Row>
        <Row label="Ponto + texto na mesma linha">
          <StatusDot tone="success">online</StatusDot>
          <StatusDot tone="warning">overdue</StatusDot>
          <StatusDot tone="muted">offline</StatusDot>
          <StatusDot tone="destructive">sem retorno</StatusDot>
        </Row>
        <Row label="Chips">
          <Chip>LOJA-TI</Chip>
          <Chip>Todos os usuários</Chip>
          <Chip onRemove={() => toast.info('Chip removido')} removeLabel="Remover LOJA-Engenharia">LOJA-Engenharia</Chip>
          <Chip tone="warning">LOJA-Antigo</Chip>
        </Row>
      </Section>

      <Section title="Tabela" description="Colunas ordenáveis por clique, paginação e os estados de carregando, vazio e erro.">
        <Tabs defaultValue="dados">
          <TabsList>
            <TabsTrigger value="dados">Com dados</TabsTrigger>
            <TabsTrigger value="carregando">Carregando</TabsTrigger>
            <TabsTrigger value="vazio">Vazio</TabsTrigger>
            <TabsTrigger value="erro">Erro</TabsTrigger>
          </TabsList>
          <TabsContent value="dados" className="grid gap-3 pt-2">
            <div className="overflow-hidden rounded-lg border">
              <Table>
                <TableHeader>
                  <TableRow className="hover:bg-transparent">
                    <SortableHead label="Computador" sortKey="host" sort={sort} onSort={toggle} />
                    <SortableHead label="Site" sortKey="site" sort={sort} onSort={toggle} />
                    <SortableHead label="Situação" sortKey="status" sort={sort} onSort={toggle} />
                    <SortableHead label="Apps" sortKey="apps" sort={sort} onSort={toggle} />
                    <SortableHead label="Pendentes" sortKey="pending" sort={sort} onSort={toggle} />
                    <SortableHead label="Loja" sortKey="loja" sort={sort} onSort={toggle} />
                    <TableHead className="w-px"><span className="sr-only">Ações</span></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {visible.map((m) => (
                    <TableRow key={m.host}>
                      <TableCell className="font-semibold">{m.host}</TableCell>
                      <TableCell>{m.site}</TableCell>
                      <TableCell><StatusDot tone={statusTone[m.status]}>{m.status}</StatusDot></TableCell>
                      <TableCell>{m.apps}</TableCell>
                      <TableCell>{m.pending ? <Badge variant="warning">{plural(m.pending, 'pendente')}</Badge> : <span className="text-muted-foreground">0</span>}</TableCell>
                      <TableCell className="font-mono text-[12.5px]">{m.loja || '—'}</TableCell>
                      <TableCell className="text-right"><Button size="sm" variant="secondary">Detalhes</Button></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            <PaginationBar total={machines.length} page={page} pageSize={pageSize} onPageChange={setPage} singular="computador" pluralForm="computadores" />
          </TabsContent>
          <TabsContent value="carregando" className="pt-2">
            <div className="overflow-hidden rounded-lg border">
              <Table aria-busy="true">
                <TableHeader>
                  <TableRow className="hover:bg-transparent">
                    {['Computador', 'Site', 'Situação', 'Apps', 'Loja'].map((h) => <SortableHead key={h} label={h} sortKey={h} sort={null} onSort={() => undefined} />)}
                  </TableRow>
                </TableHeader>
                <TableSkeleton columns={5} />
              </Table>
            </div>
          </TabsContent>
          <TabsContent value="vazio" className="pt-2">
            <div className="rounded-lg border">
              <EmptyState
                icon={PackageIcon}
                title="Nenhum aplicativo na loja"
                description="Adicione o primeiro aplicativo buscando no Chocolatey ou na pasta interna do NAS."
                action={<Button><PlusIcon aria-hidden="true" />Adicionar aplicativo</Button>}
              />
            </div>
          </TabsContent>
          <TabsContent value="erro" className="pt-2">
            <div className="rounded-lg border">
              <ErrorState message="Não foi possível consultar o Tactical RMM agora. Tente de novo em instantes." onRetry={() => toast.info('Tentando de novo…')} />
            </div>
          </TabsContent>
        </Tabs>
      </Section>

      <Section title="Janelas, menus e avisos" description="Modal (formulário e confirmação), drawer, menu de ações, dica e toast. Esc fecha; o foco fica preso na janela.">
        <Row label="Sobreposições">
          <Dialog>
            <DialogTrigger asChild><Button variant="secondary">Abrir modal</Button></DialogTrigger>
            <DialogContent className="sm:max-w-md">
              <DialogHeader>
                <DialogTitle>Editar grupos do AD</DialogTitle>
                <DialogDescription>Quem está nestes grupos vê o aplicativo na loja.</DialogDescription>
              </DialogHeader>
              <Field label="Nome do aplicativo" htmlFor="g-modal"><Input id="g-modal" defaultValue="7-Zip" /></Field>
              <DialogFooter>
                <DialogClose asChild><Button variant="secondary">Cancelar</Button></DialogClose>
                <Button onClick={() => toast.success('Grupos salvos.')}>Salvar</Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          <Button variant="secondary" onClick={() => setConfirmOpen(true)}>Confirmar ação destrutiva</Button>
          <ConfirmDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            destructive
            title="Remover da loja"
            description='Remover "7-Zip" da loja? Quem já instalou continua com o programa; ele só some da loja.'
            confirmLabel="Remover"
            onConfirm={async () => {
              await new Promise((resolve) => setTimeout(resolve, 900))
              toast.success('"7-Zip" removido.')
            }}
          />

          <Sheet>
            <SheetTrigger asChild><Button variant="secondary">Abrir drawer</Button></SheetTrigger>
            <SheetContent className="overflow-y-auto">
              <SheetHeader>
                <SheetTitle>PC-HQ-521</SheetTitle>
                <SheetDescription>Matriz · Windows 11 Pro</SheetDescription>
              </SheetHeader>
              <div className="grid gap-4 px-4 pb-4">
                <div className="flex flex-wrap gap-6 text-sm">
                  <div><p className="text-xs text-muted-foreground">Situação</p><StatusDot tone="success">online</StatusDot></div>
                  <div><p className="text-xs text-muted-foreground">Usuário com sessão aberta</p><p>ana.souza</p></div>
                  <div><p className="text-xs text-muted-foreground">Versão da loja</p><p className="font-mono text-[12.5px]">1.2.1</p></div>
                </div>
                <div className="overflow-hidden rounded-lg border">
                  <Table>
                    <TableBody>
                      {[['7-Zip', '1.0', true], ['Notepad++', '9999.0', false]].map(([name, version, outdated]) => (
                        <TableRow key={String(name)}>
                          <TableCell className="font-semibold">{name}</TableCell>
                          <TableCell className="font-mono text-[12.5px]">{version}</TableCell>
                          <TableCell className="text-right">
                            <div className="flex justify-end gap-2">
                              {outdated ? <Button size="sm">Atualizar</Button> : <Badge variant="success">em dia</Badge>}
                              <Button size="sm" variant="secondary">Desinstalar</Button>
                            </div>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </div>
              </div>
            </SheetContent>
          </Sheet>

          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="secondary">Ações<ChevronDownIcon aria-hidden="true" /></Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              <DropdownMenuItem onSelect={() => toast.info('Dados atualizados.')}>Atualizar dados do Chocolatey</DropdownMenuItem>
              <DropdownMenuItem onSelect={() => toast.info('Abriria "Editar grupos".')}>Editar grupos do AD</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive" onSelect={() => setConfirmOpen(true)}>Remover da loja</DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>

          <Tooltip>
            <TooltipTrigger asChild>
              <span tabIndex={0} className="block max-w-44 truncate rounded-sm text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring">
                Adobe Acrobat Reader DC software is the free, trusted global standard
              </span>
            </TooltipTrigger>
            <TooltipContent>Adobe Acrobat Reader DC software is the free, trusted global standard for viewing, printing, signing, sharing and annotating PDFs.</TooltipContent>
          </Tooltip>
        </Row>
        <Row label="Toasts">
          <Button size="sm" variant="secondary" onClick={() => toast.success('7-Zip adicionado para LOJA-Todos.')}>Sucesso</Button>
          <Button size="sm" variant="secondary" onClick={() => toast.error('O grupo "LOJA-Tdos" não existe no AD.')}>Erro</Button>
          <Button size="sm" variant="secondary" onClick={() => toast.info('3 atualizações enviadas, 1 pulada.')}>Informação</Button>
          <Button size="sm" variant="secondary" onClick={() => toast.warning('Máquinas offline ficam para depois.')}>Atenção</Button>
        </Row>
        <Row label="Avisos na página">
          <div className="grid w-full gap-2">
            <Alert variant="info" role="note"><AlertDescription>Informação neutra.</AlertDescription></Alert>
            <Alert variant="success" role="status"><AlertDescription className="text-success">Versão 1.2.1 publicada. 1 máquina já usa.</AlertDescription></Alert>
            <Alert variant="warning" role="note"><AlertDescription className="text-warning">Há 2 aplicativos desatualizados em máquinas online.</AlertDescription></Alert>
            <Alert variant="destructive"><AlertTitle>Não foi possível salvar</AlertTitle><AlertDescription>O servidor recusou o pedido. Confira os grupos e tente de novo.</AlertDescription></Alert>
          </div>
        </Row>
      </Section>

      <Section title="Cores do tema" description="Os tokens de src/index.css. Troque o tema acima para conferir.">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
          {[
            ['background', 'bg-background'], ['card', 'bg-card'], ['muted', 'bg-muted'], ['accent', 'bg-accent'], ['primary', 'bg-primary'],
            ['success', 'bg-success'], ['warning', 'bg-warning'], ['destructive', 'bg-destructive'], ['sidebar', 'bg-sidebar'], ['switch-off', 'bg-switch-off'],
          ].map(([name, cls]) => (
            <div key={name} className="grid gap-1.5 text-xs">
              <div className={`h-10 rounded-md border ${cls}`} />
              <span className="font-mono">{name}</span>
            </div>
          ))}
        </div>
      </Section>
    </div>
  )
}
