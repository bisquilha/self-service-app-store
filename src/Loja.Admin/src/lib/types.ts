/** Resposta de GET /api/admin/info. */
export type AdminInfo = {
  user: string | null
  serverVersion: string | null
  adminGroups: string[]
  tacticalUrl: string
  tacticalScriptId: number
  internalFeed: string
  requireLoggedUser: boolean
  blockServers: boolean
}

/** Grupo do AD ligado a um aplicativo. O SID identifica o grupo (sobrevive a renomear); o nome é para exibir. "Todos os usuários" = nome "*", sem SID. */
export type GroupRef = { name: string; sid: string | null }

/** Aplicativo da loja (GET /api/admin/apps). `missingGroups`: grupos salvos que não existem mais no AD. */
export type AdminApp = {
  id: number
  packageId: string
  source: 'community' | 'internal'
  title: string
  description: string | null
  version: string | null
  enabled: boolean
  autoUpdate: boolean
  allowUninstall: boolean
  groups: string[]
  groupRefs: GroupRef[]
  missingGroups: string[] | null
  /** Nomes que o programa tem em "Programas e Recursos" do Windows (cadastro da TI). */
  aliases: string[] | null
}

/** Quantas máquinas têm o aplicativo e quantas estão desatualizadas (GET /api/admin/apps/stats). */
export type AppStat = { appId: number; installed: number; outdated: number }

/** Grupo do AD para o seletor (GET /api/admin/groups). `memberCount` é null em grupos primários, como Domain Users. */
export type AdGroup = { name: string; description: string | null; sid: string; memberCount: number | null }

/** Como estavam os grupos de um aplicativo antes de uma alteração em lote (serve ao "Desfazer"). */
export type GroupsBefore = { appId: number; groups: GroupRef[] }

/** Resposta de POST /api/admin/apps/bulk-groups e de /apps/restore-groups. `unchanged`: títulos que já estavam como pedido. */
export type BulkGroupsResult = { updated: AdminApp[]; unchanged: string[]; before: GroupsBefore[]; batch: string }

/** Pessoa que é membro direto de um grupo (GET /api/admin/groups/{sid}). */
export type GroupMember = { login: string; displayName: string | null; department: string | null }
export type GroupLink = { name: string; sid: string }

/**
 * Como o aplicativo chega ao grupo: `group` (o grupo está nos grupos do aplicativo), `nested` (o grupo está dentro de outro
 * que o aplicativo usa; `via` diz qual) ou `everyone` (aplicativo liberado para todos os usuários).
 */
export type GroupAppOrigin = 'group' | 'nested' | 'everyone' | 'none'
export type GroupApp = { id: number; title: string; packageId: string; enabled: boolean; origin: GroupAppOrigin; via: string | null }

/** GET /api/admin/groups/{sid}. `membersListed` é falso nos grupos primários (Domain Users...), que o AD não lista. */
export type GroupDetails = {
  group: AdGroup
  members: GroupMember[]
  membersTotal: number
  membersListed: boolean
  nestedGroups: GroupLink[]
  parentGroups: GroupLink[]
  apps: GroupApp[]
  withoutAccess: GroupApp[]
}

/** Resultado da busca de pacotes para adicionar à loja (GET /api/admin/search). */
export type PackageResult = {
  packageId: string
  source: 'community' | 'internal'
  title: string
  version: string | null
  description: string | null
  downloadCount: number
  isApproved: boolean
  inStore: boolean
}

/** Retorno de "atualizar em todas as máquinas" (POST /api/admin/apps/{id}/upgrade-all). */
export type UpgradeAllResult = { started: number; skipped: number; messages: string[] }

/** Versão do app da loja publicada no servidor (GET /api/admin/client/releases). */
export type ClientRelease = {
  version: string
  sha256: string
  size: number
  mandatory: boolean
  isCurrent: boolean
  publishedUtc: string
  publishedBy: string
  /** Quantas máquinas abriram a loja nesta versão. */
  machines: number
}

/** O que o servidor diz de um .exe antes de publicar (POST /api/admin/client/releases/inspect). Nada é gravado. */
export type ClientInspect = {
  version: string
  sha256: string
  size: number
  alreadyPublished: boolean
  isCurrent: boolean
  currentVersion: string | null
  olderThanCurrent: boolean
}

/** Parâmetros que o painel altera sem reiniciar o servidor (GET e PUT /api/admin/settings). */
export type Settings = {
  autoUpdateStartHour: number
  autoUpdateEndHour: number
  autoUpdateMaxPerCycle: number
  inventoryMaxAgeMinutes: number
  requestTimeoutMinutes: number
  metadataRefreshHours: number
}
export type SettingKey = keyof Settings

/** Rótulo e faixa aceita de cada parâmetro (GET /api/admin/settings/meta). A regra é do servidor. */
export type SettingField = { key: SettingKey; label: string; min: number; max: number }

/** Uma configuração do motor (GET /api/admin/config). O formulário é desenhado só a partir disto: tipo, faixa, ajuda e dependência vêm do servidor. */
export type ConfigSetting = {
  key: string
  label: string
  help: string
  type: 'bool' | 'int' | 'text' | 'url'
  value: boolean | number | string
  default: boolean | number | string
  isDefault: boolean
  min: number | null
  max: number | null
  appliesToAgents: boolean
  /** Chave de um sim/não do qual esta depende: desligado, este campo fica desabilitado. */
  dependsOn: string | null
}
export type ConfigGroup = { id: string; label: string; help: string; settings: ConfigSetting[] }
export type ConfigResponse = { version: number; groups: ConfigGroup[] }

/** Usuário local (entrada por senha). GET /api/admin/identity/users. */
export type LocalUser = {
  username: string
  displayName: string | null
  isAdmin: boolean
  isEmergency: boolean
  enabled: boolean
  locked: boolean
  createdUtc: string
}

/** Grupo local: o `sid` (LOCAL-n) ocupa o lugar do SID do AD; os membros são logins. GET /api/admin/identity/groups. */
export type LocalGroup = { id: number; name: string; description: string | null; sid: string; members: string[] }

/** Um pedido de instalar, atualizar, desinstalar ou verificar (RequestDto do servidor). */
export type RequestRow = {
  id: number
  appId: number | null
  packageId: string
  title: string
  hostname: string
  user: string
  action: string
  status: string
  message: string | null
  createdUtc: string
  finishedUtc: string | null
  output: string | null
  /** Código de saída do Chocolatey (0 = deu certo). Vazio enquanto o pedido não terminou. */
  exitCode: number | null
  /** Número do pedido que esta execução repete (botão "Reexecutar"). Vazio nos demais. */
  retryOf: number | null
}

/** Estação do Tactical com o que a loja sabe dela (GET /api/admin/machines). */
export type Machine = {
  hostname: string
  client: string | null
  site: string | null
  status: string
  loggedUser: string | null
  operatingSystem: string | null
  blockReason: string | null
  inventoryUtc: string | null
  storeAppsInstalled: number
  outdated: number
  lastRequestUtc: string | null
  agentId: string
  sameNameCount: number
  preferred: boolean
  appVersion: string | null
  appSeenUtc: string | null
  /** Último contato do agente com o Tactical (distingue agentes de mesmo nome). */
  lastSeenUtc: string | null
}

/**
 * Estado do aplicativo numa máquina, decidido pelo servidor: `update` (instalada menor), `current` (iguais), `newer` (instalada maior:
 * nunca se oferece downgrade), `unknown` (não dá para comparar: não se afirma "em dia"), `external` (não veio pela loja, mas um
 * programa com o nome cadastrado foi achado no Windows) ou `none`.
 */
export type VersionState = 'none' | 'current' | 'update' | 'newer' | 'unknown' | 'external'

export type StoreAppState = {
  appId: number
  title: string
  packageId: string
  enabled: boolean
  latestVersion: string | null
  installedVersion: string | null
  updateAvailable: boolean
  versionState: VersionState | null
  externalVersion: string | null
}

export type PackageRow = { packageId: string; version: string; inStore: boolean }

/** Programa do Windows da máquina; `storeApp` é o aplicativo da loja que tem este nome cadastrado. */
export type ProgramRow = { name: string; version: string; storeApp: string | null }

/** GET /api/admin/machines/{hostname}. `programs` é nulo enquanto o script do Tactical (1.6.0) não enviou os programas do Windows. */
export type MachineDetail = {
  machine: Machine
  storeApps: StoreAppState[]
  packages: PackageRow[]
  requests: RequestRow[]
  programs: ProgramRow[] | null
  programsUtc: string | null
}

/** Pessoa do AD (GET /api/admin/users?q=). */
export type AdUser = { login: string; displayName: string | null; email: string | null; department: string | null; title: string | null }

/** Aplicativo como aparece na loja de uma pessoa (CatalogItemDto do servidor). */
export type CatalogItem = {
  id: number
  packageId: string
  source: string
  title: string
  description: string | null
  version: string | null
  hasIcon: boolean
  installedVersion: string | null
  updateAvailable: boolean
  allowUninstall: boolean
  versionState: VersionState | null
  /** Já está no computador sem ter vindo pela loja (nome cadastrado pela TI). Não libera atualizar nem desinstalar. */
  outsideStore: boolean
  externalVersion: string | null
  externalName: string | null
}

export type PreviewGroup = { group: string; member: boolean }
export type PreviewHidden = { appId: number; title: string; packageId: string; groups: string[]; disabled: boolean }

/** GET /api/admin/preview?user=&host=: a loja exatamente como aquela pessoa vê. */
export type Preview = {
  user: string
  isAdmin: boolean
  storeGroups: PreviewGroup[]
  loggedOn: string[]
  hostname: string | null
  inventoryUtc: string | null
  visible: CatalogItem[]
  hidden: PreviewHidden[]
  /** Quando os programas do Windows foram coletados (nulo = ainda não). */
  programsUtc: string | null
}

/** Pedidos de um dia (GET /api/admin/overview → perDay). `day` vem como "21/10". */
export type DayStat = { day: string; success: number; failed: number; denied: number }

/** GET /api/admin/overview. */
export type Overview = {
  apps: number
  activeApps: number
  machines: number
  machinesWithStore: number
  requestsToday: number
  requests7d: number
  failures7d: number
  pendingUpdates: number
  perDay: DayStat[]
  recent: RequestRow[]
}

export type StatusCheck = { name: string; ok: boolean; detail: string }

/** GET /api/admin/status: confere cada dependência da loja na hora (por isso pode demorar). */
export type AdminStatus = { serverVersion: string; adminGroups: string[]; checks: StatusCheck[] }

/** Uma alteração da trilha de auditoria (GET /api/admin/audit). `undo`: a alteração foi um "Desfazer". */
export type AuditRow = {
  id: number
  utc: string
  user: string
  kind: 'create' | 'update' | 'delete' | 'undo' | 'run' | string
  entityType: 'app' | 'settings' | 'release' | 'action' | string
  entityId: string | null
  entityLabel: string | null
  field: string | null
  oldValue: string | null
  newValue: string | null
  batch: string | null
  undo: boolean
}

export type AuditPage = { total: number; page: number; size: number; items: AuditRow[] }

/** Nome de programa do Windows já coletado nas máquinas (GET /api/admin/programs?q=), com em quantas máquinas aparece. */
export type ProgramName = { name: string; machines: number }

/** Se a máquina está ao alcance de "Atualizar em todas": `ready` online e estação; os outros dizem por que fica de fora. */
export type UpdateReach = 'ready' | 'offline' | 'blocked' | 'duplicate' | 'missing' | 'unknown'
export type UpdateMachine = { hostname: string; installedVersion: string; reach: UpdateReach }

/** Aplicativo com máquinas em versão menor que a da loja (GET /api/admin/updates). */
export type UpdateApp = {
  appId: number
  title: string
  packageId: string
  enabled: boolean
  autoUpdate: boolean
  latestVersion: string
  machines: UpdateMachine[]
}

/** Um problema do catálogo (GET /api/admin/catalog-health). `group-unverified`: o AD não respondeu, o que NÃO prova que o grupo não existe. */
export type HealthIssue = {
  kind: 'no-group' | 'group-missing' | 'group-unverified' | 'group-empty' | 'outdated' | string
  severity: 'error' | 'warning'
  appId: number
  title: string
  packageId: string
  message: string
  groupSid: string | null
  groupName: string | null
}

export type CatalogHealth = {
  issues: HealthIssue[]
  appsChecked: number
  groupsChecked: number
  /** Falso quando alguma consulta ao AD falhou: parte dos grupos ficou sem conferir. */
  adAvailable: boolean
  adNote: string | null
  checkedUtc: string
  /** O critério usado para "desatualizado em muitas máquinas". */
  outdatedRule: string
}
