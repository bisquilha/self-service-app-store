<#
.SYNOPSIS
    Testa a edicao de grupos em lote, o Desfazer, a tela Grupos e o filtro por grupo do usuario (servidor 1.6.0+).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development) e uma pasta de dados NOVA, no PC do dominio
    (usa o AD de verdade: grupos LOJA-Todos e LOJA-Admins e pessoas reais, so para LER os grupos delas).
    Usa os pacotes 7zip, vlc e notepadplusplus (internet). Os apps criados aqui sao apagados no fim.

    -DataDir  pasta de dados do servidor (a que tem o loja.db). Obrigatorio para o filtro de grupo no historico
              (grava pedidos de mentira; precisa do python no PATH); sem ela esses testes sao pulados.

.EXAMPLE
    .\test-lote-grupos.ps1 -DataDir C:\temp\loja-dados
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0

function Call($method, $path, $body = $null) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 8)); $p.ContentType = 'application/json; charset=utf-8' }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        if ($r.Headers['Content-Type'] -like 'application/json*') { $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json }
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json; Raw = $r.RawContentStream.ToArray() }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $text = (New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd()
        $json = try { $text | ConvertFrom-Json } catch { $null }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $json; Raw = @() }
    }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}
function Skip($name, $why) { Write-Output "PULOU $name ($why)"; $script:skips++ }
function Items($b) { @($b | Where-Object { $null -ne $_ }) }
function Enc($s) { [uri]::EscapeDataString($s) }
function Names($id) { ((@(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $id }).groupRefs | ForEach-Object { $_.name } | Sort-Object) -join ',' }
function Bulk($ids, $add, $remove) { Call POST '/api/admin/apps/bulk-groups' @{ appIds = @($ids); add = @($add); remove = @($remove) } }
function Ref($g) { @{ name = $g.name; sid = $g.sid } }
function Audit($q) { , @(Items (Call GET "/api/admin/audit?$q&size=200").Body.items) }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$created = @()
try {
    $todos = @(Items (Call GET '/api/admin/groups?q=LOJA-Todos').Body) | Where-Object name -eq 'LOJA-Todos' | Select-Object -First 1
    $admins = @(Items (Call GET '/api/admin/groups?q=LOJA-Admins').Body) | Where-Object name -eq 'LOJA-Admins' | Select-Object -First 1
    if (-not $todos -or -not $admins) {
        Skip 'tudo' 'LOJA-Todos e LOJA-Admins nao existem no AD (ou o AD nao respondeu)'
        Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
        exit $script:fails
    }
    $rTodos = Ref $todos; $rAdmins = Ref $admins

    foreach ($pkg in '7zip', 'vlc', 'notepadplusplus') {
        $a = Call POST '/api/admin/apps' @{ packageId = $pkg; source = 'community'; groups = @('LOJA-Todos') }
        if ($a.Body.id) { $created += $a.Body.id } else { Write-Output "AVISO nao criei $pkg : $($a.Body.message)" }
    }
    Check 'preparo: 3 apps criados com LOJA-Todos' ($created.Count -eq 3)
    $one, $two, $three = $created

    # ---------- Acrescentar em lote ----------
    $r = Bulk $created @($rAdmins) @()
    Check 'lote: acrescentar LOJA-Admins a 3 apps -> 200' ($r.Code -eq 200 -and @($r.Body.updated).Count -eq 3) $r.Body.message
    Check '... os 3 ficaram com os dois grupos' ((Names $one) -eq 'LOJA-Admins,LOJA-Todos' -and (Names $two) -eq 'LOJA-Admins,LOJA-Todos' -and (Names $three) -eq 'LOJA-Admins,LOJA-Todos')
    $before = @($r.Body.before)
    Check '... a resposta traz como estavam (para o Desfazer)' ($before.Count -eq 3 -and @($before[0].groups).Count -eq 1 -and @($before[0].groups)[0].name -eq 'LOJA-Todos')
    $r2 = Bulk $created @($rAdmins) @()
    Check 'repetir o mesmo lote: nada muda (3 sem mudanca)' ($r2.Code -eq 200 -and @($r2.Body.updated).Count -eq 0 -and @($r2.Body.unchanged).Count -eq 3)

    # ---------- Auditoria do lote ----------
    $aud = Audit 'kind=bulk'
    Check 'auditoria: uma linha por app alterado, com o mesmo lote' ($aud.Count -eq 3 -and @($aud | ForEach-Object batch | Sort-Object -Unique).Count -eq 1) "linhas=$($aud.Count)"
    Check 'auditoria: antes e depois' ($aud[0].oldValue -eq 'LOJA-Todos' -and $aud[0].newValue -eq 'LOJA-Admins, LOJA-Todos')

    # ---------- Desfazer ----------
    $items = @($before | ForEach-Object { @{ appId = $_.appId; groups = @($_.groups) } })
    $u = Call POST '/api/admin/apps/restore-groups' @{ items = $items }
    Check 'Desfazer: restaura os grupos de antes -> 200' ($u.Code -eq 200 -and (Names $one) -eq 'LOJA-Todos' -and (Names $three) -eq 'LOJA-Todos') $u.Body.message
    $undo = Audit 'kind=undo'
    Check 'Desfazer fica na auditoria como revertido' ($undo.Count -eq 3 -and -not ($undo | Where-Object { -not $_.undo }))
    $bad = Call POST '/api/admin/apps/restore-groups' @{ items = @(@{ appId = $one; groups = @(@{ name = 'Fantasma'; sid = 'S-1-5-21-1-2-3-99999' }) }) }
    Check 'Desfazer com grupo inexistente -> 400 e nada muda' ($bad.Code -eq 400 -and (Names $one) -eq 'LOJA-Todos')
    Check 'Desfazer sem itens -> 400' ((Call POST '/api/admin/apps/restore-groups' @{ items = @() }).Code -eq 400)

    # ---------- Atomicidade e recusas ----------
    $null = Bulk @($one) @($rAdmins) @()      # $one: Admins + Todos; $two e $three: so Todos
    $r = Bulk @($one, $two) @() @($rTodos)
    Check 'tirar LOJA-Todos deixaria o app 2 sem grupo -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*sem nenhum grupo*') $r.Body.message
    Check '... atomico: o app 1 (que podia perder o grupo) continua igual' ((Names $one) -eq 'LOJA-Admins,LOJA-Todos' -and (Names $two) -eq 'LOJA-Todos')
    $r = Bulk @($one, $two) @(@{ name = 'LOJA-NaoExiste-zzz'; sid = $null }) @()
    Check 'acrescentar grupo inexistente -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*LOJA-NaoExiste-zzz*') $r.Body.message
    Check '... atomico: ninguem mudou' ((Names $one) -eq 'LOJA-Admins,LOJA-Todos' -and (Names $two) -eq 'LOJA-Todos')
    $r = Bulk @($one, 999999) @($rAdmins) @()
    Check 'um aplicativo que nao existe na lista -> 400 e nada muda' ($r.Code -in 400, 404 -and (Names $one) -eq 'LOJA-Admins,LOJA-Todos')
    Check 'sem aplicativos -> 400' ((Bulk @() @($rAdmins) @()).Code -eq 400)
    Check 'sem grupos -> 400' ((Bulk @($one) @() @()).Code -eq 400)
    $r = Bulk @($one) @() @($rAdmins)
    Check 'tirar um grupo que o app tem -> 200' ($r.Code -eq 200 -and (Names $one) -eq 'LOJA-Todos')
    $r = Bulk @($one) @() @($rAdmins)
    Check 'tirar um grupo que o app nao tem -> sem mudanca' ($r.Code -eq 200 -and @($r.Body.unchanged).Count -eq 1)

    # ---------- "Todos os usuarios" ----------
    $r = Bulk @($one) @(@{ name = '*'; sid = $null }) @()
    Check 'acrescentar "Todos os usuarios" deixa so "*"' ($r.Code -eq 200 -and (Names $one) -eq '*')
    $r = Bulk @($one) @($rAdmins) @()
    Check 'grupo especifico num app de "Todos" nao entra (seria tirar acesso)' ($r.Code -eq 200 -and @($r.Body.unchanged).Count -eq 1 -and (Names $one) -eq '*')
    $r = Bulk @($one) @() @(@{ name = '*'; sid = $null })
    Check 'tirar "Todos os usuarios" do unico grupo -> 400' ($r.Code -eq 400)

    # ---------- Tela Grupos ----------
    $null = Bulk @($one, $two) @($rAdmins) @()  # $one volta a ter "*"? ($one e "*": nao muda) ; $two ganha Admins
    $d = Call GET "/api/admin/groups/$($admins.sid)"
    Check 'GET /groups/{sid} -> 200 com nome, SID e descricao' ($d.Code -eq 200 -and $d.Body.group.name -eq 'LOJA-Admins' -and $d.Body.group.sid -eq $admins.sid)
    $liber = @($d.Body.apps)
    Check 'apps liberados: o que tem o grupo vem como "group"' ((@($liber | Where-Object { $_.id -eq $two -and $_.origin -eq 'group' })).Count -eq 1)
    Check 'apps liberados para todos aparecem como "everyone"' ((@($liber | Where-Object { $_.id -eq $one -and $_.origin -eq 'everyone' })).Count -eq 1)
    Check 'sem liberacao: o que nao chega ao grupo' ((@($d.Body.withoutAccess | Where-Object { $_.id -eq $three })).Count -eq 1)
    Check 'membros diretos listados (LOJA-Admins tem pessoas)' ($d.Body.membersListed -eq $true -and @($d.Body.members).Count -ge 1 -and $d.Body.members[0].login)
    $t = Call GET "/api/admin/groups/$($todos.sid)"
    Check 'grupo aninhado: LOJA-Todos mostra os apps que tem o grupo' ((@($t.Body.apps | Where-Object { $_.id -eq $three -and $_.origin -eq 'group' })).Count -eq 1)
    $domain = @(Items (Call GET '/api/admin/groups?all=true&q=Domain').Body) | Where-Object { $_.sid -like '*-513' } | Select-Object -First 1
    if ($domain) {
        $du = Call GET "/api/admin/groups/$($domain.sid)"
        Check 'grupo primario (Domain Users): nao lista membros' ($du.Code -eq 200 -and $du.Body.membersListed -eq $false)
        if (@($du.Body.parentGroups | Where-Object name -eq 'LOJA-Todos').Count -gt 0) {
            Check 'Domain Users dentro de LOJA-Todos: os apps de LOJA-Todos chegam como "nested"' ((@($du.Body.apps | Where-Object { $_.id -eq $three -and $_.origin -eq 'nested' -and $_.via -eq 'LOJA-Todos' })).Count -eq 1)
        } else { Skip 'Domain Users aninhado em LOJA-Todos' 'nao esta aninhado neste AD' }
    } else { Skip 'grupo primario' 'Domain Users nao apareceu na busca' }
    Check 'SID que nao existe -> 404' ((Call GET '/api/admin/groups/S-1-5-21-1-2-3-99999').Code -eq 404)
    Check 'SID mal formado -> 404' ((Call GET '/api/admin/groups/lixo').Code -eq 404)

    # ---------- Filtro por grupo do usuario ----------
    $me = (Call GET '/api/admin/info').Body.user -replace '^.*\\', ''
    $other = @(@(Items (Call GET '/api/admin/users?q=ma').Body) | Where-Object { $_.login -ne $me } | Select-Object -First 2)
    $inAdmins = @($d.Body.members | ForEach-Object login)
    if ($DataDir -and (Test-Path (Join-Path $DataDir 'loja.db')) -and (Get-Command python -ErrorAction SilentlyContinue) -and @($other).Count -ge 1) {
        $py = Join-Path $env:TEMP 'loja-test-lote.py'
        Set-Content -Path $py -Encoding UTF8 -Value @'
import sqlite3, sys, datetime
db, bs = sys.argv[1], chr(92)
c = sqlite3.connect(db, timeout=10)
now = datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%S.0000000Z')
for login in sys.argv[2:]:
    for host in ('PC-GRUPO-1', 'PC-GRUPO-2'):
        c.execute("insert into requests (created_utc,finished_utc,username,hostname,agent_id,app_id,package_id,title,source,action,status,message,exit_code,output) values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            (now, now, 'CORP' + bs + login, host, 'mock', None, '7zip', '7-Zip', 'community', 'install', 'success', 'Instalado com sucesso.', 0, 'x'))
c.commit()
'@
        $logins = @($me) + @($other | ForEach-Object login) + @('pessoa.que.nao.existe')
        python -I $py (Join-Path $DataDir 'loja.db') @logins
        Remove-Item $py -ErrorAction SilentlyContinue

        $all = Call GET '/api/admin/history?host=PC-GRUPO-1&size=100'
        $inAdminsMe = ($inAdmins -contains $me)
        $f = Call GET "/api/admin/history?host=PC-GRUPO-1&size=100&group=$(Enc $admins.sid)"
        $users = @(Items $f.Body.items | ForEach-Object { ($_.user -replace '^.*\\', '') })
        Check 'filtro por grupo do usuario -> 200' ($f.Code -eq 200) "usuarios=$($users -join ',')"
        Check '... so traz pessoas que estao no grupo (e existem no AD)' (-not ($users | Where-Object { $_ -eq 'pessoa.que.nao.existe' }) -and $f.Body.total -le $all.Body.total)
        $everyone = Call GET "/api/admin/history?host=PC-GRUPO-1&size=100&group=$(Enc '*')"
        Check 'grupo "*" traz todo mundo (inclusive quem nao existe no AD)' ($everyone.Body.total -eq $all.Body.total)
        $nobody = Call GET "/api/admin/history?host=PC-GRUPO-1&group=$(Enc 'S-1-5-21-1-2-3-99999')"
        Check 'grupo sem membros -> lista vazia' ($nobody.Code -eq 200 -and $nobody.Body.total -eq 0)
        Check 'grupo invalido -> 400' ((Call GET '/api/admin/history?group=lixo').Code -eq 400)
        $csv = Call GET "/api/admin/history.csv?host=PC-GRUPO-1&group=$(Enc $admins.sid)"
        $lines = @([Text.Encoding]::UTF8.GetString($csv.Raw).TrimStart([char]0xFEFF) -split "`r?`n" | Where-Object { $_ })
        Check 'o CSV respeita o mesmo filtro de grupo' ($csv.Code -eq 200 -and ($lines.Count - 1) -eq $f.Body.total) "linhas=$($lines.Count - 1)"
        $combo = Call GET "/api/admin/history?host=PC-GRUPO-1&status=failed&group=$(Enc $admins.sid)"
        Check 'grupo combinado com outro filtro (situacao) -> 200' ($combo.Code -eq 200 -and $combo.Body.total -eq 0)
    } else {
        Skip 'filtro de grupo no historico' 'passe -DataDir com a pasta do loja.db, tenha o python no PATH e ao menos uma pessoa no AD'
    }

    # ---------- Maquinas ----------
    $m = Call GET "/api/admin/machines?group=$(Enc $admins.sid)"
    Check 'GET /machines?group= -> 200' ($m.Code -eq 200)
    Check '... grupo sem ninguem -> lista vazia' (@(Items (Call GET "/api/admin/machines?group=$(Enc 'S-1-5-21-1-2-3-99999')").Body).Count -eq 0)
    Check '... grupo invalido -> 400' ((Call GET '/api/admin/machines?group=lixo').Code -eq 400)
}
finally {
    foreach ($x in $created) { try { Call DELETE "/api/admin/apps/$x" | Out-Null } catch { Write-Output "AVISO nao consegui apagar o app $x" } }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
