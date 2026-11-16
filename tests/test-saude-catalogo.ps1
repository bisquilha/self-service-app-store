<#
.SYNOPSIS
    Testa GET /api/admin/catalog-health (servidor 1.6.0+): aplicativo ativo sem grupo, grupo que nao existe no AD,
    grupo sem membros diretos e aplicativos desatualizados em muitas maquinas (o criterio exato).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (Development) e uma pasta de dados NOVA, no PC do dominio (usa o AD de verdade,
    so para LER grupos). Precisa do python no PATH e de -DataDir (monta aplicativos e inventarios direto no banco).
    Usa os pacotes 7zip, vlc, putty e firefox (internet). Os aplicativos criados aqui sao apagados no fim.

    O caso "grupo sem membros diretos" usa um grupo LOJA-* do AD que tenha 0 membros diretos; se nao houver, esse teste e pulado.
    O caso "o AD nao respondeu" nao tem como ser simulado aqui: fica coberto pela leitura do codigo (Check devolve Unavailable
    so para erros que nao sao "nao encontrado") e esta documentado.

.EXAMPLE
    .\test-saude-catalogo.ps1 -DataDir C:\temp\loja-dados
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
    python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1], timeout=10); c.execute(sys.argv[2], tuple(sys.argv[3:])); c.commit()" (Join-Path $DataDir 'loja.db') $sql $a
}
function Health() { (Call GET '/api/admin/catalog-health').Body }
function IssuesOf($h, $kind) { @(Items $h.issues | Where-Object { $_.kind -eq $kind }) }
function Hosts($pkg, $outdated, $current, $oldVersion, $newVersion, $prefix) {
    for ($i = 1; $i -le $outdated; $i++) {
        $h = "$prefix-OLD-$i"
        Sql 'INSERT OR REPLACE INTO host_inventory (hostname, updated_utc) VALUES (?, ?)' @($h, '2026-10-01T10:00:00.0000000Z')
        Sql 'INSERT OR REPLACE INTO host_packages (hostname, package_id, version) VALUES (?,?,?)' @($h, $pkg, $oldVersion)
    }
    for ($i = 1; $i -le $current; $i++) {
        $h = "$prefix-OK-$i"
        Sql 'INSERT OR REPLACE INTO host_inventory (hostname, updated_utc) VALUES (?, ?)' @($h, '2026-10-01T10:00:00.0000000Z')
        Sql 'INSERT OR REPLACE INTO host_packages (hostname, package_id, version) VALUES (?,?,?)' @($h, $pkg, $newVersion)
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
    $h0 = Health
    Check 'catalogo vazio: sem problemas' (@(Items $h0.issues).Count -eq 0 -and $h0.appsChecked -eq 0) "apps=$($h0.appsChecked)"
    Check 'a resposta diz o criterio de "desatualizado em muitas maquinas"' ($h0.outdatedRule -like '*5 ou mais*' -and $h0.outdatedRule -like '*20*')

    $todos = @(Items (Call GET '/api/admin/groups?q=LOJA-Todos').Body) | Where-Object name -eq 'LOJA-Todos' | Select-Object -First 1
    if (-not $todos) {
        Skip 'tudo' 'LOJA-Todos nao existe no AD (ou o AD nao respondeu)'
        Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
        exit $script:fails
    }

    $apps = @{}
    foreach ($pkg in '7zip', 'vlc', 'putty', 'firefox') {
        $a = Call POST '/api/admin/apps' @{ packageId = $pkg; source = 'community'; groups = @('LOJA-Todos') }
        if ($a.Body.id) { $created += $a.Body.id; $apps[$pkg] = $a.Body } else { Write-Output "AVISO nao criei $pkg : $($a.Body.message)" }
    }
    Check 'preparo: 4 apps criados com um grupo que existe' ($apps.Count -eq 4)
    $h1 = Health
    Check 'tudo certo quando os grupos existem e nao ha atraso' (@(Items $h1.issues).Count -eq 0 -and $h1.appsChecked -eq 4 -and $h1.adAvailable -eq $true) "issues=$(@(Items $h1.issues).Count)"

    # ---------- Ativo sem grupo ----------
    $now = '2026-10-21T10:00:00.0000000Z'
    Sql "INSERT INTO apps (package_id, source, title, enabled, auto_update, created_utc, updated_utc) VALUES ('sem-grupo-teste', 'community', 'Sem Grupo Teste', 1, 0, ?, ?)" @($now, $now)
    Sql "INSERT INTO apps (package_id, source, title, enabled, auto_update, created_utc, updated_utc) VALUES ('desativado-sem-grupo', 'community', 'Desativado Sem Grupo', 0, 0, ?, ?)" @($now, $now)
    $h = Health
    $noGroup = @(IssuesOf $h 'no-group')
    Check 'ativo sem grupo: aparece como erro' ($noGroup.Count -eq 1 -and $noGroup[0].title -eq 'Sem Grupo Teste' -and $noGroup[0].severity -eq 'error')
    Check '... aplicativo DESATIVADO sem grupo nao entra' (-not ($noGroup | Where-Object { $_.title -eq 'Desativado Sem Grupo' }))

    # ---------- Grupo que nao existe no AD ----------
    $ghost = Call POST '/api/admin/apps' @{ packageId = 'sem-grupo-teste'; source = 'community'; groups = @('LOJA-Fantasma-Inexistente') }
    Check 'o painel recusa criar app com grupo inexistente (400)' ($ghost.Code -eq 400)
    $idGhost = (Call GET '/api/admin/apps').Body | Where-Object { $_.packageId -eq 'sem-grupo-teste' } | Select-Object -First 1 -ExpandProperty id
    Sql 'INSERT OR IGNORE INTO app_groups (app_id, group_name) VALUES (?, ?)' @("$idGhost", 'LOJA-Fantasma-Inexistente')
    $h = Health
    $missing = @(IssuesOf $h 'group-missing')
    Check 'grupo salvo que sumiu do AD: erro "group-missing" com o nome do grupo' ($missing.Count -eq 1 -and $missing[0].severity -eq 'error' -and $missing[0].message -like '*LOJA-Fantasma-Inexistente*' -and $missing[0].title -eq 'Sem Grupo Teste')
    Check '... o AD respondeu, entao nada fica "nao conferido"' (@(IssuesOf $h 'group-unverified').Count -eq 0 -and $h.adAvailable -eq $true)
    Check '... e o app agora tem grupo, entao deixa de ser "sem grupo"' (@(IssuesOf $h 'no-group').Count -eq 0)

    # ---------- Grupo sem membros diretos ----------
    $all = @(Items (Call GET '/api/admin/groups?q=LOJA').Body)
    $empty = $all | Where-Object { $null -ne $_.memberCount -and $_.memberCount -eq 0 } | Select-Object -First 1
    if ($empty) {
        $emptyApp = $apps['putty']
        $null = Call PUT "/api/admin/apps/$($emptyApp.id)" @{ groups = @($empty.name); groupRefs = @(@{ name = $empty.name; sid = $empty.sid }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
        $h = Health
        $ge = @(IssuesOf $h 'group-empty')
        Check 'grupo com 0 membros diretos: aviso "group-empty"' ($ge.Count -ge 1 -and $ge[0].severity -eq 'warning' -and $ge[0].message -like "*$($empty.name)*") $empty.name
        Check '... o aviso nao afirma que ninguem tem acesso (diz que nao prova)' ($ge[0].message -like '*n?o prova*')
        $null = Call PUT "/api/admin/apps/$($emptyApp.id)" @{ groups = @('LOJA-Todos'); groupRefs = @(@{ name = $todos.name; sid = $todos.sid }); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    } else {
        Skip 'grupo com 0 membros diretos' 'nenhum grupo LOJA-* com 0 membros diretos no AD'
    }
    $h = Health
    Check 'grupo com membros (LOJA-Todos): sem aviso de vazio' (-not (IssuesOf $h 'group-empty' | Where-Object { $_.groupName -eq 'LOJA-Todos' }))

    # ---------- Desatualizado em muitas maquinas: o criterio ----------
    $v7 = $apps['7zip'].version; $vVlc = $apps['vlc'].version; $vPutty = $apps['putty'].version; $vFox = $apps['firefox'].version
    Hosts '7zip' 5 0 '0.1' $v7 'A'           # 5 de 5: 5 ou mais -> entra
    Hosts 'vlc' 1 9 '0.1' $vVlc 'B'          # 1 de 10: so uma -> nao entra
    Hosts 'putty' 4 0 '0.1' $vPutty 'C'      # 4 de 4: duas ou mais e 100% -> entra
    Hosts 'firefox' 2 8 '0.1' $vFox 'D'      # 2 de 10: 20% -> entra
    $h = Health
    $out = @(IssuesOf $h 'outdated')
    $titles = @($out | ForEach-Object { $_.title })
    Check '5 de 5 desatualizadas entra' ($titles -contains $apps['7zip'].title)
    Check '1 de 10 desatualizada NAO entra (so uma)' ($titles -notcontains $apps['vlc'].title)
    Check '4 de 4 desatualizadas entra (duas ou mais e 100%)' ($titles -contains $apps['putty'].title)
    Check '2 de 10 (exatos 20%) entra' ($titles -contains $apps['firefox'].title)
    Hosts 'vlc' 1 0 '0.1' $vVlc 'B2'         # agora 2 de 12 (16,7%): nao entra
    Hosts 'vlc' 0 1 '0.1' $vVlc 'B3'
    Hosts 'vlc' 0 1 '0.1' $vVlc 'B4'
    $h = Health
    Check '2 de 13 (menos de 20%) NAO entra' (@(IssuesOf $h 'outdated' | Where-Object { $_.title -eq $apps['vlc'].title }).Count -eq 0)
    $oo = IssuesOf $h 'outdated' | Where-Object { $_.title -eq $apps['7zip'].title }
    Check 'o aviso diz quantas e a versao da loja' ($oo.message -like '*5 de 5*' -and $oo.message -like "*$v7*") $oo.message
    Check 'desatualizado e aviso (nao erro)' ($oo.severity -eq 'warning')
    # versao que nao da para comparar nao conta como atrasada
    foreach ($i in 1..6) { Sql 'INSERT OR REPLACE INTO host_inventory (hostname, updated_utc) VALUES (?, ?)' @("E-X-$i", '2026-10-01T10:00:00.0000000Z'); Sql 'INSERT OR REPLACE INTO host_packages (hostname, package_id, version) VALUES (?,?,?)' @("E-X-$i", 'vlc', 'latest') }
    $h = Health
    Check 'versoes que nao dao para comparar nao entram na conta de atrasadas' (@(IssuesOf $h 'outdated' | Where-Object { $_.title -eq $apps['vlc'].title }).Count -eq 0)

    # ---------- Ordem e formato ----------
    $sev = @($h.issues | ForEach-Object { $_.severity })
    $firstWarning = [array]::IndexOf($sev, 'warning')
    $lastError = [array]::LastIndexOf($sev, 'error')
    Check 'erros vem antes dos avisos' ($firstWarning -lt 0 -or $lastError -lt $firstWarning)
    Check 'cada item traz o pacote (para abrir o aplicativo)' (@($h.issues | Where-Object { -not $_.packageId }).Count -eq 0)
}
finally {
    foreach ($id in $created) { $null = Call DELETE "/api/admin/apps/$id" }
    foreach ($row in (Items (Call GET '/api/admin/apps').Body | Where-Object { $_.packageId -in 'sem-grupo-teste', 'desativado-sem-grupo' })) { $null = Call DELETE "/api/admin/apps/$($row.id)" }
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
