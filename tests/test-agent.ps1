# Teste de ponta a ponta do agente da loja: servidor real (Development, Tactical simulado) + agente real + um "choco" falso.
# Precisa de internet (cadastro de apps consulta o Chocolatey, como o test-api.ps1). Nada e instalado de verdade.
# Uso:  .\test-agent.ps1            (compila antes, se precisar)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$raiz = Split-Path -Parent $here
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$pc = $env:COMPUTERNAME
$port = 5081
$Base = "http://localhost:$port"
$work = Join-Path $env:TEMP 'loja-agent-test'
$script:fails = 0

function Call($method, $path, $body = $null, [switch]$Anon) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    if (-not $Anon) { $p.UseDefaultCredentials = $true }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 5)); $p.ContentType = 'application/json; charset=utf-8' }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        if ($r.Headers['Content-Type'] -like 'application/json*') { $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json }
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $text = (New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd()
        $json = try { $text | ConvertFrom-Json } catch { $null }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $json }
    }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) { if (& $cond) { return $true }; Start-Sleep -Seconds 1 }
    return $false
}
function Wait-Request($id, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) {
        $row = (Call GET '/api/admin/requests?take=100').Body | Where-Object { $_.id -eq $id } | Select-Object -First 1
        if ($row -and $row.status -ne 'running') { return $row }
        Start-Sleep -Seconds 1
    }
    return $row
}
# --- preparo: compila e monta a pasta de teste
& $dotnet build "$raiz\src\Loja.Server\Loja.Server.csproj" -c Debug -nologo -v q | Out-Null
& $dotnet build "$raiz\src\Loja.Agent\Loja.Agent.csproj" -c Debug -nologo -v q | Out-Null
$serverDll = Join-Path $raiz 'src\Loja.Server\bin\Debug\net8.0\Loja.Server.dll'
$agentDll = (Get-ChildItem "$raiz\src\Loja.Agent\bin\Debug" -Recurse -Filter LojaAgent.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not (Test-Path $serverDll) -or -not $agentDll) { throw 'Compilacao nao gerou os arquivos esperados.' }

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$work\data", "$work\agent" -Force | Out-Null
@'
@echo off
echo %*>> "%~dp0choco-calls.log"
if "%1"=="--version" (echo 2.4.3& exit /b 0)
if "%1"=="list" (echo 7zip^|23.1& exit /b 0)
if "%2"=="notepadplusplus" (ping -n 300 127.0.0.1 >nul& exit /b 0)
echo Chocolatey simulado: %1 %2
exit /b 0
'@ | Set-Content "$work\choco.cmd" -Encoding ASCII

$script:server = $null; $script:agent = $null
function Start-Server([bool]$agentEnabled, [bool]$trust) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Urls = $Base
    $env:Loja__DataDir = "$work\data"
    $env:Loja__Agent__Enabled = "$agentEnabled"
    $env:Loja__Agent__TrustUserIdentityInDevelopment = "$trust"
    $env:Loja__Agent__PollSeconds = '15'
    $env:Loja__Agent__JobTimeoutSeconds = '30'
    $script:server = Start-Process $dotnet -ArgumentList "`"$serverDll`"" -WorkingDirectory (Split-Path $serverDll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$work\server.out.log" -RedirectStandardError "$work\server.err.log"
    if (-not (Wait-Until { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; $true } catch { $false } } 40)) { throw 'O servidor nao subiu.' }
}
function Stop-Server { if ($script:server -and -not $script:server.HasExited) { Stop-Process -Id $script:server.Id -Force }; $script:server = $null; Start-Sleep -Seconds 1 }
function Start-Agent {
    $env:LOJAAGENT_SERVERURL = $Base
    $env:LOJAAGENT_CHOCOPATH = "$work\choco.cmd"
    $env:LOJAAGENT_DATADIR = "$work\agent"
    $env:LOJAAGENT_CONFIG = "$work\agent\nao-existe.json"
    $script:agent = Start-Process $dotnet -ArgumentList "`"$agentDll`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$work\agent.out.log" -RedirectStandardError "$work\agent.err.log"
}
function Stop-Agent { if ($script:agent -and -not $script:agent.HasExited) { Stop-Process -Id $script:agent.Id -Force }; $script:agent = $null; Start-Sleep -Seconds 1 }
$hb = @{ hostname = $pc; agentVersion = '0.1.0'; osCaption = 'Windows 11 Pro'; osBuild = 26200; productType = 1; loggedUser = 'x'; runningJobs = @() }

try {
    Write-Output '=== Fase A: agente desligado no servidor (padrao) ==='
    Start-Server $false $false
    $r = Call POST '/api/agent/heartbeat' $hb
    Check 'batimento com o agente desligado -> 404' ($r.Code -eq 404)
    Stop-Server

    Write-Output '=== Fase B: agente ligado, mas sem confiar em login de usuario ==='
    Start-Server $true $false
    $r = Call POST '/api/agent/heartbeat' $hb
    Check 'login de usuario (nao e conta de computador) -> 403' ($r.Code -eq 403)
    $r = Call POST '/api/agent/heartbeat' $hb -Anon
    Check 'sem login -> 401' ($r.Code -eq 401)
    $r = Call GET "/api/agent/jobs/next?hostname=$pc&wait=0"
    Check 'buscar trabalho como usuario -> 403' ($r.Code -eq 403)
    Stop-Server

    Write-Output '=== Fase C: fluxo completo com o agente ==='
    Start-Server $true $true
    $apps = @{}
    foreach ($id in '7zip', 'notepadplusplus') {
        $a = Call POST '/api/admin/apps' @{ packageId = $id; source = 'community'; groups = @('*') }
        Check "cadastra $id" ($a.Code -eq 200) $a.Body.title
        $apps[$id] = $a.Body.id
    }

    Start-Agent
    $up = Wait-Until { (Test-Path "$work\agent\agent.log") -and ((Get-Content "$work\agent\agent.log" -Raw) -match 'Primeiro batimento aceito') } 40
    Check 'agente bateu no servidor' $up
    $me = Call GET "/api/me?hostname=$pc"
    Check 'servidor ve a maquina online' ($me.Body.machineOk) $me.Body.machineMessage

    # 1) instalar 7zip pelo agente
    $i = Call POST '/api/install' @{ appId = $apps['7zip']; hostname = $pc }
    Check 'pedido de instalacao aceito' ($i.Code -eq 200 -and $i.Body.status -eq 'running') $i.Body.message
    $row = Wait-Request $i.Body.id 60
    Check 'instalacao conclui com sucesso' ($row.status -eq 'success') "status=$($row.status) msg=$($row.message)"
    $calls = Get-Content "$work\choco-calls.log" -ErrorAction SilentlyContinue
    Check 'o agente rodou o choco com o pacote certo' (($calls -join "`n") -match 'install 7zip -y --no-progress --source=') ($calls -join ' | ')
    $cat = (Call GET "/api/catalog?hostname=$pc").Body.items | Where-Object packageId -eq '7zip'
    Check 'inventario veio do agente (versao 23.1 do choco falso)' ($cat.installedVersion -eq '23.1') "instalado=$($cat.installedVersion)"

    # 2) instalacao que trava: tempo maximo (30 s) e fim da arvore de processos
    $pingsAntes = @(Get-Process ping -ErrorAction SilentlyContinue).Count
    $t0 = Get-Date
    $i = Call POST '/api/install' @{ appId = $apps['notepadplusplus']; hostname = $pc }
    Check 'pedido que vai travar aceito' ($i.Code -eq 200)
    $row = Wait-Request $i.Body.id 90
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    Check 'travada: encerrada por tempo maximo (codigo 124)' ($row.status -eq 'failed' -and $row.exitCode -eq 124) "status=$($row.status) codigo=$($row.exitCode) em $secs s"
    Check 'travada: mensagem clara para o usuario' ($row.message -match 'tempo m') $row.message
    Start-Sleep -Seconds 2
    Check 'travada: nenhum processo ficou para tras (ping do choco falso)' (@(Get-Process ping -ErrorAction SilentlyContinue).Count -le $pingsAntes)

    # 3) cancelamento pelo painel
    $i = Call POST '/api/install' @{ appId = $apps['notepadplusplus']; hostname = $pc }
    Start-Sleep -Seconds 4
    $c = Call POST "/api/admin/requests/$($i.Body.id)/cancel"
    Check 'cancelar pedido em execucao aceito' ($c.Code -eq 200) $c.Body.message
    $row = Wait-Request $i.Body.id 45
    Check 'cancelado: agente encerrou (codigo 125)' ($row.status -eq 'failed' -and $row.exitCode -eq 125) "status=$($row.status) codigo=$($row.exitCode) msg=$($row.message)"
    Start-Sleep -Seconds 2
    Check 'cancelado: nenhum processo ficou para tras' (@(Get-Process ping -ErrorAction SilentlyContinue).Count -le $pingsAntes)
    $c = Call POST "/api/admin/requests/$($i.Body.id)/cancel"
    Check 'cancelar de novo -> 409 (ja terminou)' ($c.Code -eq 409) $c.Body.message

    # 4) cancelar um pedido que ainda esta na fila (agente parado)
    Stop-Agent
    $i = Call POST '/api/install' @{ appId = $apps['7zip']; hostname = $pc }
    Check 'pedido aceito com o agente parado (ainda conta como online)' ($i.Code -eq 200) $i.Body.message
    $c = Call POST "/api/admin/requests/$($i.Body.id)/cancel"
    Check 'cancelar pedido na fila' ($c.Code -eq 200 -and $c.Body.message -match 'antes de come') $c.Body.message
    $row = Wait-Request $i.Body.id 5
    Check 'pedido na fila ficou como cancelado' ($row.status -eq 'failed' -and $row.exitCode -eq 125)

    # 5) o comando status do agente
    $env:LOJAAGENT_CONFIG = "$work\agent\nao-existe.json"
    $out = & $dotnet $agentDll status 2>&1 | Out-String
    Check 'comando status mostra servico e servidor' ($out -match 'Servico' -and $out -match 'Servidor responde: sim') ($out -replace "`r?`n", ' | ')

    Write-Output '=== Fase D: politica do servidor (Configuracoes do painel) chega ao agente ==='
    $r = Call GET "/api/agent/policy?hostname=$pc"
    Check 'politica: padrao (versao 0, tudo ligado, endereco do appsettings)' ($r.Code -eq 200 -and $r.Body.version -eq 0 -and $r.Body.chocolateyEnabled -and $r.Body.internalAppsEnabled -and $r.Body.communityFeed -like 'https://community.chocolatey.org/*') ($r.Body | ConvertTo-Json -Compress)
    $r = Call GET "/api/agent/policy?hostname=$pc" -Anon
    Check 'politica sem login -> 401' ($r.Code -eq 401)
    $r = Call POST '/api/agent/heartbeat' $hb
    Check 'batimento traz a versao da configuracao' ($r.Code -eq 200 -and $r.Body.configVersion -eq 0) "configVersion=$($r.Body.configVersion)"

    Remove-Item "$work\choco-calls.log" -ErrorAction SilentlyContinue
    Start-Agent
    $got = Wait-Until { (Test-Path "$work\agent\agent.log") -and ((Get-Content "$work\agent\agent.log" -Raw) -match 'atualizada \(vers\S+ 0\)') } 40
    Check 'agente buscou a politica na primeira conversa' $got
    Check 'agente guardou a copia local (policy.json)' ((Test-Path "$work\agent\policy.json") -and ((Get-Content "$work\agent\policy.json" -Raw | ConvertFrom-Json).Version -eq 0))

    # troca o endereco do Chocolatey pelo painel: o agente ja instalado passa a usar o novo, sem reinstalar
    $r = Call PUT '/api/admin/config' @{ values = @{ 'chocolatey.communityFeedUrl' = 'https://espelho.exemplo.local/api/v2/' } }
    Check 'painel salva o novo endereco (versao 1)' ($r.Code -eq 200 -and $r.Body.version -eq 1)
    $got = Wait-Until { (Get-Content "$work\agent\agent.log" -Raw) -match 'atualizada \(vers\S+ 1\)' } 50
    Check 'agente recebeu a politica nova no proximo batimento (sem reinstalar)' $got
    $i = Call POST '/api/install' @{ appId = $apps['7zip']; hostname = $pc }
    $row = Wait-Request $i.Body.id 60
    $calls = Get-Content "$work\choco-calls.log" -ErrorAction SilentlyContinue
    Check 'o choco foi chamado com o endereco novo' (($calls -join "`n") -match 'install 7zip -y --no-progress --source=https://espelho\.exemplo\.local/api/v2/') ($calls -join ' | ')

    # a pasta interna tambem vem da politica
    $r = Call PUT '/api/admin/config' @{ values = @{ 'internalApps.feedPath' = 'C:\pacotes-internos-teste' } }
    $got = Wait-Until { (Get-Content "$work\agent\agent.log" -Raw) -match 'atualizada \(vers\S+ 2\)' } 50
    Check 'agente recebeu a pasta interna nova' ($r.Code -eq 200 -and $got)
    Check 'a pasta interna da politica esta no arquivo local' ((Get-Content "$work\agent\policy.json" -Raw | ConvertFrom-Json).InternalFeed -eq 'C:\pacotes-internos-teste')

    # a configuracao sobrevive a reiniciar o servidor
    Stop-Agent
    Stop-Server
    Start-Server $true $true
    $cfg = (Call GET '/api/admin/config').Body
    $s = foreach ($g in $cfg.groups) { foreach ($x in $g.settings) { $x } }
    Check 'configuracao salva sobrevive ao reinicio (versao e valores)' ($cfg.version -eq 2 -and ($s | Where-Object key -eq 'chocolatey.communityFeedUrl').value -eq 'https://espelho.exemplo.local/api/v2/' -and ($s | Where-Object key -eq 'internalApps.feedPath').value -eq 'C:\pacotes-internos-teste')
    $r = Call GET "/api/agent/policy?hostname=$pc"
    Check 'politica depois do reinicio traz os valores salvos' ($r.Body.version -eq 2 -and $r.Body.communityFeed -eq 'https://espelho.exemplo.local/api/v2/')
}
finally {
    Stop-Agent
    Stop-Server
    foreach ($n in 'LOJAAGENT_SERVERURL', 'LOJAAGENT_CHOCOPATH', 'LOJAAGENT_DATADIR', 'LOJAAGENT_CONFIG', 'Urls', 'Loja__DataDir', 'Loja__Agent__Enabled', 'Loja__Agent__TrustUserIdentityInDevelopment', 'Loja__Agent__PollSeconds', 'Loja__Agent__JobTimeoutSeconds', 'ASPNETCORE_ENVIRONMENT') {
        Remove-Item "Env:$n" -ErrorAction SilentlyContinue
    }
}

Write-Output ''
if ($script:fails -eq 0) { Write-Output 'TUDO CERTO'; exit 0 } else { Write-Output "$($script:fails) FALHA(S). Logs em $work"; exit 1 }
