<#
.SYNOPSIS
    Testa a trilha de auditoria do painel (servidor 1.6.0+): alteracoes de apps, parametros, versoes do app e acoes em lote.

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development) e uma pasta de dados NOVA, no PC do dominio.
    Usa o pacote notepadplusplus (internet). O app criado aqui e apagado no fim (a auditoria dele fica, de proposito).
    A publicacao do .exe so e testada se Downloads\LojaDeAplicativos-1.2.0.exe existir (-Exe120 troca o caminho).

.EXAMPLE
    .\test-auditoria.ps1
#>
param([string]$Base = 'http://localhost:5080', [string]$Exe120 = (Join-Path $env:USERPROFILE 'Downloads\LojaDeAplicativos-1.2.0.exe'))
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0

function Call($method, $path, $body = $null, $headers = @{ 'X-Requested-With' = 'loja-admin' }) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = $headers }
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
function Audit($query = '') { Call GET "/api/admin/audit?$query&size=200" }
# A virgula impede o PowerShell de desembrulhar uma lista de um item so (senao .Count some).
function Rows($query = '') { , @(Items (Audit $query).Body.items) }
function Put($id, $o) { Call PUT "/api/admin/apps/$id" $o }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$created = @()
try {
    $start = (Audit).Body.total
    Check 'GET /audit responde 200' ((Audit).Code -eq 200) "total inicial=$start"

    # ---------- Apps ----------
    $a = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('LOJA-Todos') }
    $id = $a.Body.id
    if ($id) { $created += $id }
    $rows = Rows "entity=app&entityId=$id"
    Check 'criar app grava uma linha "create"' ($rows.Count -eq 1 -and $rows[0].kind -eq 'create') "linhas=$($rows.Count)"
    Check 'a linha traz quem, o app e os grupos' ($rows[0].user -like '*\*' -and $rows[0].entityLabel -eq $a.Body.title -and $rows[0].newValue -like '*LOJA-Todos*') "$($rows[0].user) / $($rows[0].entityLabel)"
    Check 'a data vem em UTC e recente' (([datetime]$rows[0].utc).ToUniversalTime() -gt (Get-Date).ToUniversalTime().AddMinutes(-5))

    $r = Put $id @{ groups = @('LOJA-Todos'); enabled = $false; autoUpdate = $false; allowUninstall = $true }
    $rows = Rows "entity=app&entityId=$id&kind=update"
    Check 'desligar o switch Ativo grava 1 linha com antes e depois' ($rows.Count -eq 1 -and $rows[0].field -eq 'Ativo' -and $rows[0].oldValue -eq 'Sim' -and $rows[0].newValue -eq 'N' + [char]0xE3 + 'o') "campo=$($rows[0].field) $($rows[0].oldValue)->$($rows[0].newValue)"

    $before = (Audit).Body.total
    $null = Put $id @{ groups = @('LOJA-Todos'); enabled = $false; autoUpdate = $false; allowUninstall = $true }
    Check 'salvar sem mudar nada nao grava nada' ((Audit).Body.total -eq $before)

    $null = Put $id @{ groups = @('LOJA-Todos', 'LOJA-Admins'); enabled = $true; autoUpdate = $true; allowUninstall = $false }
    $rows = Rows "entity=app&entityId=$id&kind=update"
    $fields = @($rows | ForEach-Object field | Sort-Object)
    Check 'mudar 4 campos de uma vez grava 4 linhas (uma por campo)' ($rows.Count -eq 5 -and ($fields -contains 'Grupos do AD') -and ($fields -contains 'Atualiza' + [char]0xE7 + [char]0xE3 + 'o autom' + [char]0xE1 + 'tica')) "linhas=$($rows.Count) campos=$($fields -join ',')"
    $g = $rows | Where-Object field -eq 'Grupos do AD' | Select-Object -First 1
    Check 'grupos: antes e depois legiveis' ($g.oldValue -eq 'LOJA-Todos' -and $g.newValue -eq 'LOJA-Admins, LOJA-Todos') "$($g.oldValue) -> $($g.newValue)"

    # Desfazer (o painel manda reason = undo)
    $null = Put $id @{ groups = @('LOJA-Todos'); enabled = $true; autoUpdate = $false; allowUninstall = $true; reason = 'undo' }
    $undo = Rows "entity=app&entityId=$id&kind=undo"
    Check 'Desfazer fica marcado como revertido (kind=undo, undo=true)' ($undo.Count -ge 2 -and -not ($undo | Where-Object { -not $_.undo })) "linhas=$($undo.Count)"

    # Recusado (grupo inexistente) nao grava nada
    $before = (Audit).Body.total
    $r = Put $id @{ groups = @('LOJA-NaoExiste-zzz'); enabled = $true; autoUpdate = $false; allowUninstall = $true }
    Check 'alteracao recusada (400) nao grava auditoria' ($r.Code -eq 400 -and (Audit).Body.total -eq $before)

    # ---------- Parametros ----------
    $s = (Call GET '/api/admin/settings').Body
    $new = @{ autoUpdateStartHour = $s.autoUpdateStartHour; autoUpdateEndHour = $s.autoUpdateEndHour; autoUpdateMaxPerCycle = $s.autoUpdateMaxPerCycle + 1; inventoryMaxAgeMinutes = $s.inventoryMaxAgeMinutes; requestTimeoutMinutes = $s.requestTimeoutMinutes; metadataRefreshHours = $s.metadataRefreshHours }
    $null = Call PUT '/api/admin/settings' $new
    $rows = Rows 'entity=settings'
    Check 'mudar um parametro grava 1 linha com o rotulo, antes e depois' ($rows.Count -eq 1 -and $rows[0].field -like 'M*quinas por ciclo' -and [int]$rows[0].oldValue -eq $s.autoUpdateMaxPerCycle -and [int]$rows[0].newValue -eq $s.autoUpdateMaxPerCycle + 1) "$($rows[0].field) $($rows[0].oldValue)->$($rows[0].newValue)"
    $null = Call PUT '/api/admin/settings' @{ autoUpdateStartHour = 99; autoUpdateEndHour = 14; autoUpdateMaxPerCycle = 20; inventoryMaxAgeMinutes = 10; requestTimeoutMinutes = 60; metadataRefreshHours = 12 }
    Check 'parametro fora da faixa (400) nao grava' ((Rows 'entity=settings').Count -eq 1)
    $null = Call PUT '/api/admin/settings' @{ autoUpdateStartHour = $s.autoUpdateStartHour; autoUpdateEndHour = $s.autoUpdateEndHour; autoUpdateMaxPerCycle = $s.autoUpdateMaxPerCycle; inventoryMaxAgeMinutes = $s.inventoryMaxAgeMinutes; requestTimeoutMinutes = $s.requestTimeoutMinutes; metadataRefreshHours = $s.metadataRefreshHours }
    Check 'voltar ao valor antigo tambem fica registrado' ((Rows 'entity=settings').Count -eq 2)

    # ---------- Acoes em lote ----------
    $null = Call POST '/api/admin/autoupdate/run'
    $act = Rows 'entity=action'
    Check 'rodar a atualizacao automatica agora fica registrado' (@($act | Where-Object { $_.entityLabel -like 'Atualiza*autom*' }).Count -eq 1)
    $null = Call POST "/api/admin/apps/$id/upgrade-all"
    Check '"atualizar em todas as maquinas" fica registrado no app' ((Rows "entity=action&entityId=$id").Count -eq 1)

    # ---------- Versoes do app ----------
    if (Test-Path $Exe120) {
        Add-Type -AssemblyName System.Net.Http
        $handler = New-Object System.Net.Http.HttpClientHandler; $handler.UseDefaultCredentials = $true
        $http = New-Object System.Net.Http.HttpClient($handler); $http.Timeout = [TimeSpan]::FromMinutes(5)
        $http.DefaultRequestHeaders.Add('X-Requested-With', 'loja-admin')
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $fs = [IO.File]::OpenRead($Exe120)
        try {
            $form.Add((New-Object System.Net.Http.StreamContent($fs)), 'file', 'LojaDeAplicativos.exe')
            $resp = $http.PostAsync("$Base/api/admin/client/releases?mandatory=true", $form).Result
        } finally { $fs.Dispose() }
        Check 'publicar o .exe -> 200' ([int]$resp.StatusCode -eq 200) "http=$([int]$resp.StatusCode) $(if ([int]$resp.StatusCode -ne 200) { $resp.Content.ReadAsStringAsync().Result })"
        $rel = Rows 'entity=release'
        Check 'publicar a versao fica registrado (obrigatoria)' ($rel.Count -eq 1 -and $rel[0].kind -eq 'create' -and $rel[0].newValue -like '*obrigat*') "$($rel[0].entityLabel): $($rel[0].newValue)"
    } else {
        Skip 'publicar versao do app' "falta $Exe120"
    }

    # ---------- Consulta ----------
    $all = Rows
    Check 'ordem padrao: a mais recente primeiro' ([long]$all[0].id -gt [long]$all[-1].id)
    $asc = (Call GET '/api/admin/audit?sort=when&dir=asc&size=200').Body.items
    Check 'sort=when asc inverte a ordem' ([long]@($asc)[0].id -lt [long]@($asc)[-1].id)
    Check 'sort desconhecido -> 200 (ordem padrao)' ((Call GET '/api/admin/audit?sort=nao_existe').Code -eq 200)
    Check 'sort com SQL -> 200 e a tabela continua la' ((Call GET ('/api/admin/audit?sort=' + [uri]::EscapeDataString('id; DROP TABLE audit_log; --'))).Code -eq 200 -and (Audit).Body.total -ge $all.Count)
    Check 'filtro de texto acha pelo valor' ((Rows 'text=LOJA-Admins').Count -ge 1)
    Check 'filtro de texto com SQL e so texto' ((Call GET ('/api/admin/audit?text=' + [uri]::EscapeDataString("' OR '1'='1"))).Body.total -eq 0)
    Check 'filtro por usuario' ((Rows "user=$($env:USERNAME)").Count -ge 1 -and (Rows 'user=ninguem.assim').Count -eq 0)
    $today = Get-Date -Format 'yyyy-MM-dd'
    Check 'filtro de datas (de hoje a hoje) traz as de hoje' ((Rows "from=$today&to=$today").Count -eq $all.Count)
    Check 'de depois de ate -> vazio' ((Call GET '/api/admin/audit?from=2099-01-01&to=2098-01-01').Body.total -eq 0)
    $p = (Call GET '/api/admin/audit?size=10&page=999').Body
    Check 'pagina alem do fim -> vazia, total certo' (@($p.items).Count -eq 0 -and $p.total -eq $all.Count)
    Check 'size menor que 10 vira 10' ((Call GET '/api/admin/audit?size=1').Body.size -eq 10)
    Check 'sem o cabecalho do painel, alterar e recusado (CSRF), e nao grava' ((Call PUT "/api/admin/apps/$id" @{ groups = @('LOJA-Todos'); enabled = $false; autoUpdate = $false; allowUninstall = $true } @{}).Code -eq 403)
}
finally {
    foreach ($x in $created) { try { Call DELETE "/api/admin/apps/$x" | Out-Null } catch { Write-Output "AVISO nao consegui apagar o app $x" } }
}
$del = (Rows 'kind=delete')
Check 'apagar o app fica registrado e a trilha dele continua' ($del.Count -ge 1 -and (Rows "entity=app&entityId=$id").Count -ge 5)

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
