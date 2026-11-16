# Testa a identidade da loja (servidor 1.8+): login local por senha (cookie), administrador de emergencia, bloqueio por tentativas,
# grupos locais com membros por login, login do Windows desligavel e a ponte do agente (token com o usuario logado no PC).
# Sobe o proprio servidor (Development, Tactical simulado, porta 5082). Precisa de internet (cadastra o 7zip) e de PC no dominio.
# Uso:  .\test-identidade.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$raiz = Split-Path -Parent $here
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$pc = $env:COMPUTERNAME
$port = 5082
$Base = "http://localhost:$port"
$work = Join-Path $env:TEMP 'loja-ident-test'
$emerg = 'Emergencia#Teste2026'
$script:fails = 0

# Chamada com o login do Windows (como o painel faz hoje) ou com uma sessao de cookie (-Sess).
function Call($method, $path, $body = $null, $Sess = $null, [switch]$Anon, [string]$Bearer = '') {
    $h = @{ 'X-Requested-With' = 'loja-admin' }
    if ($Bearer) { $h['Authorization'] = "Bearer $Bearer" }
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; Headers = $h; MaximumRedirection = 0 }
    if ($Sess) { $p.WebSession = $Sess } elseif (-not $Anon -and -not $Bearer) { $p.UseDefaultCredentials = $true }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 6)); $p.ContentType = 'application/json; charset=utf-8' }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        if ($r.Headers['Content-Type'] -like 'application/json*') { $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json }
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json; Location = $r.Headers['Location'] }
    } catch {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $text = try { (New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd() } catch { '' }
        $json = try { $text | ConvertFrom-Json } catch { $null }
        $loc = try { $resp.Headers['Location'] } catch { $null }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $json; Location = $loc }
    }
}
function NewSession { New-Object Microsoft.PowerShell.Commands.WebRequestSession }
function Login($user, $pass) { $s = NewSession; $r = Call POST '/api/auth/login' @{ username = $user; password = $pass } -Sess $s; [pscustomobject]@{ Sess = $s; R = $r } }
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) { if (& $cond) { return $true }; Start-Sleep -Seconds 1 }
    return $false
}
function Setting($cfg, $key) { foreach ($g in $cfg.groups) { foreach ($s in $g.settings) { if ($s.key -eq $key) { return $s } } } }
function Put($values, $Sess = $null) { Call PUT '/api/admin/config' @{ values = $values } -Sess $Sess }
function CatalogIds($bearer) { @((Call GET "/api/catalog?hostname=$pc" -Bearer $bearer).Body.items | ForEach-Object { $_.packageId }) }

& $dotnet build "$raiz\src\Loja.Server\Loja.Server.csproj" -c Debug -nologo -v q | Out-Null
$serverDll = Join-Path $raiz 'src\Loja.Server\bin\Debug\net8.0\Loja.Server.dll'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$work\data" -Force | Out-Null

$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Urls = $Base; $env:Loja__DataDir = "$work\data"
$env:Loja__Agent__Enabled = 'true'; $env:Loja__Agent__TrustUserIdentityInDevelopment = 'true'
$env:Loja__Identity__EmergencyAdminPassword = $emerg
# No desenvolvimento AdminGroups e "*" (todo mundo admin). Aqui so o grupo (SID) de quem roda o teste e admin.
$mySid = ([Security.Principal.WindowsIdentity]::GetCurrent().Groups | Where-Object { $_.Value -match '^S-1-5-21-.*-513$' } | Select-Object -First 1).Value
if (-not $mySid) { throw 'Nao achei o grupo Domain Users do usuario (o PC precisa estar no dominio).' }
$env:Loja__AdminGroups__0 = $mySid
$server = Start-Process $dotnet -ArgumentList "`"$serverDll`"" -WorkingDirectory (Split-Path $serverDll) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$work\server.out.log" -RedirectStandardError "$work\server.err.log"
try {
    if (-not (Wait-Until { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; $true } catch { $false } } 40)) { throw 'O servidor nao subiu.' }

    Write-Output '=== Padroes: nada muda para quem usa so o Windows ==='
    $cfg = (Call GET '/api/admin/config').Body
    Check 'login do Windows ligado, login local e ponte do agente desligados por padrao' ((Setting $cfg 'identity.windows.enabled').value -eq $true -and (Setting $cfg 'identity.local.enabled').value -eq $false -and (Setting $cfg 'identity.agentBridge.enabled').value -eq $false)
    $users = @((Call GET '/api/admin/identity/users').Body)
    $em = $users | Where-Object username -eq 'emergencia'
    Check 'administrador de emergencia criado pela configuracao do servidor' ($em -and $em.isAdmin -and $em.isEmergency -and $em.enabled)
    Check 'painel com login do Windows continua funcionando' ((Call GET '/api/admin/info').Code -eq 200)
    Check 'sem login nenhum -> 401' ((Call GET '/api/admin/info' -Anon).Code -eq 401)

    Write-Output '=== Administrador de emergencia ==='
    $e = Login 'emergencia' $emerg
    Check 'emergencia entra mesmo com o login local desligado' ($e.R.Code -eq 200 -and $e.R.Body.isAdmin) "codigo=$($e.R.Code)"
    $r = Call GET '/api/admin/info' -Sess $e.Sess
    Check 'cookie da emergencia abre o painel (admin)' ($r.Code -eq 200 -and $r.Body.user -eq 'emergencia') "user=$($r.Body.user)"
    $bad = Login 'emergencia' 'senha-errada-123'
    Check 'senha errada -> 401 sem dizer o que errou' ($bad.R.Code -eq 401 -and $bad.R.Body.message -like '*incorretos*')
    $r = Call POST '/api/auth/login' @{ username = 'emergencia'; password = $emerg } -Anon
    $noHeader = try { Invoke-WebRequest "$Base/api/auth/login" -Method POST -Body '{"username":"emergencia","password":"x"}' -ContentType 'application/json' -UseBasicParsing } catch { $_.Exception.Response }
    Check 'login sem o cabecalho anti-CSRF -> 403' ([int]$noHeader.StatusCode -eq 403)
    Check 'emergencia nao pode ser desligada' ((Call PUT '/api/admin/identity/users/emergencia' @{ enabled = $false }).Code -eq 400)
    Check 'emergencia nao perde o perfil de admin' ((Call PUT '/api/admin/identity/users/emergencia' @{ isAdmin = $false }).Code -eq 400)
    Check 'emergencia nao pode ser apagada' ((Call DELETE '/api/admin/identity/users/emergencia').Code -eq 400)

    Write-Output '=== Usuarios locais ==='
    $r = Call POST '/api/admin/identity/users' @{ username = 'maria'; displayName = 'Maria'; password = 'curta'; isAdmin = $false }
    Check 'senha curta recusada (400)' ($r.Code -eq 400 -and $r.Body.message -like '*10 caracteres*')
    $r = Call POST '/api/admin/identity/users' @{ username = 'maria'; displayName = 'Maria'; password = 'SenhaDaMaria#1'; isAdmin = $false }
    Check 'cria usuario local' ($r.Code -eq 200 -and @($r.Body | Where-Object username -eq 'maria').Count -eq 1)
    Check 'usuario repetido recusado' ((Call POST '/api/admin/identity/users' @{ username = 'MARIA'; password = 'OutraSenha#12345' }).Code -eq 400)
    $m = Login 'maria' 'SenhaDaMaria#1'
    Check 'usuario comum NAO entra com o login local desligado' ($m.R.Code -eq 401)
    Check 'ligar o login local' ((Put @{ 'identity.local.enabled' = $true }).Code -eq 200)
    $m = Login 'maria' 'SenhaDaMaria#1'
    Check 'agora entra' ($m.R.Code -eq 200 -and -not $m.R.Body.isAdmin)
    $x = Call GET '/api/admin/info' -Sess $m.Sess; Check 'usuario comum nao abre o painel de admin (403)' ($x.Code -eq 403) "codigo=$($x.Code)"
    $me = Call GET "/api/me?hostname=$pc" -Sess $m.Sess
    Check 'usuario comum usa a API da loja (/api/me)' ($me.Code -eq 200 -and $me.Body.user -eq 'maria' -and -not $me.Body.isAdmin) "codigo=$($me.Code) $($me.Body | ConvertTo-Json -Compress)"
    Check 'virar administrador vale na hora (mesmo cookie)' ((Call PUT '/api/admin/identity/users/maria' @{ isAdmin = $true }).Code -eq 200 -and (Call GET '/api/admin/info' -Sess $m.Sess).Code -eq 200)
    Check 'deixar de ser admin tambem vale na hora' ((Call PUT '/api/admin/identity/users/maria' @{ isAdmin = $false }).Code -eq 200 -and (Call GET '/api/admin/info' -Sess $m.Sess).Code -eq 403)
    Check 'desligar o usuario derruba a sessao (401)' ((Call PUT '/api/admin/identity/users/maria' @{ enabled = $false }).Code -eq 200 -and (Call GET '/api/me' -Sess $m.Sess).Code -eq 401)
    Check 'usuario desligado nao entra' ((Login 'maria' 'SenhaDaMaria#1').R.Code -eq 401)
    Check 'trocar a senha e religar' ((Call PUT '/api/admin/identity/users/maria' @{ enabled = $true; password = 'NovaSenhaMaria#2' }).Code -eq 200 -and (Login 'maria' 'NovaSenhaMaria#2').R.Code -eq 200)
    Check 'senha antiga nao vale mais' ((Login 'maria' 'SenhaDaMaria#1').R.Code -eq 401)

    Write-Output '=== Bloqueio por tentativas ==='
    Call POST '/api/admin/identity/users' @{ username = 'tentativas'; password = 'SenhaTentativas#1' } | Out-Null
    1..5 | ForEach-Object { Login 'tentativas' 'errada-errada-1' | Out-Null }
    $l = Login 'tentativas' 'SenhaTentativas#1'
    Check 'apos 5 erros a conta bloqueia, ate com a senha certa (429)' ($l.R.Code -eq 429 -and $l.R.Body.message -like '*bloqueada*') $l.R.Body.message
    Call PUT '/api/admin/identity/users/tentativas' @{ password = 'SenhaTentativas#2' } | Out-Null
    Check 'a TI desbloqueia trocando a senha' ((Login 'tentativas' 'SenhaTentativas#2').R.Code -eq 200)
    Check 'apagar usuario comum' ((Call DELETE '/api/admin/identity/users/tentativas').Code -eq 200)

    Write-Output '=== Grupos locais e acesso aos apps ==='
    $r = Call POST '/api/admin/identity/groups' @{ name = 'vendas'; description = 'Equipe de vendas' }
    $g = @($r.Body | Where-Object name -eq 'vendas')[0]
    Check 'cria grupo local (identificador LOCAL-n)' ($r.Code -eq 200 -and $g.sid -like 'LOCAL-*') $g.sid
    Check 'grupo repetido recusado' ((Call POST '/api/admin/identity/groups' @{ name = 'VENDAS' }).Code -eq 400)
    $r = Call PUT "/api/admin/identity/groups/$($g.id)" @{ members = @('joao', 'CORP\Joao', 'pedro') }
    $g = @($r.Body | Where-Object name -eq 'vendas')[0]
    Check 'membros por login (sem repetir, sem dominio)' ($r.Code -eq 200 -and @($g.members).Count -eq 2 -and (@($g.members) -contains 'joao') -and (@($g.members) -contains 'pedro')) (@($g.members) -join ',')
    $seek = (Call GET '/api/admin/groups?q=vend').Body
    Check 'seletor de grupos do painel mostra o grupo local' (@($seek | Where-Object { $_.sid -like 'LOCAL-*' -and $_.name -eq 'vendas' }).Count -eq 1)
    $app = Call POST '/api/admin/apps' @{ packageId = '7zip'; source = 'community'; groups = @('vendas') }
    Check 'cadastra app liberado so para o grupo local' ($app.Code -eq 200 -and $app.Body.groupRefs[0].sid -like 'LOCAL-*') ($app.Body.message)
    $audit = (Call GET '/api/admin/audit?size=40').Body.items
    Check 'auditoria registrou usuarios e grupos locais' (@($audit | Where-Object { $_.entityLabel -in 'Usuário local', 'vendas' }).Count -ge 2)

    Write-Output '=== Ponte do agente (sem AD): o agente informa quem esta logado ==='
    $hb = @{ hostname = $pc; agentVersion = '0.2.0'; osCaption = 'Windows 11 Pro'; osBuild = 26200; productType = 1; loggedUser = 'CORP\joao'; runningJobs = @() }
    Check 'ponte desligada: token recusado (404)' ((Call POST '/api/agent/user-token' @{ hostname = $pc }).Code -eq 404)
    Check 'ligar a ponte do agente' ((Put @{ 'identity.agentBridge.enabled' = $true }).Code -eq 200)
    Check 'sem ninguem logado no PC -> 409' ((Call POST '/api/agent/user-token' @{ hostname = $pc }).Code -eq 409)
    Call POST '/api/agent/heartbeat' $hb | Out-Null
    $t = Call POST '/api/agent/user-token' @{ hostname = $pc }
    Check 'agente recebe token do usuario logado (joao, sem dominio)' ($t.Code -eq 200 -and $t.Body.user -eq 'joao' -and $t.Body.token) "user=$($t.Body.user)"
    Check 'token sem login de agente -> 401' ((Call POST '/api/agent/user-token' @{ hostname = $pc } -Anon).Code -eq 401)
    $tokJoao = $t.Body.token
    Check 'joao (membro de vendas) ve o app' ((CatalogIds $tokJoao) -contains '7zip')
    $hb2 = $hb.Clone(); $hb2.loggedUser = 'ana'; Call POST '/api/agent/heartbeat' $hb2 | Out-Null
    $tokAna = (Call POST '/api/agent/user-token' @{ hostname = $pc }).Body.token
    Check 'ana (fora do grupo) NAO ve o app' (-not ((CatalogIds $tokAna) -contains '7zip'))
    Call PUT "/api/admin/identity/groups/$($g.id)" @{ members = @('joao', 'pedro', 'ana') } | Out-Null
    Check 'colocar ana no grupo vale na hora' ((CatalogIds $tokAna) -contains '7zip')
    Check 'token de usuario nao abre o painel de admin (403)' ((Call GET '/api/admin/info' -Bearer $tokJoao).Code -eq 403)
    Check 'token adulterado -> 401' ((Call GET "/api/catalog?hostname=$pc" -Bearer ($tokJoao.Substring(0, $tokJoao.Length - 3) + 'AAA')).Code -eq 401)
    Check 'token com texto qualquer -> 401' ((Call GET "/api/catalog?hostname=$pc" -Bearer 'abc.def').Code -eq 401)
    Check 'ponte desligada derruba os tokens (401)' ((Put @{ 'identity.agentBridge.enabled' = $false }).Code -eq 200 -and (Call GET "/api/catalog?hostname=$pc" -Bearer $tokJoao).Code -eq 401)

    Write-Output '=== Desligar o login do Windows (a emergencia continua entrando) ==='
    $e = Login 'emergencia' $emerg
    $r = Put @{ 'identity.windows.enabled' = $false } $e.Sess
    Check 'desligar o login do Windows (ha admin local ativo)' ($r.Code -eq 200)
    Check 'login do Windows deixou de valer (401)' ((Call GET '/api/admin/info').Code -eq 401)
    Add-Type -AssemblyName System.Net.Http
    $hc = New-Object System.Net.Http.HttpClient((New-Object System.Net.Http.HttpClientHandler -Property @{ AllowAutoRedirect = $false }))
    $r = $hc.GetAsync("$Base/admin/").Result
    Check 'abrir /admin/ sem entrar leva para a pagina de login (302)' ([int]$r.StatusCode -eq 302 -and "$($r.Headers.Location)" -like '/login*') "codigo=$([int]$r.StatusCode) local=$($r.Headers.Location)"
    $page = Invoke-WebRequest "$Base/login" -UseBasicParsing
    Check 'pagina de login responde' ($page.StatusCode -eq 200 -and $page.Content -like '*Entrar*')
    Check 'a emergencia segue no painel' ((Call GET '/api/admin/info' -Sess $e.Sess).Code -eq 200)
    $mm = Login 'maria' 'NovaSenhaMaria#2'
    Check 'sem Windows, grupos locais valem pelo nome: cadastrar app com grupo local' ((Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('vendas') } -Sess $e.Sess).Code -eq 200)
    Check 'religar o login do Windows pelo painel' ((Put @{ 'identity.windows.enabled' = $true } $e.Sess).Code -eq 200 -and (Call GET '/api/admin/info').Code -eq 200)
    $out = Call POST '/api/auth/logout' -Sess $e.Sess
    Check 'sair derruba o cookie' ($out.Code -eq 204 -and (Call GET '/api/admin/info' -Sess $e.Sess).Code -eq 401)

    Write-Output '=== Persistencia ==='
    Stop-Process -Id $server.Id -Force; Start-Sleep -Seconds 2
    $server = Start-Process $dotnet -ArgumentList "`"$serverDll`"" -WorkingDirectory (Split-Path $serverDll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$work\server.out.log" -RedirectStandardError "$work\server.err.log"
    [void](Wait-Until { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; $true } catch { $false } } 40)
    Check 'usuarios e grupos sobrevivem ao reinicio' ((Login 'maria' 'NovaSenhaMaria#2').R.Code -eq 200 -and @((Call GET '/api/admin/identity/groups').Body | Where-Object name -eq 'vendas').Count -eq 1)
    Check 'a senha de emergencia do arquivo nao recria nada (so uma emergencia)' (@((Call GET '/api/admin/identity/users').Body | Where-Object isEmergency).Count -eq 1)
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    foreach ($n in 'Urls', 'Loja__DataDir', 'Loja__Agent__Enabled', 'Loja__Agent__TrustUserIdentityInDevelopment', 'Loja__Identity__EmergencyAdminPassword', 'Loja__AdminGroups__0', 'ASPNETCORE_ENVIRONMENT') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
}
Write-Output ''
if ($script:fails -eq 0) { Write-Output 'TUDO CERTO'; exit 0 } else { Write-Output "$($script:fails) FALHA(S). Logs em $work"; exit 1 }
