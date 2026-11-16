import { useState } from 'react'
import { Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { ErrorState } from '@/components/ui/error-state'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Textarea } from '@/components/ui/textarea'
import { useApi } from '@/hooks/use-api'
import { api, messageOf } from '@/lib/api'
import type { LocalGroup, LocalUser } from '@/lib/types'

/** Roda uma chamada do painel mostrando o resultado em aviso. Devolve se deu certo. */
async function run(action: () => Promise<unknown>, success: string): Promise<boolean> {
  try {
    await action()
    toast.success(success)
    return true
  } catch (error) {
    toast.error(messageOf(error))
    return false
  }
}

const userPath = (username: string) => `/identity/users/${encodeURIComponent(username)}`

/** Usuários locais: quem entra no painel com usuário e senha (o administrador de emergência é criado na configuração do servidor). */
export function LocalUsersCard() {
  const users = useApi<LocalUser[]>('/identity/users')
  const [form, setForm] = useState({ username: '', displayName: '', password: '', isAdmin: false })
  const [creating, setCreating] = useState(false)
  const [passwordFor, setPasswordFor] = useState<LocalUser | null>(null)
  const [newPassword, setNewPassword] = useState('')
  const [deleting, setDeleting] = useState<LocalUser | null>(null)

  async function create() {
    setCreating(true)
    const ok = await run(() => api('/identity/users', { method: 'POST', body: form }), `Usuário ${form.username} criado.`)
    setCreating(false)
    if (ok) {
      setForm({ username: '', displayName: '', password: '', isAdmin: false })
      users.reload()
    }
  }

  async function update(user: LocalUser, body: Record<string, unknown>, message: string) {
    if (await run(() => api(userPath(user.username), { method: 'PUT', body }), message)) users.reload()
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Usuários locais</CardTitle>
        <CardDescription>
          Entram no painel com usuário e senha (pela página /login). O administrador de emergência entra sempre, mesmo com o login local desligado, e não pode ser desligado nem apagado.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {users.error && !users.data ? (
          <ErrorState message={users.error} onRetry={users.reload} />
        ) : !users.data ? (
          <Skeleton className="h-24 w-full" />
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Usuário</TableHead>
                  <TableHead>Situação</TableHead>
                  <TableHead>Administrador</TableHead>
                  <TableHead>Ativo</TableHead>
                  <TableHead className="text-right">Ações</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {users.data.map((u) => (
                  <TableRow key={u.username}>
                    <TableCell>
                      <div className="font-medium">{u.username}</div>
                      {u.displayName ? <div className="text-xs text-muted-foreground">{u.displayName}</div> : null}
                    </TableCell>
                    <TableCell className="space-x-1">
                      {u.isEmergency ? <Badge variant="info">Emergência</Badge> : null}
                      {!u.enabled ? <Badge variant="muted">Desligado</Badge> : null}
                      {u.locked ? <Badge variant="warning">Bloqueado por tentativas</Badge> : null}
                    </TableCell>
                    <TableCell>
                      <Switch
                        size="sm"
                        checked={u.isAdmin}
                        disabled={u.isEmergency}
                        aria-label={`${u.username} é administrador`}
                        onCheckedChange={(next) => update(u, { isAdmin: next }, next ? `${u.username} agora é administrador.` : `${u.username} deixou de ser administrador.`)}
                      />
                    </TableCell>
                    <TableCell>
                      <Switch
                        size="sm"
                        checked={u.enabled}
                        disabled={u.isEmergency}
                        aria-label={`${u.username} está ativo`}
                        onCheckedChange={(next) => update(u, { enabled: next }, next ? `${u.username} ligado.` : `${u.username} desligado (a sessão cai na hora).`)}
                      />
                    </TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-1">
                        <Button variant="secondary" size="sm" onClick={() => { setPasswordFor(u); setNewPassword('') }}>
                          Trocar senha
                        </Button>
                        <Button variant="ghost" size="sm" disabled={u.isEmergency} aria-label={`Apagar ${u.username}`} onClick={() => setDeleting(u)}>
                          <Trash2 aria-hidden="true" className="size-4" />
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}

        <fieldset className="grid gap-3 border-0 p-0 md:grid-cols-2">
          <legend className="mb-2 p-0 font-semibold">Novo usuário</legend>
          <div className="grid gap-1.5">
            <Label htmlFor="nu-user">Usuário</Label>
            <Input id="nu-user" value={form.username} autoComplete="off" onChange={(e) => setForm({ ...form, username: e.target.value })} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="nu-name">Nome para exibir (opcional)</Label>
            <Input id="nu-name" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="nu-pass">Senha (mínimo 10 caracteres)</Label>
            <Input id="nu-pass" type="password" autoComplete="new-password" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
          </div>
          <div className="flex items-end gap-3">
            <div className="flex items-center gap-2 pb-2">
              <Switch id="nu-admin" checked={form.isAdmin} onCheckedChange={(next) => setForm({ ...form, isAdmin: next })} />
              <Label htmlFor="nu-admin">Administrador</Label>
            </div>
            <Button className="ml-auto" loading={creating} disabled={!form.username.trim() || form.password.length < 10} onClick={create}>
              Criar usuário
            </Button>
          </div>
        </fieldset>
      </CardContent>

      <Dialog open={passwordFor !== null} onOpenChange={(open) => (open ? undefined : setPasswordFor(null))}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Trocar a senha de {passwordFor?.username}</DialogTitle>
            <DialogDescription>Também desbloqueia a conta se ela estiver bloqueada por tentativas erradas.</DialogDescription>
          </DialogHeader>
          <div className="grid gap-1.5">
            <Label htmlFor="np-pass">Nova senha (mínimo 10 caracteres)</Label>
            <Input id="np-pass" type="password" autoComplete="new-password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
          </div>
          <DialogFooter>
            <Button variant="secondary" onClick={() => setPasswordFor(null)}>
              Cancelar
            </Button>
            <Button
              disabled={newPassword.length < 10}
              onClick={async () => {
                if (passwordFor && (await run(() => api(userPath(passwordFor.username), { method: 'PUT', body: { password: newPassword } }), 'Senha trocada.'))) {
                  setPasswordFor(null)
                  users.reload()
                }
              }}
            >
              Trocar senha
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => (open ? undefined : setDeleting(null))}
        title={`Apagar ${deleting?.username ?? ''}?`}
        description="O usuário deixa de entrar e a sessão dele cai na hora. Não dá para desfazer."
        confirmLabel="Apagar"
        destructive
        onConfirm={async () => {
          if (!deleting) return
          if (await run(() => api(userPath(deleting.username), { method: 'DELETE' }), `${deleting.username} apagado.`)) users.reload()
          else throw new Error('falhou')
        }}
      />
    </Card>
  )
}

/** Grupos locais: liberam aplicativos como os do AD. Os membros são logins (o usuário do Windows que o agente informa, ou um usuário local). */
export function LocalGroupsCard() {
  const groups = useApi<LocalGroup[]>('/identity/groups')
  const [draft, setDraft] = useState<Record<number, string>>({})
  const [form, setForm] = useState({ name: '', description: '' })
  const [creating, setCreating] = useState(false)
  const [deleting, setDeleting] = useState<LocalGroup | null>(null)

  const membersText = (g: LocalGroup) => draft[g.id] ?? g.members.join('\n')

  async function create() {
    setCreating(true)
    const ok = await run(() => api('/identity/groups', { method: 'POST', body: form }), `Grupo ${form.name} criado.`)
    setCreating(false)
    if (ok) {
      setForm({ name: '', description: '' })
      groups.reload()
    }
  }

  async function save(g: LocalGroup) {
    const members = membersText(g).split(/[\n,;]+/).map((m) => m.trim()).filter(Boolean)
    if (await run(() => api(`/identity/groups/${g.id}`, { method: 'PUT', body: { members } }), `Membros de ${g.name} salvos. Valem na hora.`)) {
      setDraft(({ [g.id]: _removed, ...rest }) => rest)
      groups.reload()
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-[15px]">Grupos locais</CardTitle>
        <CardDescription>
          Funcionam como os grupos do AD: aparecem no seletor de grupos de cada aplicativo. Os membros são logins, um por linha (por exemplo o usuário do Windows do funcionário). Valem na hora.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-5">
        {groups.error && !groups.data ? (
          <ErrorState message={groups.error} onRetry={groups.reload} />
        ) : !groups.data ? (
          <Skeleton className="h-24 w-full" />
        ) : groups.data.length === 0 ? (
          <p className="text-sm text-muted-foreground">Nenhum grupo local ainda.</p>
        ) : (
          groups.data.map((g) => (
            <div key={g.id} className="grid gap-2 rounded-lg border p-3">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-semibold">{g.name}</span>
                <Badge variant="muted">{g.sid}</Badge>
                {g.description ? <span className="text-xs text-muted-foreground">{g.description}</span> : null}
                <Button variant="ghost" size="sm" className="ml-auto" aria-label={`Apagar o grupo ${g.name}`} onClick={() => setDeleting(g)}>
                  <Trash2 aria-hidden="true" className="size-4" />
                </Button>
              </div>
              <Label htmlFor={`gm-${g.id}`} className="sr-only">
                Membros de {g.name}
              </Label>
              <Textarea id={`gm-${g.id}`} rows={4} value={membersText(g)} onChange={(e) => setDraft({ ...draft, [g.id]: e.target.value })} placeholder="um login por linha" />
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs text-muted-foreground">{g.members.length} membro(s) salvo(s)</span>
                <Button size="sm" disabled={draft[g.id] === undefined} onClick={() => save(g)}>
                  Salvar membros
                </Button>
              </div>
            </div>
          ))
        )}

        <fieldset className="grid gap-3 border-0 p-0 md:grid-cols-[1fr_1fr_auto] md:items-end">
          <legend className="mb-2 p-0 font-semibold">Novo grupo</legend>
          <div className="grid gap-1.5">
            <Label htmlFor="ng-name">Nome</Label>
            <Input id="ng-name" value={form.name} autoComplete="off" onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="ng-desc">Descrição (opcional)</Label>
            <Input id="ng-desc" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          <Button loading={creating} disabled={!form.name.trim()} onClick={create}>
            Criar grupo
          </Button>
        </fieldset>
      </CardContent>

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => (open ? undefined : setDeleting(null))}
        title={`Apagar o grupo ${deleting?.name ?? ''}?`}
        description="Os aplicativos liberados só para este grupo deixam de aparecer para os membros. Não dá para desfazer."
        confirmLabel="Apagar"
        destructive
        onConfirm={async () => {
          if (!deleting) return
          if (await run(() => api(`/identity/groups/${deleting.id}`, { method: 'DELETE' }), `Grupo ${deleting.name} apagado.`)) groups.reload()
          else throw new Error('falhou')
        }}
      />
    </Card>
  )
}
