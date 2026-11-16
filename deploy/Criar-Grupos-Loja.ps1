<#
    Loja de Aplicativos - grupos do AD no padrao GG_ (rodar no DC01, como administrador do dominio).

    Cria 3 grupos na OU dos grupos DL_ (OU=Groups), escopo Global, igual ao GG_TI_C:
      GG_TODOS_LOJA  apps liberados para todos        -> contem o grupo "Domain Users"
      GG_TI_LOJA     ferramentas da TI                -> contem GG_TI_C e GG_ADMIN_LOJA
      GG_ADMIN_LOJA  administradores do painel da loja -> contem ana.souza

    Tres etapas, SEMPRE nesta ordem (cada uma pode rodar de novo sem problema):
      1) sem parametro          cria os grupos e coloca os membros. Nao mexe na loja.
      2) -ConfigurarPainel      coloca GG_ADMIN_LOJA na lista de administradores do painel (mantem LOJA-Admins por enquanto),
                                faz copia do appsettings.Production.json e recicla o pool "Loja".
         >>> depois da etapa 2, o Claude troca os grupos dos aplicativos no painel, e voce confere o acesso.
      3) -ExcluirAntigos        so depois de tudo conferido: tira LOJA-Admins da lista do painel, recicla o pool e apaga os grupos
                                LOJA-Todos e LOJA-Admins (guarda os membros num CSV; a Lixeira do AD esta ligada e permite restaurar).

    -Simular mostra o que faria, sem alterar nada.  Arquivo sem acentos de proposito (PowerShell 5.1).
#>
param(
    [switch]$ConfigurarPainel,
    [switch]$ExcluirAntigos,
    [switch]$Simular,
    [string]$Ou = 'OU=Groups,DC=corp,DC=example,DC=com',
    [string[]]$MembrosAdmin = @('ana.souza'),
    [string[]]$GruposTI = @('GG_TI_C'),
    [string]$Config = 'C:\inetpub\loja\appsettings.Production.json',
    [string]$Pool = 'Loja'
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory

$gTodos = 'GG_TODOS_LOJA'
$gTI    = 'GG_TI_LOJA'
$gAdmin = 'GG_ADMIN_LOJA'
$antigos = @('LOJA-Todos', 'LOJA-Admins')

function Passo($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Fazer($descricao, [scriptblock]$acao) {
    if ($Simular) { Write-Host "[simular] $descricao" -ForegroundColor Yellow } else { & $acao; Write-Host "ok: $descricao" -ForegroundColor Green }
}
function GrupoExiste($nome) { [bool](Get-ADGroup -Filter "Name -eq '$nome'" -ErrorAction SilentlyContinue) }
function ReciclarPool {
    Import-Module WebAdministration
    Fazer "reciclar o pool $Pool" { Restart-WebAppPool -Name $Pool }
}
function LerConfig {
    if (-not (Test-Path -LiteralPath $Config)) { throw "Nao achei $Config (rode este script no servidor da loja)." }
    Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
}
function GravarAdminGroups($lista) {
    $json = LerConfig
    if (-not $json.Loja) { throw 'O appsettings nao tem a secao Loja.' }
    $atual = @($json.Loja.AdminGroups)
    if (($atual -join '|') -eq ($lista -join '|')) { Write-Host "AdminGroups ja esta: $($lista -join ', ')"; return $false }
    Fazer ("AdminGroups: {0}  ->  {1}" -f ($atual -join ', '), ($lista -join ', ')) {
        $copia = "$Config.bak-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
        Copy-Item -LiteralPath $Config -Destination $copia
        Write-Host "copia: $copia"
        $json.Loja.AdminGroups = $lista
        $json | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Config -Encoding UTF8
    }
    return $true
}

# ---------------------------------------------------------------- etapa 3
if ($ExcluirAntigos) {
    Passo 'Etapa 3: excluir os grupos antigos'
    if (-not (GrupoExiste $gAdmin)) { throw "$gAdmin nao existe: rode a etapa 1 antes." }
    $json = LerConfig
    $lista = @($json.Loja.AdminGroups)
    if ($lista -notcontains $gAdmin) { throw "O painel ainda nao usa $gAdmin (AdminGroups: $($lista -join ', ')). Rode a etapa 2 e confira o acesso antes." }
    if ((Get-ADGroupMember $gAdmin).Count -lt 1) { throw "$gAdmin esta vazio: ninguem entraria no painel. Cancelado." }
    Write-Host 'ATENCAO: so continue se os aplicativos da loja JA foram trocados para os grupos novos e voce JA entrou no painel pelo grupo novo.' -ForegroundColor Yellow
    if (-not $Simular) {
        $resp = Read-Host 'Digite EXCLUIR para apagar LOJA-Todos e LOJA-Admins'
        if ($resp -cne 'EXCLUIR') { Write-Host 'Cancelado.'; return }
    }
    $log = Join-Path $env:ProgramData ('LojaGruposAntigos-{0:yyyyMMdd-HHmmss}.csv' -f (Get-Date))
    $linhas = foreach ($n in $antigos) {
        $g = Get-ADGroup -Filter "Name -eq '$n'" -ErrorAction SilentlyContinue
        if ($g) { foreach ($m in (Get-ADGroupMember $g)) { [pscustomobject]@{ Grupo = $n; Membro = $m.SamAccountName; Tipo = $m.objectClass } } }
    }
    if (-not $Simular) { $linhas | Export-Csv -Path $log -NoTypeInformation -Encoding UTF8; Write-Host "membros guardados em: $log" }
    $novaLista = @($lista | Where-Object { $antigos -notcontains $_ })
    if (GravarAdminGroups $novaLista) { ReciclarPool }
    foreach ($n in $antigos) {
        $g = Get-ADGroup -Filter "Name -eq '$n'" -ErrorAction SilentlyContinue
        if ($g) { Fazer "apagar o grupo $n" { Remove-ADGroup -Identity $g -Confirm:$false } } else { Write-Host "$n ja nao existe" }
    }
    Write-Host ''; Write-Host 'Pronto. Para desfazer: Lixeira do AD (Get-ADObject -IncludeDeletedObjects ... | Restore-ADObject).'
    return
}

# ---------------------------------------------------------------- etapa 2
if ($ConfigurarPainel) {
    Passo 'Etapa 2: acesso ao painel'
    if (-not (GrupoExiste $gAdmin)) { throw "$gAdmin nao existe: rode a etapa 1 antes." }
    if ((Get-ADGroupMember $gAdmin).Count -lt 1) { throw "$gAdmin esta vazio. Cancelado." }
    $json = LerConfig
    $lista = @($json.Loja.AdminGroups)
    $nova = @($gAdmin) + @($lista | Where-Object { $_ -ne $gAdmin })
    if (GravarAdminGroups $nova) { ReciclarPool }
    Write-Host ''; Write-Host 'Proximo passo: avise o Claude para trocar os grupos dos aplicativos no painel; depois confira o acesso e rode -ExcluirAntigos.'
    return
}

# ---------------------------------------------------------------- etapa 1
Passo 'Etapa 1: criar os grupos'
if (-not (Get-ADOrganizationalUnit -Filter "DistinguishedName -eq '$Ou'" -ErrorAction SilentlyContinue)) { throw "A OU nao existe: $Ou" }
$definicao = @(
    @{ Nome = $gTodos; Desc = 'Loja de Aplicativos - apps liberados para todos'; Membros = @('Domain Users') },
    @{ Nome = $gTI;    Desc = 'Loja de Aplicativos - ferramentas da TI';          Membros = @($GruposTI) + @($gAdmin) },
    @{ Nome = $gAdmin; Desc = 'Loja de Aplicativos - administradores do painel';  Membros = @($MembrosAdmin) }
)
foreach ($d in $definicao) {
    if (GrupoExiste $d.Nome) { Write-Host "$($d.Nome): ja existe" }
    else {
        Fazer "criar o grupo $($d.Nome) em $Ou" {
            New-ADGroup -Name $d.Nome -SamAccountName $d.Nome -GroupScope Global -GroupCategory Security -Path $Ou -Description $d.Desc
            Get-ADGroup $d.Nome | Set-ADObject -ProtectedFromAccidentalDeletion $true
        }
    }
}
foreach ($d in $definicao) {
    foreach ($m in $d.Membros) {
        $obj = Get-ADObject -Filter "SamAccountName -eq '$m' -or Name -eq '$m'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $obj) { Write-Host "AVISO: '$m' nao existe no AD; nao foi adicionado a $($d.Nome)." -ForegroundColor Yellow; continue }
        if (-not $Simular -and (GrupoExiste $d.Nome)) {
            $ja = Get-ADGroupMember $d.Nome | Where-Object { $_.distinguishedName -eq $obj.DistinguishedName }
            if ($ja) { Write-Host "$m ja esta em $($d.Nome)"; continue }
        }
        Fazer "colocar '$m' em $($d.Nome)" { Add-ADGroupMember -Identity $d.Nome -Members $obj.DistinguishedName }
    }
}
Write-Host ''; Write-Host 'Resumo (membros diretos):'
foreach ($d in $definicao) {
    if (GrupoExiste $d.Nome) { '{0,-20} {1}' -f $d.Nome, ((Get-ADGroupMember $d.Nome | ForEach-Object { $_.Name }) -join ', ') }
}
Write-Host ''; Write-Host 'Proximo passo: rode de novo com -ConfigurarPainel.'
