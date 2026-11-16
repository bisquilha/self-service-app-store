param([string]$Base = 'http://localhost:5080')
$ErrorActionPreference = 'Stop'
$pc = $env:COMPUTERNAME
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
function Check($name, $cond, $detail = '') { if ($cond) { "OK    $name $detail" } else { "FALHA $name $detail"; $script:fails++ } }
function Item($pkg) { (Call GET "/api/catalog?hostname=$pc").Body.items | Where-Object { $_.packageId -eq $pkg } }
function WaitDone($id) { for ($i = 0; $i -lt 20; $i++) { $r = (Call GET "/api/requests/$id").Body; if ($r.status -ne 'running') { return $r }; Start-Sleep -Milliseconds 700 }; return $r }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$a = Call POST '/api/admin/apps' @{ packageId = '7zip'; source = 'community'; groups = @('*') }
Check 'adiciona 7zip' ($a.Code -eq 200) "versao loja $($a.Body.version), desinstalar=$($a.Body.allowUninstall)"
$id = $a.Body.id

$cat = (Call GET "/api/catalog?hostname=$pc").Body
Check 'catalogo novo formato' ($null -ne $cat.items -and -not $cat.inventoryUtc) "itens=$(@($cat.items).Count) inventario=$($cat.inventoryUtc)"

$r = Call POST "/api/inventory/refresh?hostname=$pc"
Check 'consulta de instalados disparada' ($r.Body.started -eq $true)
$r = Call POST "/api/inventory/refresh?hostname=$pc"
Check 'segunda consulta seguida ignorada' ($r.Body.started -eq $false)
Start-Sleep -Seconds 4
$cat = (Call GET "/api/catalog?hostname=$pc").Body
Check 'inventario recebido' ($cat.inventoryUtc -and -not $cat.inventoryRunning) $cat.inventoryUtc
$it = Item '7zip'; Check 'nao instalado ainda' (-not $it.installedVersion)

$req = (Call POST '/api/install' @{ appId = $id; hostname = $pc }).Body
$done = WaitDone $req.id
$it = Item '7zip'
Check 'instalar -> card mostra instalado' ($done.status -eq 'success' -and $it.installedVersion -eq '1.0') "versao=$($it.installedVersion) msg=$($done.message)"
Check 'versao antiga -> atualizacao disponivel' ($it.updateAvailable -eq $true)

$req = (Call POST '/api/install' @{ appId = $id; hostname = $pc; action = 'upgrade' }).Body
$done = WaitDone $req.id
$it = Item '7zip'
Check 'atualizar -> em dia' ($done.status -eq 'success' -and $it.installedVersion -eq '9999.0' -and -not $it.updateAvailable) "$($done.message) versao=$($it.installedVersion)"

$req = (Call POST '/api/install' @{ appId = $id; hostname = $pc; action = 'uninstall' }).Body
$done = WaitDone $req.id
$it = Item '7zip'
Check 'desinstalar -> some do inventario' ($done.status -eq 'success' -and -not $it.installedVersion) $done.message

$r = Call PUT "/api/admin/apps/$id" @{ groups = @('*'); enabled = $true; autoUpdate = $false; allowUninstall = $false }
Check 'admin desliga desinstalar' ($r.Body.allowUninstall -eq $false)
$r = Call POST '/api/install' @{ appId = $id; hostname = $pc; action = 'uninstall' }
Check 'desinstalar bloqueado pelo admin -> 403' ($r.Code -eq 403) $r.Body.message
$r = Call POST '/api/install' @{ appId = $id; hostname = $pc; action = 'format-c' }
Check 'acao inventada -> 400' ($r.Code -eq 400) $r.Body.message

# Atualizacao automatica: instala (fica 1.0), marca auto-atualizar e roda o ciclo manualmente
$req = (Call POST '/api/install' @{ appId = $id; hostname = $pc }).Body; WaitDone $req.id | Out-Null
$r = Call PUT "/api/admin/apps/$id" @{ groups = @('*'); enabled = $true; autoUpdate = $true; allowUninstall = $true }
$r = Call POST '/api/admin/autoupdate/run'
Check 'auto-update dispara para versao antiga' ($r.Body.started -eq 1) "disparados=$($r.Body.started)"
Start-Sleep -Seconds 8
$it = Item '7zip'
Check 'auto-update atualizou' ($it.installedVersion -eq '9999.0') "versao=$($it.installedVersion)"
$r = Call POST '/api/admin/autoupdate/run'
Check 'auto-update nao repete' ($r.Body.started -eq 0)

$log = (Call GET '/api/admin/requests').Body
$auto = @($log | Where-Object { $_.user -like '(autom*' })
Check 'historico mostra o automatico' ($auto.Count -eq 1) "$(@($log).Count) linhas"
Check 'consultas (list) fora do historico' (-not ($log | Where-Object { $_.action -eq 'list' }))
$mine = (Call GET "/api/requests?hostname=$pc").Body
Check 'meus pedidos sem consultas' (-not ($mine | Where-Object { $_.action -eq 'list' })) "$(@($mine).Count) pedidos"

$st = (Call GET '/api/admin/status').Body.checks | Where-Object { $_.name -like 'Atualiza*' }
"INFO  $($st.name): $($st.detail)"
"----- $script:fails falha(s)"
exit $script:fails
