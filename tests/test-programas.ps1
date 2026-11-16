<#
.SYNOPSIS
    Testa a Fase C (servidor 1.6.0+): programas do Windows fora da loja, estados de versao, atualizacoes disponiveis e o
    alcance de "Atualizar em todas".

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development, Tactical simulado) e uma pasta de dados NOVA.
    Usa os pacotes vlc, 7zip e putty (internet). Precisa do python no PATH e de -DataDir (mexe direto no banco para montar
    inventarios de maquinas que nao existem de verdade). Os aplicativos criados aqui sao apagados no fim.

.EXAMPLE
    .\test-programas.ps1 -DataDir C:\temp\loja-dados
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0
$pc = $env:COMPUTERNAME

function Call($method, $path, $body = $null) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 8)); $p.ContentType = 'application/json; charset=utf-8' }
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
function Skip($name, $why) { Write-Output "PULOU $name ($why)"; $script:skips++ }
function Items($b) { @($b | Where-Object { $null -ne $_ }) }
function Sql($sql, $a = @()) {
    python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1], timeout=10); c.execute(sys.argv[2], tuple(sys.argv[3:])); c.commit()" (Join-Path $DataDir 'loja.db') $sql @($a)
}
function Catalog($hostname) { @(Items (Call GET "/api/catalog?hostname=$hostname").Body.items) }
function Item($hostname, $pkg) { Catalog $hostname | Where-Object { $_.packageId -eq $pkg } | Select-Object -First 1 }
function SetPkg($hostname, $pkg, $version) { Sql 'INSERT OR REPLACE INTO host_packages (hostname, package_id, version) VALUES (?,?,?)' @($hostname, $pkg, $version) }
function WaitRequests() {
    for ($i = 0; $i -lt 60; $i++) {
        $running = @(Items (Call GET '/api/admin/requests?take=50').Body | Where-Object { $_.status -eq 'running' })
        if ($running.Count -eq 0) { return }
        Start-Sleep -Milliseconds 700
    }
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

if (-not $DataDir -or -not (Get-Command python -ErrorAction SilentlyContinue)) {
    Skip 'tudo' 'precisa de -DataDir e do python no PATH'
    Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
    exit $script:fails
}

$created = @()
try {
    foreach ($pkg in 'vlc', '7zip', 'putty') {
        $a = Call POST '/api/admin/apps' @{ packageId = $pkg; source = 'community'; groups = @('*') }
        if ($a.Body.id) { $created += $a.Body.id } else { Write-Output "AVISO nao criei $pkg : $($a.Body.message)" }
    }
    Check 'preparo: 3 apps criados' ($created.Count -eq 3)
    $vlc, $zip, $putty = $created
    $vlcVersion = (@(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $vlc }).version

    # ---------- Coleta dos programas do Windows ----------
    $before = Call GET "/api/admin/machines/$pc"
    Check 'antes da coleta: sem programas e sem data' ($null -eq $before.Body.programs -and $null -eq $before.Body.programsUtc)
    $c0 = Call GET "/api/catalog?hostname=$pc"
    Check 'antes da coleta: o catalogo nao afirma nada sobre programas (ProgramsUtc vazio)' ($null -eq $c0.Body.programsUtc)
    $act = Call POST "/api/admin/machines/$pc/action" @{ action = 'list' }
    Check 'verificar programas -> 200' ($act.Code -eq 200) $act.Body.message
    # a consulta "list" nao aparece nos historicos: espera ate a lista de programas chegar
    for ($i = 0; $i -lt 30; $i++) {
        if ((Call GET "/api/admin/machines/$pc").Body.programsUtc) { break }
        Start-Sleep -Milliseconds 700
    }
    $d = Call GET "/api/admin/machines/$pc"
    $names = @($d.Body.programs | ForEach-Object { $_.name })
    Check 'depois da coleta: 5 programas do Windows e a data' (@($d.Body.programs).Count -eq 5 -and $d.Body.programsUtc) "n=$(@($d.Body.programs).Count)"
    $foxit = @($d.Body.programs) | Where-Object { $_.name -eq 'Foxit PDF Reader' }
    Check '... programa sem versao fica com versao vazia' ($foxit -and $foxit.version -eq '')
    Check '... nenhum programa ainda esta ligado a um aplicativo' (-not (@($d.Body.programs) | Where-Object { $_.storeApp }))
    $c1 = Call GET "/api/catalog?hostname=$pc"
    Check 'o catalogo agora traz a data da coleta' ($null -ne $c1.Body.programsUtc)

    # ---------- Sem nome cadastrado nao ha deteccao ----------
    $it = Item $pc 'vlc'
    Check 'sem nome cadastrado: VLC "Nao instalado", fora da loja = nao' ($it.versionState -eq 'none' -and -not $it.outsideStore -and -not $it.installedVersion)

    # ---------- Nome exato cadastrado ----------
    $r = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('  vlc   MEDIA player ') }
    Check 'cadastrar nome (com espacos e maiusculas diferentes) -> 200' ($r.Code -eq 200 -and @($r.Body.aliases).Count -eq 1 -and $r.Body.aliases[0] -eq 'vlc MEDIA player') ($r.Body.aliases -join '|')
    $it = Item $pc 'vlc'
    Check 'nome exato (sem diferenciar maiusculas/espacos): "Ja instalado no computador", Fora da loja, versao 3.0.20' ($it.versionState -eq 'external' -and $it.outsideStore -and $it.externalVersion -eq '3.0.20' -and $it.externalName -eq 'VLC media player') "$($it.versionState) $($it.externalVersion)"
    Check '... mas NAO vira "instalado": sem versao instalada e sem "atualizacao disponivel"' (-not $it.installedVersion -and -not $it.updateAvailable)
    Check '... a instalacao continua permitida so pela loja (AllowUninstall segue o cadastro, nao a deteccao)' ($it.allowUninstall -eq $true)
    $d = Call GET "/api/admin/machines/$pc"
    $vlcProgram = @($d.Body.programs) | Where-Object { $_.name -eq 'VLC media player' }
    Check 'detalhe da maquina: o programa aparece ligado ao aplicativo' ($vlcProgram.storeApp -ne $null)
    $state = @($d.Body.storeApps) | Where-Object { $_.appId -eq $vlc }
    Check 'detalhe da maquina: estado do app = external, com a versao' ($state.versionState -eq 'external' -and $state.externalVersion -eq '3.0.20')

    $null = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('VLC') }
    Check 'nome PARCIAL ("VLC") nao casa: sem semelhanca de nome' ((Item $pc 'vlc').versionState -eq 'none')
    $null = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('VLC media') }
    Check 'nome PARCIAL ("VLC media") tambem nao casa' ((Item $pc 'vlc').versionState -eq 'none')
    $null = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('VLC media player') }

    # ---------- Maquina cuja coleta de programas nunca aconteceu (script antigo) ----------
    Sql 'INSERT OR REPLACE INTO host_inventory (hostname, updated_utc) VALUES (?, ?)' @('PC-ANTIGO', '2026-10-01T10:00:00.0000000Z')
    Sql 'INSERT OR REPLACE INTO host_programs (hostname, name, version) VALUES (?,?,?)' @('PC-ANTIGO', 'VLC media player', '3.0.20')
    $it = Item 'PC-ANTIGO' 'vlc'
    Check 'sem data de coleta (script antigo): nao afirma "fora da loja" mesmo com o nome cadastrado' ($it.versionState -eq 'none' -and -not $it.outsideStore)

    # ---------- Sem duplicar com o Chocolatey ----------
    $null = Call PUT "/api/admin/apps/$zip/aliases" @{ aliases = @('7-Zip 24.09 (x64)') }
    $it = Item $pc '7zip'
    Check '7-Zip fora da loja: external, versao 24.09' ($it.versionState -eq 'external' -and $it.externalVersion -eq '24.09')
    $inst = Call POST '/api/install' @{ appId = $zip; hostname = $pc; action = 'install' }
    Check 'instalar 7-Zip pela loja (simulado) -> 200' ($inst.Code -eq 200) $inst.Body.message
    WaitRequests
    $it = Item $pc '7zip'
    Check 'instalado pelo Chocolatey E com nome cadastrado: conta uma vez so (nao e fora da loja)' ($it.installedVersion -and -not $it.outsideStore -and $it.versionState -ne 'external') "$($it.versionState) inst=$($it.installedVersion)"

    # ---------- Programa sem versao ----------
    $null = Call PUT "/api/admin/apps/$putty/aliases" @{ aliases = @('Foxit PDF Reader') }
    $it = Item $pc 'putty'
    Check 'programa que nao informa versao: external, versao em branco (nada inventado)' ($it.versionState -eq 'external' -and $null -eq $it.externalVersion -and $it.externalName -eq 'Foxit PDF Reader')

    # ---------- Estados de versao do Chocolatey ----------
    SetPkg $pc 'vlc' '0.1'
    $it = Item $pc 'vlc'
    Check 'instalada menor: update, UpdateAvailable' ($it.versionState -eq 'update' -and $it.updateAvailable)
    SetPkg $pc 'vlc' $vlcVersion
    $it = Item $pc 'vlc'
    Check 'instalada igual: current, sem atualizacao' ($it.versionState -eq 'current' -and -not $it.updateAvailable) "v=$vlcVersion"
    SetPkg $pc 'vlc' '99.0'
    $it = Item $pc 'vlc'
    Check 'instalada MAIOR que a da loja: newer, nunca oferece atualizar (nem downgrade)' ($it.versionState -eq 'newer' -and -not $it.updateAvailable)
    SetPkg $pc 'vlc' 'abc'
    $it = Item $pc 'vlc'
    Check 'versao instalada que nao da para comparar: unknown, nao afirma "em dia" nem oferece atualizar' ($it.versionState -eq 'unknown' -and -not $it.updateAvailable)
    SetPkg $pc 'vlc' '1.0-beta'
    $it = Item $pc 'vlc'
    Check 'versao com sufixo (1.0-beta) compara normalmente' ($it.versionState -eq 'update')

    # ---------- Validacao dos nomes ----------
    $many = @(1..11 | ForEach-Object { "Nome $_" })
    Check 'mais de 10 nomes -> 400' ((Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = $many }).Code -eq 400)
    Check 'nome com mais de 200 letras -> 400' ((Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('x' * 201) }).Code -eq 400)
    $r = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('A', ' a ', "A`tb", '', '   ') }
    Check 'repetidos e vazios saem; controle vira espaco' ($r.Code -eq 200 -and @($r.Body.aliases).Count -eq 2) ($r.Body.aliases -join '|')
    Check 'aplicativo que nao existe -> 404' ((Call PUT '/api/admin/apps/999999/aliases' @{ aliases = @('x') }).Code -eq 404)
    $r = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @() }
    Check 'lista vazia limpa os nomes' ($r.Code -eq 200 -and @($r.Body.aliases).Count -eq 0)
    $list = @(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $vlc }
    Check 'a listagem de aplicativos traz os nomes (campo aliases)' ($null -ne $list.aliases)

    # ---------- Auditoria dos nomes ----------
    $null = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @('VLC media player') }
    $null = Call PUT "/api/admin/apps/$vlc/aliases" @{ aliases = @(); reason = 'undo' }
    $aud = @(Items (Call GET "/api/admin/audit?entity=app&entityId=$vlc&size=50").Body.items) | Where-Object { $_.field -eq 'Nomes no Windows' }
    Check 'auditoria: alteracao dos nomes registrada com antes e depois' (@($aud).Count -ge 3 -and ($aud | Where-Object { $_.newValue -eq 'VLC media player' }))
    Check 'auditoria: o "Desfazer" fica como revertido' ($aud | Where-Object { $_.kind -eq 'undo' -and $_.undo })

    # ---------- Busca de nomes de programa ----------
    $found = @(Items (Call GET '/api/admin/programs?q=vlc').Body)
    Check 'busca de programas pelo texto: acha o nome e conta as maquinas' ($found.Count -ge 1 -and ($found | Where-Object { $_.name -eq 'VLC media player' -and $_.machines -ge 1 }))
    Check 'busca com 1 letra devolve vazio' (@(Items (Call GET '/api/admin/programs?q=v').Body).Count -eq 0)
    Check 'busca com % e _ e literal (nao casa tudo)' (@(Items (Call GET '/api/admin/programs?q=%25%25').Body).Count -eq 0 -and @(Items (Call GET '/api/admin/programs?q=__').Body).Count -eq 0)

    # ---------- Atualizacoes disponiveis e o alcance de "Atualizar em todas" ----------
    foreach ($h in 'PC-OFFLINE', 'SRV-EXEMPLO', 'SRV-COMO-ESTACAO', 'PC-DUP-ONLINE', 'PC-NAO-EXISTE', 'PC-COM-VERSAO-ESTRANHA', 'PC-MAIS-NOVO') {
        Sql 'INSERT OR REPLACE INTO host_inventory (hostname, updated_utc) VALUES (?, ?)' @($h, '2026-10-01T10:00:00.0000000Z')
    }
    foreach ($h in 'PC-OFFLINE', 'SRV-EXEMPLO', 'SRV-COMO-ESTACAO', 'PC-DUP-ONLINE', 'PC-NAO-EXISTE') { SetPkg $h 'vlc' '0.1' }
    SetPkg 'PC-COM-VERSAO-ESTRANHA' 'vlc' 'latest'
    SetPkg 'PC-MAIS-NOVO' 'vlc' '99.0'
    SetPkg $pc 'vlc' '0.2'
    $up = @(Items (Call GET '/api/admin/updates').Body) | Where-Object { $_.appId -eq $vlc }
    $reach = @{}
    foreach ($m in @($up.machines)) { $reach[$m.hostname] = $m.reach }
    Check 'atualizacoes: o VLC aparece com as maquinas de versao menor' ($up -and @($up.machines).Count -eq 6) "n=$(@($up.machines).Count)"
    Check '... maquina com versao que nao da para comparar e a mais nova ficam FORA da lista' (-not $reach.ContainsKey('PC-COM-VERSAO-ESTRANHA') -and -not $reach.ContainsKey('PC-MAIS-NOVO'))
    Check '... alcance: este PC online = ready' ($reach[$pc] -eq 'ready')
    Check '... alcance: offline' ($reach['PC-OFFLINE'] -eq 'offline')
    Check '... alcance: servidor e servidor cadastrado como estacao = blocked' ($reach['SRV-EXEMPLO'] -eq 'blocked' -and $reach['SRV-COMO-ESTACAO'] -eq 'blocked')
    Check '... alcance: dois online com o mesmo nome = duplicate' ($reach['PC-DUP-ONLINE'] -eq 'duplicate')
    Check '... alcance: fora do Tactical = missing' ($reach['PC-NAO-EXISTE'] -eq 'missing')
    Check '... a versao da loja vem junto' ($up.latestVersion -eq $vlcVersion)
    $ups = Call POST "/api/admin/apps/$vlc/upgrade-all"
    Check 'Atualizar em todas: envia SO para a maquina ao alcance' ($ups.Code -eq 200 -and $ups.Body.started -eq 1) "started=$($ups.Body.started) skipped=$($ups.Body.skipped)"
    Check '... as outras ficam puladas com o motivo' ($ups.Body.skipped -eq 5 -and (@($ups.Body.messages) -join ' ') -like '*PC-OFFLINE*')
    WaitRequests
    $hist = @(Items (Call GET '/api/admin/history?action=upgrade&size=200').Body.items) | Where-Object { $_.packageId -like 'vlc*' }
    $hosts = @($hist | ForEach-Object { $_.hostname } | Sort-Object -Unique)
    Check 'historico: pedidos de atualizacao do VLC so no computador ao alcance' ($hosts.Count -eq 1 -and $hosts[0] -eq $pc) ($hosts -join ',')
    $aud = @(Items (Call GET '/api/admin/audit?kind=run&size=20').Body.items) | Where-Object { $_.field -like 'Atualizar em todas as m?quinas' }
    Check 'auditoria: "Atualizar em todas" registrada com quem pediu' (@($aud).Count -ge 1)

    # ---------- O funcionario nunca dispara uma operacao em varias maquinas ----------
    Check 'API do usuario nao tem "atualizar todas as maquinas" (rota inexistente)' ((Call POST "/api/apps/$vlc/upgrade-all").Code -in 404, 405)
    $x = Call POST '/api/install' @{ appId = $vlc; hostname = 'PC-OFFLINE'; action = 'upgrade' }
    Check 'pedido de usuario em OUTRO computador (offline) -> 409' ($x.Code -eq 409)
    $x = Call POST '/api/install' @{ appId = $vlc; hostname = 'SRV-EXEMPLO'; action = 'upgrade' }
    Check 'pedido de usuario em servidor -> 409' ($x.Code -eq 409)

    # ---------- Atualizacao automatica so com versoes comparaveis ----------
    $app = @(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $vlc }
    $null = Call PUT "/api/admin/apps/$vlc" @{ groups = @($app.groups); groupRefs = @($app.groupRefs); enabled = $true; autoUpdate = $true; allowUninstall = $true }
    foreach ($h in 'PC-OFFLINE', 'SRV-EXEMPLO', 'SRV-COMO-ESTACAO', 'PC-DUP-ONLINE', 'PC-NAO-EXISTE', 'PC-COM-VERSAO-ESTRANHA', 'PC-MAIS-NOVO') { Sql 'DELETE FROM host_packages WHERE hostname = ?' @($h) }
    SetPkg $pc 'vlc' 'abc'
    $run = Call POST '/api/admin/autoupdate/run'
    Check 'atualizacao automatica: versao instalada que nao da para comparar NAO e atualizada' ($run.Code -eq 200 -and $run.Body.started -eq 0) "started=$($run.Body.started)"
    SetPkg $pc 'vlc' '0.1'
    $run = Call POST '/api/admin/autoupdate/run'
    Check 'atualizacao automatica: versao menor e comparavel e atualizada' ($run.Code -eq 200 -and $run.Body.started -ge 1) "started=$($run.Body.started)"
    WaitRequests
}
finally {
    foreach ($id in $created) { $null = Call DELETE "/api/admin/apps/$id" }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
