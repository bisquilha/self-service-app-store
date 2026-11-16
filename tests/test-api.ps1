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
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json; Bytes = $r.RawContentStream.ToArray() }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        $text = (New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)).ReadToEnd()
        $json = try { $text | ConvertFrom-Json } catch { $null }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $json; Bytes = $null }
    }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}

# Espera o servidor subir
for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$r = Call GET '/' -Anon;                                   Check 'raiz anonima' ($r.Code -eq 200)
$r = Call GET "/api/me?hostname=$pc" -Anon;                Check 'sem login -> 401' ($r.Code -eq 401)
$me = Call GET "/api/me?hostname=$pc";                     Check 'me (este PC)' ($me.Body.machineOk -and $me.Body.isAdmin) "user=$($me.Body.user)"
$r = Call GET '/api/me?hostname=SRV-EXEMPLO';              Check 'me (servidor bloqueado)' (-not $r.Body.machineOk) $r.Body.machineMessage
$r = Call GET '/api/me?hostname=PC-OFFLINE';               Check 'me (offline)' (-not $r.Body.machineOk) $r.Body.machineMessage
$r = Call GET '/api/me?hostname=SRV-COMO-ESTACAO';         Check 'me (Windows Server cadastrado como estacao)' (-not $r.Body.machineOk) $r.Body.machineMessage
$r = Call GET '/api/me?hostname=PC%3Bdel';                 Check 'me (hostname invalido)' (-not $r.Body.machineOk) $r.Body.machineMessage

$r = Call GET '/api/admin/search?q=7zip&source=community'; Check 'busca Chocolatey 7zip' ($r.Code -eq 200 -and ($r.Body | Where-Object packageId -eq '7zip')) "$(@($r.Body).Count) resultados"
$r = Call GET '/api/admin/search?q=&source=internal'
$ai = $r.Body | Where-Object packageId -eq 'app-interno'
Check 'feed interno lista e pega maior versao' ($ai.version -eq '2.10.0') "versao=$($ai.version) total=$(@($r.Body).Count)"

$a1 = Call POST '/api/admin/apps' @{ packageId = '7zip'; source = 'community'; groups = @('*') }
Check 'adiciona 7zip' ($a1.Code -eq 200) "$($a1.Body.title) $($a1.Body.version)"
$a2 = Call POST '/api/admin/apps' @{ packageId = 'app-interno'; source = 'internal'; groups = @('*') }
Check 'adiciona app interno' ($a2.Code -eq 200) "$($a2.Body.title) $($a2.Body.version)"
# S-1-5-32-546 (BUILTIN\Guests, por SID para nao depender do idioma do Windows): o usuario de teste nao faz parte,
# entao o app e criado mas nao fica visivel para ele.
$r = Call POST '/api/admin/apps' @{ packageId = 'teste-falha'; source = 'internal'; groups = @('GRUPO-QUE-NAO-EXISTE') }
Check 'grupo inexistente recusado (400)' ($r.Code -eq 400) $r.Body.message
$a3 = Call POST '/api/admin/apps' @{ packageId = 'teste-falha'; source = 'internal'; groups = @('S-1-5-32-546') }
Check 'adiciona teste-falha (grupo existente do qual o usuario nao faz parte)' ($a3.Code -eq 200)
$r = Call POST '/api/admin/apps' @{ packageId = '7zip'; source = 'community'; groups = @('*') }
Check 'duplicado recusado' ($r.Code -eq 400) $r.Body.message
$r = Call POST '/api/admin/apps' @{ packageId = 'x;calc'; source = 'community'; groups = @('*') }
Check 'nome malicioso recusado' ($r.Code -eq 400) $r.Body.message
$r = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @() }
Check 'sem grupo recusado' ($r.Code -eq 400) $r.Body.message

$cat = Call GET '/api/catalog'; $cat.Body = $cat.Body.items
Check 'catalogo (admin ve tudo)' (@($cat.Body).Count -eq 3) "$(@($cat.Body).Count) apps"
$icon = Call GET "/api/icons/$($a2.Body.id)"
Check 'icone embutido do pacote interno' ($icon.Code -eq 200 -and $icon.Bytes[1] -eq 0x50) "$($icon.Bytes.Length) bytes"
$z = $cat.Body | Where-Object packageId -eq '7zip'
Write-Output "INFO  7zip tem icone em cache: $($z.hasIcon)"

$i1 = Call POST '/api/install' @{ appId = $a1.Body.id; hostname = $pc }
Check 'instala 7zip (pedido aceito)' ($i1.Code -eq 200 -and $i1.Body.status -eq 'running') $i1.Body.message
$r = Call POST '/api/install' @{ appId = $a2.Body.id; hostname = $pc }
Check 'segundo pedido simultaneo bloqueado' ($r.Code -eq 409) $r.Body.message
$r = Call POST '/api/install' @{ appId = $a1.Body.id; hostname = 'SRV-EXEMPLO' }
Check 'instalar em servidor negado' ($r.Code -eq 409) $r.Body.message
$r = Call POST '/api/install' @{ appId = $a1.Body.id; hostname = 'SRV-COMO-ESTACAO' }
Check 'instalar em Windows Server marcado como estacao negado' ($r.Code -eq 409) $r.Body.message
$r = Call POST '/api/install' @{ appId = 99999; hostname = $pc }
Check 'app inexistente' ($r.Code -eq 404)

Start-Sleep -Seconds 8
$r = Call GET "/api/requests/$($i1.Body.id)"
Check 'pedido 7zip concluido via callback' ($r.Body.status -eq 'success') $r.Body.message

$i2 = Call POST '/api/install' @{ appId = $a3.Body.id; hostname = $pc }
Start-Sleep -Seconds 8
$r = Call GET "/api/requests/$($i2.Body.id)"
Check 'pacote com falha registrado' ($r.Body.status -eq 'failed') $r.Body.message

$r = Call POST '/api/callback' @{ token = ('A' * 64); exitCode = 0; output = 'x' } -Anon
Check 'callback com token falso recusado' ($r.Code -eq 404)

$mine = Call GET "/api/requests?hostname=$pc"
Check 'meus pedidos' (@($mine.Body).Count -ge 2) "$(@($mine.Body).Count) pedidos"
$log = Call GET '/api/admin/requests'
$denied = @($log.Body | Where-Object status -eq 'denied').Count
Check 'historico admin inclui negados' ($denied -ge 2) "$(@($log.Body).Count) linhas, $denied negados"

$r = Call PUT "/api/admin/apps/$($a1.Body.id)" @{ groups = @('*'); enabled = $false; autoUpdate = $false }
$cat = Call GET '/api/catalog'; $cat.Body = $cat.Body.items
Check 'desativar esconde do catalogo' (-not ($cat.Body | Where-Object id -eq $a1.Body.id))
$r = Call PUT "/api/admin/apps/$($a1.Body.id)" @{ groups = @('*'); enabled = $true; autoUpdate = $false }

$st = Call GET '/api/admin/status'
foreach ($c in $st.Body.checks) { Write-Output ("INFO  status: [{0}] {1}: {2}" -f $(if ($c.ok) { 'ok' } else { '!!' }), $c.name, $c.detail) }

Write-Output "----- $script:fails falha(s)"
exit $script:fails
