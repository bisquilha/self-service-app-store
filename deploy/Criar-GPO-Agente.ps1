<#
    Loja de Aplicativos - cria a GPO "Loja de Aplicativos - Agente" (implantacao do agente nas estacoes).

    Rodar NUM CONTROLADOR DE DOMINIO (qualquer DC), como administrador do dominio, em PowerShell.
    Feita no MESMO molde da GPO "Loja de Aplicativos - Distribuicao": configuracao de usuario desligada, filtro WMI
    "Somente estacoes Windows" (servidores ficam de fora), um script de inicializacao do computador no SYSVOL.

    SEM -Criar o script so CONFERE tudo e mostra o que faria (nada e criado).
    COM  -Criar cria a GPO, copia o Instalar-Agente.ps1 para o SYSVOL, ajusta a versao e VINCULA na OU das estacoes.

    Antes de criar ele confere: o LojaAgent.exe e o Instalar-Agente.ps1 da NAS tem o SHA-256 esperado; o filtro WMI existe;
    a OU existe; ja nao ha GPO com esse nome. Se algo nao bater, para sem criar nada.

    Voltar atras: Remove-GPLink -Name "Loja de Aplicativos - Agente" -Target "OU=Estacoes,DC=corp,DC=example,DC=com"
    (desvincula; quem ja instalou continua com o agente; para tirar o agente de uma maquina: LojaAgent.exe uninstall).

    Arquivo sem acentos de proposito (o PowerShell 5.1 le script sem BOM como ANSI).
#>
param(
    [switch]$Criar,
    [string]$Nome = 'Loja de Aplicativos - Agente',
    [string]$OU = 'OU=Estacoes,DC=corp,DC=example,DC=com',
    [string]$Origem = '\\fileserver\share\loja-deploy\agente',
    [string]$ScriptLocal = '',      # Instalar-Agente.ps1 ao lado deste arquivo (padrao); se nao houver, usa o da NAS
    # SHA-256 do seu build (Publicar-Agente.ps1 mostra os dois)
    [string]$HashExe = '723CFAD391D77DA057ED2ADE218C4D32BB2AB96B3B5983FD9FA4A946BB111837',
    [string]$HashScript = 'CA38242F789E7B9E1F412D4A5765CEC17BF5563572F9A35E3AC927C4727E8023'
)

$ErrorActionPreference = 'Stop'

# Valores copiados da GPO "Loja de Aplicativos - Distribuicao". Troque o filtro WMI pelo do seu dominio:
# (Get-ADObject -SearchBase "CN=SOM,CN=WMIPolicy,CN=System,<seu DN>" -Filter * -Properties msWMI-Name)
$FiltroWmi = '[corp.example.com;{00000000-0000-0000-0000-000000000000};0]'
$ExtensaoScripts = '[{42B5FAAE-6536-11D2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]'
$NomeScript = 'Instalar-Agente.ps1'

# Monta os arquivos da GPO no SYSVOL: pasta de inicializacao, o script, o psscripts.ini e a versao no GPT.INI.
function Escrever-ArquivosGpo([string]$pastaGpo, [string]$scriptOrigem, [int]$versao) {
    $startup = Join-Path $pastaGpo 'Machine\Scripts\Startup'
    New-Item -ItemType Directory -Path $startup -Force | Out-Null
    Copy-Item -LiteralPath $scriptOrigem -Destination (Join-Path $startup $NomeScript) -Force

    # psscripts.ini: mesmo formato da GPO da loja (UTF-16 com marca de ordem, que o Windows grava).
    $ini = "`r`n[Startup]`r`n0CmdLine=$NomeScript`r`n0Parameters=`r`n`r`n"
    [IO.File]::WriteAllText((Join-Path $pastaGpo 'Machine\Scripts\psscripts.ini'), $ini, [Text.Encoding]::Unicode)

    # GPT.INI: Version = parte do computador (16 bits de baixo) = $versao. Preserva as demais linhas.
    $gpt = Join-Path $pastaGpo 'GPT.INI'
    $linhas = @()
    if (Test-Path -LiteralPath $gpt) { $linhas = @(Get-Content -LiteralPath $gpt) }
    if (-not ($linhas | Where-Object { $_ -match '^\[General\]' })) { $linhas = @('[General]') + $linhas }
    $achou = $false
    $linhas = @($linhas | ForEach-Object { if ($_ -match '^Version=') { $achou = $true; "Version=$versao" } else { $_ } })
    if (-not $achou) { $linhas = @($linhas[0], "Version=$versao") + @($linhas | Select-Object -Skip 1) }
    Set-Content -LiteralPath $gpt -Value $linhas -Encoding Default
}

# Testes locais carregam so a funcao acima.
if ($env:GPO_AGENTE_SO_FUNCOES) { return }

Import-Module GroupPolicy
Import-Module ActiveDirectory
$problemas = @()

Write-Host "== Conferindo (nada e criado ainda) ==" -ForegroundColor Cyan

# 1. o script que vai para o SYSVOL: o que esta ao lado deste arquivo (ou o da NAS) tem de ter o hash aprovado
$pastaAqui = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not $ScriptLocal) { $ScriptLocal = Join-Path $pastaAqui $NomeScript }
$fonteScript = $null
foreach ($cand in @($ScriptLocal, (Join-Path $Origem $NomeScript))) {
    try {
        if (-not (Test-Path -LiteralPath $cand -ErrorAction Stop)) { continue }
        $h = (Get-FileHash -LiteralPath $cand -Algorithm SHA256).Hash
        if ($h -eq $HashScript) { $fonteScript = $cand; Write-Host "OK   $NomeScript com o SHA-256 aprovado: $cand"; break }
        else { $problemas += "$cand tem SHA-256 diferente do aprovado ($h)" }
    } catch { Write-Host "AVISO nao consegui ler $cand ($($_.Exception.Message))" -ForegroundColor Yellow }
}
if (-not $fonteScript -and $problemas.Count -eq 0) { $problemas += "Nao achei o $NomeScript aprovado (coloque-o ao lado deste arquivo)" }

# 1b. o .exe na NAS: conferencia extra. Se esta conta nao le a NAS daqui, so avisa (cada estacao confere o hash do .exe sozinha).
$exeNas = Join-Path $Origem 'LojaAgent.exe'
try {
    if (-not (Test-Path -LiteralPath $exeNas -ErrorAction Stop)) { $problemas += "Nao achei $exeNas" }
    else {
        $h = (Get-FileHash -LiteralPath $exeNas -Algorithm SHA256).Hash
        if ($h -eq $HashExe) { Write-Host "OK   LojaAgent.exe na NAS tem o SHA-256 esperado" } else { $problemas += "LojaAgent.exe na NAS tem SHA-256 diferente ($h)" }
    }
} catch { Write-Host "AVISO esta conta nao le a NAS daqui ($($_.Exception.Message)). Sigo sem essa conferencia: cada estacao confere o hash do .exe ao instalar." -ForegroundColor Yellow }
# 2. filtro WMI, OU e nome da GPO
$guidWmi = [regex]::Match($FiltroWmi, '\{[0-9A-Fa-f-]{36}\}').Value
$wmiDn = "CN=$guidWmi,CN=SOM,CN=WMIPolicy,CN=System,DC=corp,DC=example,DC=com"
try { $w = Get-ADObject -Identity $wmiDn -Properties 'msWMI-Name'; Write-Host "OK   filtro WMI existe: $($w.'msWMI-Name')" } catch { $problemas += "Filtro WMI $guidWmi nao encontrado" }
try { $null = Get-ADOrganizationalUnit -Identity $OU; Write-Host "OK   OU existe: $OU" } catch { $problemas += "OU nao encontrada: $OU" }
if (Get-GPO -Name $Nome -ErrorAction SilentlyContinue) { $problemas += "Ja existe uma GPO chamada '$Nome'" } else { Write-Host "OK   nao existe GPO com o nome '$Nome'" }
$qtd = @(Get-ADComputer -SearchBase $OU -Filter *).Count
Write-Host "     computadores na OU (com sub-OUs): $qtd"

if ($problemas.Count -gt 0) {
    Write-Host ''
    $problemas | ForEach-Object { Write-Host "PROBLEMA: $_" -ForegroundColor Red }
    Write-Host 'Nada foi criado.' -ForegroundColor Red
    return
}

if (-not $Criar) {
    Write-Host ''
    Write-Host "SIMULACAO: tudo certo. Com -Criar eu: criaria a GPO '$Nome', copiaria $NomeScript para o SYSVOL, aplicaria o filtro WMI e vincularia em $OU." -ForegroundColor Yellow
    return
}

Write-Host ''
Write-Host "== Criando ==" -ForegroundColor Cyan
$gpo = New-GPO -Name $Nome -Comment 'Implanta o LojaAgent nas estacoes (script de inicializacao).'
$gpo.GpoStatus = [Microsoft.GroupPolicy.GpoStatus]::UserSettingsDisabled
$id = $gpo.Id.ToString().ToUpper()
Write-Host "GPO criada: $Nome  {$id}"

try {
    $pasta = "\\$($env:USERDNSDOMAIN)\SYSVOL\$($env:USERDNSDOMAIN)\Policies\{$id}"
    Escrever-ArquivosGpo -pastaGpo $pasta -scriptOrigem $fonteScript -versao 1

    # confere que o script copiado e o mesmo aprovado
    $copiado = Join-Path $pasta "Machine\Scripts\Startup\$NomeScript"
    if ((Get-FileHash -LiteralPath $copiado -Algorithm SHA256).Hash -ne $HashScript) { throw 'O script copiado para o SYSVOL nao confere com o hash esperado.' }

    Set-ADObject -Identity "CN={$id},CN=Policies,CN=System,DC=corp,DC=example,DC=com" -Replace @{
        gPCWQLFilter             = $FiltroWmi
        gPCMachineExtensionNames = $ExtensaoScripts
        versionNumber            = 1
    }
    New-GPLink -Guid $gpo.Id -Target $OU -LinkEnabled Yes | Out-Null
}
catch {
    Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "A GPO foi criada mas NAO foi vinculada. Para desfazer: Remove-GPO -Name '$Nome'" -ForegroundColor Red
    return
}

Write-Host ''
Write-Host "== Conferindo o resultado ==" -ForegroundColor Cyan
Get-ChildItem $pasta -Recurse -File | ForEach-Object { '  ' + $_.FullName.Substring($pasta.Length) }
Get-Content (Join-Path $pasta 'Machine\Scripts\psscripts.ini') -Raw
Get-Content (Join-Path $pasta 'GPT.INI')
Get-ADObject "CN={$id},CN=Policies,CN=System,DC=corp,DC=example,DC=com" -Properties versionNumber, gPCWQLFilter, gPCMachineExtensionNames | Format-List versionNumber, gPCWQLFilter, gPCMachineExtensionNames
(Get-GPInheritance $OU).GpoLinks | Format-Table Order, DisplayName, Enabled
Write-Host "PRONTO. As estacoes instalam o agente no proximo boot (ou: gpupdate /force e reiniciar). Log em C:\ProgramData\LojaAgent-instalacao.log" -ForegroundColor Green
