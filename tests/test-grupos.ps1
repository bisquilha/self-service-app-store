<#
.SYNOPSIS
    Testa a busca de grupos do AD e a validacao de grupos ao salvar apps (servidor 1.4.1+).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development: Tactical simulado, AdminGroups "*").
    O PC precisa estar no dominio para as checagens que consultam o AD de verdade.

    -DataDir  pasta de dados do servidor (a que tem o loja.db). Opcional: sem ela, o teste do
              "grupo antigo" (grupo salvo que depois sumiu do AD) e pulado.
    Usa o pacote notepadplusplus (internet). Os apps criados aqui sao apagados no fim.

.EXAMPLE
    .\test-grupos.ps1 -DataDir C:\temp\loja-dados
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0

function Call($method, $path, $body = $null) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
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
function Skip($name, $why) { Write-Output "PULOU $name ($why)"; $script:skips++ }
# No PowerShell 5.1, @($null).Count e 1 e um array JSON vazio vira $null: sempre passar por aqui.
function Items($b) { @($b | Where-Object { $null -ne $_ }) }
function Enc($s) { [uri]::EscapeDataString($s) }
function GetApp($id) { @(Items (Call GET '/api/admin/apps').Body) | Where-Object { $_.id -eq $id } }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$created = @()
try {
    # ---------- Busca de grupos ----------
    $g = Call GET '/api/admin/groups'
    $adOk = ($g.Code -eq 200)
    Check 'GET /groups responde (200; 502 = AD inacessivel)' ($g.Code -in 200, 502) "codigo=$($g.Code)"
    $list = @(Items $g.Body)
    $first = $list | Select-Object -First 1

    if ($adOk) {
        Write-Output "INFO  grupos com o prefixo da loja: $($list.Count) ($((@($list | ForEach-Object name) -join ', ')))"
        $bad = @($list | Where-Object { $_.name -notlike 'LOJA-*' })
        Check 'lista padrao so traz LOJA-*' ($bad.Count -eq 0) $(if ($bad.Count) { "fora do prefixo: $($bad.name -join ', ')" })
        if ($first) {
            Check 'item tem nome, SID e membros' ($first.name -and $first.sid -like 'S-1-5-21-*' -and ($first.PSObject.Properties.Name -contains 'memberCount')) "$($first.name) $($first.sid) membros=$($first.memberCount)"
            $mid = $first.name.Substring(5, [Math]::Min(3, $first.name.Length - 5))
            $r = Call GET "/api/admin/groups?q=$(Enc $mid)"
            Check 'filtro por pedaco do nome' ((@(Items $r.Body) | Where-Object name -eq $first.name)) "q=$mid"
        } else { Skip 'item, filtro' 'nenhum grupo com o prefixo LOJA- no AD' }

        $r = Call GET '/api/admin/groups?all=true&q=a'
        Check 'all=true exige 2 letras' ($r.Code -eq 200 -and @(Items $r.Body).Count -eq 0)
        $r = Call GET '/api/admin/groups?all=true&q=LOJA'
        Check 'all=true procura no dominio todo' ($r.Code -eq 200 -and @(Items $r.Body).Count -ge $list.Count) "$(@(Items $r.Body).Count) grupos"
        $evil = '*)(objectClass=*'
        $r = Call GET ('/api/admin/groups?q=' + (Enc $evil))
        Check 'caracteres de filtro LDAP sao escapados' ($r.Code -eq 200 -and @(Items $r.Body).Count -eq 0) "codigo=$($r.Code)"

        $prim = $null
        foreach ($term in 'Domain', 'dom', 'Usu', 'users') {
            $r = Call GET "/api/admin/groups?all=true&q=$term"
            $prim = @(Items $r.Body) | Where-Object { $_.sid -like '*-513' } | Select-Object -First 1
            if ($prim) { break }
        }
        if ($prim) { Check 'Domain Users: membros nao informados (grupo primario)' ($null -eq $prim.memberCount) "$($prim.name)" }
        else { Skip 'Domain Users sem contagem' 'grupo -513 nao apareceu nas buscas' }
    } else {
        Skip 'checagens que dependem do AD' 'servidor nao alcancou o AD (502)'
    }

    # ---------- Validacao ao salvar ----------
    $r = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('LOJA-NaoExiste-zzz') }
    Check 'POST com grupo inexistente -> 400' ($r.Code -eq 400) $r.Body.message
    Check 'mensagem cita o grupo' ($r.Body.message -like '*LOJA-NaoExiste-zzz*')
    $r = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('*', 'LOJA-NaoExiste-zzz') }
    Check 'POST com "*" + grupo inexistente -> 400' ($r.Code -eq 400)
    $r = Call GET '/api/admin/apps'
    Check 'nada foi salvo nos casos recusados' (-not (@(Items $r.Body) | Where-Object packageId -eq 'notepadplusplus'))

    $a = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('*') }
    Check 'POST com "*" -> 200' ($a.Code -eq 200) "id=$($a.Body.id)"
    $id = $a.Body.id
    if ($id) { $created += $id }
    Check 'app novo nao tem grupo faltando' (@(Items $a.Body.missingGroups).Count -eq 0)

    $r = Call PUT "/api/admin/apps/$id" @{ groups = @('*', 'LOJA-NovoInvalido'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'PUT acrescentando grupo inexistente -> 400' ($r.Code -eq 400) $r.Body.message
    Check 'grupos continuam como antes' ((@((GetApp $id).groups) -join ',') -eq '*')

    # Nomes de grupos embutidos do Windows mudam com o idioma (Guests x Convidados): usa um grupo LOJA-* real do AD.
    if ($first) {
        $r = Call PUT "/api/admin/apps/$id" @{ groups = @('*', $first.name); enabled = $true; autoUpdate = $false; allowUninstall = $true }
        Check "PUT acrescentando grupo existente ($($first.name)) -> 200" ($r.Code -eq 200) $r.Body.message
        Check 'grupo existente nao e marcado como faltando' (@(Items $r.Body.missingGroups).Count -eq 0)
    } else { Skip 'PUT com grupo existente pelo nome' 'nenhum grupo LOJA-* no AD para usar' }
    $r = Call PUT "/api/admin/apps/$id" @{ groups = @('*', 'S-1-5-32-546'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'PUT com grupo por SID -> 200' ($r.Code -eq 200)

    # ---------- Grupo salvo que depois sumiu do AD ----------
    $old = 'LOJA-Antigo-Renomeado'
    if ($DataDir -and (Test-Path (Join-Path $DataDir 'loja.db')) -and (Get-Command python -ErrorAction SilentlyContinue)) {
        $db = Join-Path $DataDir 'loja.db'
        python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1], timeout=10); c.execute('INSERT OR IGNORE INTO app_groups (app_id, group_name) VALUES (?,?)', (int(sys.argv[2]), sys.argv[3])); c.commit()" $db $id $old
        $app = GetApp $id
        Check 'GET /apps marca o grupo que sumiu' (@($app.missingGroups) -contains $old) "faltando=$(@($app.missingGroups) -join ',')"
        Check '"*" nunca e marcado' (-not (@($app.missingGroups) -contains '*'))

        $keep = @($app.groups)
        $r = Call PUT "/api/admin/apps/$id" @{ groups = $keep; enabled = $false; autoUpdate = $false; allowUninstall = $true }
        Check 'ligar/desligar switch continua funcionando com grupo antigo' ($r.Code -eq 200 -and $r.Body.enabled -eq $false) "codigo=$($r.Code) $($r.Body.message)"
        $r = Call PUT "/api/admin/apps/$id" @{ groups = $keep; enabled = $true; autoUpdate = $false; allowUninstall = $true }

        $r = Call PUT "/api/admin/apps/$id" @{ groups = ($keep + 'LOJA-NovoInvalido'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
        Check 'acrescentar grupo inexistente junto do antigo -> 400' ($r.Code -eq 400) $r.Body.message
        Check 'a mensagem cita so o grupo novo' ($r.Body.message -like '*LOJA-NovoInvalido*' -and $r.Body.message -notlike "*$old*")

        $fixed = @($keep | Where-Object { $_ -ne $old })
        $r = Call PUT "/api/admin/apps/$id" @{ groups = $fixed; enabled = $true; autoUpdate = $false; allowUninstall = $true }
        Check 'remover o grupo antigo -> 200 e some do aviso' ($r.Code -eq 200 -and @(Items $r.Body.missingGroups).Count -eq 0)
    } else {
        Skip 'grupo antigo (salvo e depois removido do AD)' 'passe -DataDir com a pasta do loja.db e tenha o python no PATH'
    }
}
finally {
    foreach ($x in $created) { try { Call DELETE "/api/admin/apps/$x" | Out-Null } catch { Write-Output "AVISO nao consegui apagar o app $x" } }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
