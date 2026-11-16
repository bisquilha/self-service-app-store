<#
.SYNOPSIS
    Testa a Fase D do servidor 1.6.0: "Reexecutar" um pedido que falhou, "Verificar programas" em varias maquinas de uma vez
    e o ultimo contato das maquinas.

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development, Tactical simulado, pacote interno "teste-falha" no feed)
    e uma pasta de dados NOVA. Precisa do python no PATH e de -DataDir (grava pedidos de mentira direto no banco para montar
    pedidos de maquinas que estao offline, que sao servidores etc.). O app "teste-falha" criado aqui e apagado no fim.

.EXAMPLE
    .\test-maquinas-reexecucao.ps1 -DataDir C:\temp\loja-dados
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
function Req($id) { @(Items (Call GET '/api/admin/history?size=500').Body.items) | Where-Object { $_.id -eq $id } | Select-Object -First 1 }
function WaitDone($id) {
    for ($i = 0; $i -lt 40; $i++) {
        $r = Req $id
        if ($r -and $r.status -ne 'running') { return $r }
        Start-Sleep -Milliseconds 700
    }
    return Req $id
}
function WaitIdle() {
    for ($i = 0; $i -lt 40; $i++) {
        if (@(Items (Call GET '/api/admin/history?status=running&size=50').Body.items).Count -eq 0) { return }
        Start-Sleep -Milliseconds 700
    }
}
function FakeFailed($host_, $appId, $pkg, $title, $action) {
    $sql = "INSERT INTO requests (created_utc, finished_utc, username, hostname, agent_id, app_id, package_id, title, source, action, status, message, exit_code) VALUES ('2026-10-20T10:00:00.0000000Z', '2026-10-20T10:01:00.0000000Z', 'CORP\ana.souza', ?, NULL, ?, ?, ?, 'internal', ?, 'failed', 'falhou de mentira', 1)"
    $id = python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1], timeout=10); cur=c.execute(sys.argv[2], tuple(sys.argv[3:])); c.commit(); print(cur.lastrowid)" (Join-Path $DataDir 'loja.db') $sql $host_ $appId $pkg $title $action
    return [int]$id
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

if (-not $DataDir -or -not (Get-Command python -ErrorAction SilentlyContinue)) {
    Skip 'tudo' 'precisa de -DataDir e do python no PATH'
    Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
    exit $script:fails
}

$app = $null
try {
    $a = Call POST '/api/admin/apps' @{ packageId = 'teste-falha'; source = 'internal'; groups = @('*') }
    if (-not $a.Body.id) { Skip 'tudo' "nao consegui criar teste-falha ($($a.Body.message)); o feed interno precisa ter o pacote"; Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"; exit $script:fails }
    $app = $a.Body

    # ---------- Ultimo contato das maquinas ----------
    $machines = @(Items (Call GET '/api/admin/machines').Body)
    $dups = @($machines | Where-Object { $_.hostname -eq 'PC-DUPLICADO' })
    Check 'maquinas: todas trazem o ultimo contato' (@($machines | Where-Object { -not $_.lastSeenUtc }).Count -eq 0)
    Check 'maquinas: nome duplicado mostra contatos diferentes (o antigo ficou para tras)' ($dups.Count -eq 2 -and ($dups | Select-Object -ExpandProperty lastSeenUtc -Unique).Count -eq 2)
    $off = $machines | Where-Object { $_.hostname -eq 'PC-OFFLINE' }
    $days = ((Get-Date).ToUniversalTime() - ([datetime]$off.lastSeenUtc).ToUniversalTime()).TotalDays
    Check 'maquinas: a offline tem contato antigo' ($days -gt 5) "dias=$([math]::Round($days,1))"
    $hasOverdue = @($machines | Where-Object { $_.status -eq 'overdue' }).Count -ge 1
    Check 'maquinas: existe status "sem contato" (overdue) vindo do Tactical' $hasOverdue

    # ---------- Reexecutar: pedido que falha de verdade ----------
    $inst = Call POST '/api/install' @{ appId = $app.id; hostname = $pc; action = 'install' }
    Check 'preparo: pedido de instalar um pacote que falha -> 200' ($inst.Code -eq 200) $inst.Body.message
    $orig = WaitDone $inst.Body.id
    Check 'preparo: o pedido terminou como falhou' ($orig.status -eq 'failed') $orig.status

    $r = Call POST "/api/admin/requests/$($orig.id)/retry"
    Check 'reexecutar -> 200 e vira um pedido NOVO' ($r.Code -eq 200 -and $r.Body.id -ne $orig.id) $r.Body.message
    Check '... ligado ao pedido original (retryOf)' ($r.Body.retryOf -eq $orig.id)
    Check '... e e uma acao do painel (usuario "(painel)"), nao do funcionario' ($r.Body.user -like '*(painel)' -and $r.Body.user -ne $orig.user) $r.Body.user
    Check '... mesma acao, mesmo computador, mesmo aplicativo' ($r.Body.action -eq 'install' -and $r.Body.hostname -eq $pc -and $r.Body.appId -eq $app.id)
    $r2 = Call POST "/api/admin/requests/$($orig.id)/retry"
    Check 'reexecutar de novo ENQUANTO o primeiro roda -> 409 (uma acao por maquina)' ($r2.Code -eq 409) $r2.Body.message
    $done = WaitDone $r.Body.id
    Check 'a reexecucao termina e fica no historico com o vinculo' ($done.status -eq 'failed' -and $done.retryOf -eq $orig.id)
    $origAfter = Req $orig.id
    Check 'o pedido original nao muda' ($origAfter.status -eq 'failed' -and $origAfter.message -eq $orig.message -and -not $origAfter.retryOf)
    $aud = @(Items (Call GET '/api/admin/audit?kind=run&size=20').Body.items) | Where-Object { $_.field -eq 'Reexecutar pedido' }
    Check 'auditoria: a reexecucao fica registrada (pedido original e novo)' (@($aud).Count -ge 1 -and $aud[0].newValue -like "*$($orig.id)*$($done.id)*") ($aud[0].newValue)

    # ---------- Reexecutar: o que NAO pode ----------
    $okInst = Call POST '/api/install' @{ appId = $app.id; hostname = $pc; action = 'uninstall' }
    $uninstOrig = WaitDone $okInst.Body.id
    Check 'preparo: desinstalar tambem falha neste pacote' ($uninstOrig.status -eq 'failed')
    $app2 = (@(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $app.id })
    $null = Call PUT "/api/admin/apps/$($app.id)" @{ groups = @($app2.groups); groupRefs = @($app2.groupRefs); enabled = $true; autoUpdate = $false; allowUninstall = $false }
    $x = Call POST "/api/admin/requests/$($uninstOrig.id)/retry"
    Check 'desinstalacao nao liberada HOJE: nao reexecuta (revalida a regra atual) -> 409' ($x.Code -eq 409 -and $x.Body.message -like '*desinstala*') $x.Body.message
    $null = Call PUT "/api/admin/apps/$($app.id)" @{ groups = @($app2.groups); groupRefs = @($app2.groupRefs); enabled = $false; autoUpdate = $false; allowUninstall = $true }
    $x = Call POST "/api/admin/requests/$($orig.id)/retry"
    Check 'aplicativo desativado: nao reexecuta instalar -> 409' ($x.Code -eq 409 -and $x.Body.message -like '*desativado*') $x.Body.message
    $null = Call PUT "/api/admin/apps/$($app.id)" @{ groups = @($app2.groups); groupRefs = @($app2.groupRefs); enabled = $true; autoUpdate = $false; allowUninstall = $true }

    $okReq = Call POST '/api/install' @{ appId = $app.id; hostname = $pc; action = 'install' }
    $ok = WaitDone $okReq.Body.id
    Sql "UPDATE requests SET status = 'success' WHERE id = ?" @($ok.id)
    $x = Call POST "/api/admin/requests/$($ok.id)/retry"
    Check 'pedido que deu certo: nao reexecuta -> 409' ($x.Code -eq 409)
    $denied = FakeFailed $pc $app.id 'teste-falha' $app.title 'install'
    Sql "UPDATE requests SET status = 'denied' WHERE id = ?" @($denied)
    Check 'pedido negado: nao reexecuta -> 409' ((Call POST "/api/admin/requests/$denied/retry").Code -eq 409)
    Check 'pedido que nao existe -> 404' ((Call POST '/api/admin/requests/9999999/retry').Code -eq 404)

    $gone = FakeFailed $pc 999999 'sumiu' 'Sumiu' 'install'
    $x = Call POST "/api/admin/requests/$gone/retry"
    Check 'aplicativo que saiu da loja: nao reexecuta -> 409' ($x.Code -eq 409 -and $x.Body.message -like '*n?o est? mais na loja*') $x.Body.message

    # computador que hoje nao passa nas travas
    $offId = FakeFailed 'PC-OFFLINE' $app.id 'teste-falha' $app.title 'install'
    $x = Call POST "/api/admin/requests/$offId/retry"
    Check 'computador offline hoje: nao reexecuta -> 409' ($x.Code -eq 409 -and $x.Body.message -like '*offline*') $x.Body.message
    $srvId = FakeFailed 'SRV-EXEMPLO' $app.id 'teste-falha' $app.title 'install'
    $x = Call POST "/api/admin/requests/$srvId/retry"
    Check 'servidor: nunca reexecuta -> 409' ($x.Code -eq 409 -and $x.Body.message -like '*esta??es*') $x.Body.message
    $dupId = FakeFailed 'PC-DUP-ONLINE' $app.id 'teste-falha' $app.title 'install'
    $x = Call POST "/api/admin/requests/$dupId/retry"
    Check 'nome repetido online: nao reexecuta -> 409' ($x.Code -eq 409)
    $noneId = FakeFailed 'PC-NAO-EXISTE' $app.id 'teste-falha' $app.title 'install'
    Check 'computador que sumiu do Tactical: nao reexecuta -> 409' ((Call POST "/api/admin/requests/$noneId/retry").Code -eq 409)
    $listId = FakeFailed $pc $app.id 'teste-falha' $app.title 'list'
    Check 'consulta de programas (list) nao e reexecutada -> 409' ((Call POST "/api/admin/requests/$listId/retry").Code -eq 409)
    WaitIdle

    # ---------- Verificar programas em varias maquinas ----------
    $s = Call POST '/api/admin/machines/scan' @{ hostnames = @($pc, 'PC-OFFLINE', 'SRV-EXEMPLO', 'PC-NAO-EXISTE', $pc.ToLower(), '  ') }
    Check 'verificar varias: so a maquina ao alcance e consultada' ($s.Code -eq 200 -and $s.Body.started -eq 1 -and $s.Body.skipped -eq 3) "started=$($s.Body.started) skipped=$($s.Body.skipped)"
    $msg = @($s.Body.messages) -join ' | '
    Check '... as puladas dizem o motivo' ($msg -like '*PC-OFFLINE*' -and $msg -like '*SRV-EXEMPLO*' -and $msg -like '*PC-NAO-EXISTE*') $msg
    for ($i = 0; $i -lt 30; $i++) { if ((Call GET "/api/admin/machines/$pc").Body.programsUtc) { break }; Start-Sleep -Milliseconds 700 }
    Check '... a lista de programas chegou na maquina consultada' ($null -ne (Call GET "/api/admin/machines/$pc").Body.programsUtc)
    Check 'verificar varias: sem maquinas -> 400' ((Call POST '/api/admin/machines/scan' @{ hostnames = @() }).Code -eq 400)
    Check 'verificar varias: mais de 100 -> 400' ((Call POST '/api/admin/machines/scan' @{ hostnames = @(1..101 | ForEach-Object { "PC-$_" }) }).Code -eq 400)
    $aud = @(Items (Call GET '/api/admin/audit?kind=run&size=20').Body.items) | Where-Object { $_.entityLabel -eq 'Verificar programas' }
    Check 'auditoria: a verificacao em varias maquinas fica registrada' (@($aud).Count -ge 1 -and $aud[0].newValue -like '*4 m?quinas*')
    # a consulta nao aparece nos historicos de instalacao
    $listsInHistory = @(Items (Call GET '/api/admin/history?action=list&size=50').Body.items)
    Check 'a consulta (list) continua fora do historico de pedidos' ($listsInHistory.Count -eq 0)
}
finally {
    if ($app) { $null = Call DELETE "/api/admin/apps/$($app.id)" }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
