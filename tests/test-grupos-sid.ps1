<#
.SYNOPSIS
    Testa os grupos do AD guardados por SID + nome (servidor 1.6.0+).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development) e uma pasta de dados NOVA, no PC do dominio.
    Usa o pacote notepadplusplus (internet) e os grupos reais LOJA-Todos e LOJA-Admins. Os apps criados aqui sao apagados no fim.

    -DataDir  pasta de dados do servidor (a que tem o loja.db). Obrigatorio para os testes de migracao e de renomear grupo
              (precisa do python no PATH); sem ela esses testes sao pulados.

.EXAMPLE
    .\test-grupos-sid.ps1 -DataDir C:\temp\loja-dados
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0

function Call($method, $path, $body = $null) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 6)); $p.ContentType = 'application/json; charset=utf-8' }
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
function Enc($s) { [uri]::EscapeDataString($s) }
function GetApp($id) { @(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $id } }
function Db($sql, $params = @()) {
    $py = Join-Path $env:TEMP 'loja-test-sid.py'
    Set-Content -Path $py -Encoding UTF8 -Value @'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1], timeout=10)
rows = c.execute(sys.argv[2], tuple(sys.argv[3:])).fetchall()
c.commit()
for r in rows: print('|'.join('' if v is None else str(v) for v in r))
'@
    $out = python -I $py (Join-Path $DataDir 'loja.db') $sql @params
    Remove-Item $py -ErrorAction SilentlyContinue
    # A virgula impede o PowerShell de "desembrulhar" uma unica linha: sem ela, (Db ...)[0] seria o primeiro CARACTERE.
    , @($out)
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }
$hasDb = $DataDir -and (Test-Path (Join-Path $DataDir 'loja.db')) -and (Get-Command python -ErrorAction SilentlyContinue)

$created = @()
try {
    $todos = @(Items (Call GET '/api/admin/groups?q=LOJA-Todos').Body) | Where-Object name -eq 'LOJA-Todos' | Select-Object -First 1
    $admins = @(Items (Call GET '/api/admin/groups?q=LOJA-Admins').Body) | Where-Object name -eq 'LOJA-Admins' | Select-Object -First 1
    if (-not $todos -or -not $admins) {
        Skip 'tudo' 'os grupos LOJA-Todos e LOJA-Admins nao existem no AD (ou o AD nao respondeu)'
        Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
        exit $script:fails
    }
    Write-Output "INFO  LOJA-Todos = $($todos.sid) ; LOJA-Admins = $($admins.sid)"

    # ---------- Criar guardando o SID ----------
    $a = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('loja-todos') }
    Check 'POST so com nomes -> 200' ($a.Code -eq 200) $a.Body.message
    $id = $a.Body.id
    if ($id) { $created += $id }
    $ref = @($a.Body.groupRefs)[0]
    Check 'o SID do grupo foi gravado' ($ref.sid -eq $todos.sid) "sid=$($ref.sid)"
    Check 'o nome vira o do AD (maiusculas certas)' ($ref.name -ceq 'LOJA-Todos') "nome=$($ref.name)"
    Check 'o campo antigo "groups" continua com os nomes' ((@($a.Body.groups) -join ',') -ceq 'LOJA-Todos')

    # ---------- PUT com groupRefs (painel novo) e com nomes (app 1.2.0) ----------
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = @(@{ name = 'nome-que-o-painel-mandou'; sid = $admins.sid }, @{ name = 'LOJA-Todos'; sid = $todos.sid }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'PUT com groupRefs (SID manda, nome vem do AD) -> 200' ($r.Code -eq 200) $r.Body.message
    $names = @($r.Body.groupRefs | ForEach-Object { "$($_.name):$($_.sid)" } | Sort-Object)
    Check 'os dois grupos ficaram com o nome certo do AD' ($names -contains "LOJA-Admins:$($admins.sid)" -and $names -contains "LOJA-Todos:$($todos.sid)") ($names -join ' ')
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @('LOJA-Todos'); enabled = $true; autoUpdate = $true; allowUninstall = $true }
    Check 'PUT no formato do app 1.2.0 (so nomes) -> 200' ($r.Code -eq 200 -and @($r.Body.groupRefs).Count -eq 1) $r.Body.message
    Check 'no formato antigo o SID tambem e gravado' (@($r.Body.groupRefs)[0].sid -eq $todos.sid)

    # ---------- Recusas ----------
    $fake = 'S-1-5-21-111111111-222222222-333333333-99999'
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = @(@{ name = 'Fantasma'; sid = $fake }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'SID que nao existe no AD -> 400' ($r.Code -eq 400) $r.Body.message
    Check 'a mensagem cita o grupo' ($r.Body.message -like '*Fantasma*')
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @('*', 'LOJA-NaoExiste-zzz'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check '"*" junto de grupo inexistente -> 400' ($r.Code -eq 400)
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = @(); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'sem nenhum grupo -> 400' ($r.Code -eq 400)
    Check 'os grupos continuam os de antes' ((@((GetApp $id).groupRefs)[0]).sid -eq $todos.sid)

    $r = Call PUT "/api/admin/apps/$id" @{ groups = @('*'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    $everyone = @($r.Body.groupRefs)[0]
    Check '"*" (todos os usuarios) fica sem SID' ($r.Code -eq 200 -and $everyone.name -eq '*' -and -not $everyone.sid)
    Check '"*" nunca aparece como grupo faltando' (@($r.Body.missingGroups).Count -eq 0)

    # ---------- Migracao e renomear (mexem no banco) ----------
    if ($hasDb) {
        $null = Call PUT "/api/admin/apps/$id" @{ groups = @('LOJA-Todos'); enabled = $true; autoUpdate = $false; allowUninstall = $true }

        # grupo salvo na 1.5.0: so o nome, sem SID
        Db 'UPDATE app_groups SET group_sid = NULL WHERE app_id = ?' @($id) | Out-Null
        Check 'preparo: linha sem SID' ((Db 'SELECT COUNT(*) FROM app_groups WHERE app_id = ? AND group_sid IS NULL' @($id))[0] -eq '1')
        $app = GetApp $id
        Check 'ao listar, o SID do grupo antigo e preenchido' (@($app.groupRefs)[0].sid -eq $todos.sid) "sid=$(@($app.groupRefs)[0].sid)"
        Check '... e gravado no banco' ((Db 'SELECT group_sid FROM app_groups WHERE app_id = ?' @($id))[0] -eq $todos.sid)

        # grupo renomeado no AD: o banco tem o nome velho e o SID certo
        Db "UPDATE app_groups SET group_name = 'LOJA-Nome-Antigo' WHERE app_id = ?" @($id) | Out-Null
        $app = GetApp $id
        Check 'renomeado: o painel mostra o nome ATUAL do AD' (@($app.groupRefs)[0].name -ceq 'LOJA-Todos') "nome=$(@($app.groupRefs)[0].name)"
        Check 'renomeado: nao e marcado como grupo faltando' (@($app.missingGroups).Count -eq 0)
        Check 'renomeado: o nome novo e gravado no banco' ((Db 'SELECT group_name FROM app_groups WHERE app_id = ?' @($id))[0] -ceq 'LOJA-Todos')

        # grupo salvo so com nome que nao existe: continua aparecendo como faltando
        Db "INSERT INTO app_groups (app_id, group_name) VALUES (?, 'LOJA-Apagado-Do-AD')" @($id) | Out-Null
        $app = GetApp $id
        Check 'nome sem SID e inexistente -> marcado como faltando' (@($app.missingGroups) -contains 'LOJA-Apagado-Do-AD') "faltando=$(@($app.missingGroups) -join ',')"
        $keep = @($app.groupRefs | ForEach-Object { @{ name = $_.name; sid = $_.sid } })
        $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = $keep; enabled = $false; autoUpdate = $false; allowUninstall = $true }
        Check 'o grupo apagado do AD nao impede ligar/desligar switch' ($r.Code -eq 200 -and $r.Body.enabled -eq $false) $r.Body.message
        $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = @(@{ name = 'LOJA-Todos'; sid = $todos.sid }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
        Check 'tirar o grupo apagado -> 200 e some do aviso' ($r.Code -eq 200 -and @($r.Body.missingGroups).Count -eq 0)

        # o mesmo grupo duas vezes (uma por nome, uma por SID) vira uma linha so
        $r = Call PUT "/api/admin/apps/$id" @{ groups = @(); groupRefs = @(@{ name = 'LOJA-Todos'; sid = $todos.sid }, @{ name = 'loja-todos'; sid = $todos.sid }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
        Check 'o mesmo SID repetido vira um grupo so' ($r.Code -eq 200 -and @($r.Body.groupRefs).Count -eq 1)
    } else {
        Skip 'migracao e renomear grupo' 'passe -DataDir com a pasta do loja.db e tenha o python no PATH'
    }
}
finally {
    foreach ($x in $created) { try { Call DELETE "/api/admin/apps/$x" | Out-Null } catch { Write-Output "AVISO nao consegui apagar o app $x" } }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
