<#
.SYNOPSIS
    Testa as Configuracoes do painel: faixas dos parametros, salvar, e o envio do .exe do app da loja
    (inspecionar sem publicar, publicar, versao ja publicada, versao mais antiga que a atual).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar e com a pasta de dados NOVA (as versoes publicadas aqui ficam no banco).
    -DataDir  pasta de dados do servidor: se informada, confere que a inspecao nao deixa arquivo temporario em cliente\.
    -Exe120 / -Exe121  dois LojaDeAplicativos.exe de versoes diferentes (padrao: Downloads\LojaDeAplicativos-1.2.0.exe e -1.2.1.exe).
    Sem os .exe, as verificacoes de envio sao puladas.
#>
param(
    [string]$Base = 'http://localhost:5080',
    [string]$DataDir = '',
    [string]$Exe120 = (Join-Path $env:USERPROFILE 'Downloads\LojaDeAplicativos-1.2.0.exe'),
    [string]$Exe121 = (Join-Path $env:USERPROFILE 'Downloads\LojaDeAplicativos-1.2.1.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
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
function Items($b) { @($b | Where-Object { $null -ne $_ }) }

# Envio de arquivo (multipart) com o login do Windows. Um GET antes evita refazer o POST grande por causa do Negotiate.
function NewClient([bool]$withHeader) {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseDefaultCredentials = $true
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    if ($withHeader) { $client.DefaultRequestHeaders.Add('X-Requested-With', 'loja-admin') }
    $client.GetAsync("$Base/api/admin/info").Result | Out-Null
    return $client
}
function Upload($client, $path, $url, $fileName = 'LojaDeAplicativos.exe') {
    $content = New-Object System.Net.Http.MultipartFormDataContent
    $stream = [IO.File]::OpenRead($path)
    try {
        $content.Add((New-Object System.Net.Http.StreamContent($stream)), 'file', $fileName)
        $resp = $client.PostAsync("$Base$url", $content).Result
        $text = $resp.Content.ReadAsStringAsync().Result
        $json = try { $text | ConvertFrom-Json } catch { $null }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $json }
    } finally { $stream.Dispose() }
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

# ---------- Parametros: faixas vindas do servidor ----------
$meta = Call GET '/api/admin/settings/meta'
$fields = @(Items $meta.Body)
Check 'meta traz os 6 parametros' ($meta.Code -eq 200 -and $fields.Count -eq 6) "itens=$($fields.Count)"
$cur = (Call GET '/api/admin/settings').Body
$keysOfSettings = @($cur.PSObject.Properties.Name | Sort-Object)
Check 'chaves do meta = chaves do GET /settings' ((($fields | ForEach-Object key | Sort-Object) -join ',') -eq ($keysOfSettings -join ','))
$start = $fields | Where-Object key -eq 'autoUpdateStartHour'
$end = $fields | Where-Object key -eq 'autoUpdateEndHour'
Check 'faixa do inicio 0-23 e do fim 0-24' ($start.min -eq 0 -and $start.max -eq 23 -and $end.min -eq 0 -and $end.max -eq 24) "inicio=$($start.min)-$($start.max) fim=$($end.min)-$($end.max)"
Check 'todo parametro tem rotulo' (@($fields | Where-Object { -not $_.label }).Count -eq 0)

# ---------- Salvar ----------
$orig = $cur.autoUpdateMaxPerCycle
$new = @{}; $cur.PSObject.Properties | ForEach-Object { $new[$_.Name] = $_.Value }
$new.autoUpdateMaxPerCycle = 33
$r = Call PUT '/api/admin/settings' $new
Check 'salvar valor valido -> 200' ($r.Code -eq 200 -and $r.Body.autoUpdateMaxPerCycle -eq 33)
Check 'GET mostra o valor salvo' ((Call GET '/api/admin/settings').Body.autoUpdateMaxPerCycle -eq 33)
$bad = @{}; $new.GetEnumerator() | ForEach-Object { $bad[$_.Key] = $_.Value }
$bad.autoUpdateStartHour = $start.max + 1
$r = Call PUT '/api/admin/settings' $bad
Check 'valor fora da faixa -> 400 com o rotulo' ($r.Code -eq 400 -and $r.Body.message -like "*$($start.label)*") $r.Body.message
Check 'valor recusado nao foi salvo' ((Call GET '/api/admin/settings').Body.autoUpdateStartHour -eq $cur.autoUpdateStartHour)
$new.autoUpdateMaxPerCycle = $orig
Call PUT '/api/admin/settings' $new | Out-Null

# ---------- Envio do .exe ----------
$withHeader = NewClient $true
$noHeader = NewClient $false
$txt = Join-Path $env:TEMP 'loja-teste-notas.txt'; Set-Content $txt 'isto nao e um executavel'
$other = Join-Path $env:WINDIR 'System32\whoami.exe'

$r = Upload $noHeader $txt '/api/admin/client/releases/inspect'
Check 'inspecionar SEM o cabecalho anti-CSRF -> 403' ($r.Code -eq 403) "codigo=$($r.Code)"
$r = Upload $withHeader $txt '/api/admin/client/releases/inspect' 'notas.txt'
Check 'arquivo que nao e executavel -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*execut*') $r.Body.message
if (Test-Path $other) {
    $r = Upload $withHeader $other '/api/admin/client/releases/inspect' 'whoami.exe'
    Check 'executavel que nao e a loja -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*Loja de Aplicativos*') $r.Body.message
}
Remove-Item $txt -Force -ErrorAction SilentlyContinue

if ((Test-Path $Exe120) -and (Test-Path $Exe121)) {
    $before = @(Items (Call GET '/api/admin/client/releases').Body).Count
    $r = Upload $withHeader $Exe120 '/api/admin/client/releases/inspect'
    Check 'inspecionar 1.2.0 -> versao detectada' ($r.Code -eq 200 -and $r.Body.version -eq '1.2.0') "versao=$($r.Body.version)"
    Check 'tamanho e SHA-256 conferem' ($r.Body.size -eq (Get-Item $Exe120).Length -and $r.Body.sha256 -match '^[0-9A-F]{64}$' -and $r.Body.sha256 -eq (Get-FileHash $Exe120 -Algorithm SHA256).Hash)
    Check 'versao nova: nao ha aviso' (-not $r.Body.alreadyPublished -and -not $r.Body.isCurrent -and -not $r.Body.olderThanCurrent)
    Check 'inspecionar nao publica nada' (@(Items (Call GET '/api/admin/client/releases').Body).Count -eq $before)
    if ($DataDir) {
        $left = @(Get-ChildItem (Join-Path $DataDir 'cliente') -Filter 'envio-*.tmp' -ErrorAction SilentlyContinue)
        Check 'inspecionar nao deixa arquivo temporario' ($left.Count -eq 0) "sobraram=$($left.Count)"
    } else { Skip 'arquivo temporario' 'passe -DataDir' }

    $r = Upload $withHeader $Exe120 '/api/admin/client/releases?mandatory=false'
    Check 'publicar 1.2.0 -> 200 e vira a atual' ($r.Code -eq 200 -and $r.Body.version -eq '1.2.0' -and $r.Body.isCurrent) "codigo=$($r.Code)"
    $r = Upload $withHeader $Exe120 '/api/admin/client/releases/inspect'
    Check 'inspecionar a mesma versao: ja publicada e atual' ($r.Body.alreadyPublished -and $r.Body.isCurrent -and $r.Body.currentVersion -eq '1.2.0')

    $r = Upload $withHeader $Exe121 '/api/admin/client/releases?mandatory=true'
    Check 'publicar 1.2.1 obrigatoria -> 200' ($r.Code -eq 200 -and $r.Body.version -eq '1.2.1' -and $r.Body.mandatory) "codigo=$($r.Code)"
    $r = Upload $withHeader $Exe120 '/api/admin/client/releases/inspect'
    Check 'inspecionar 1.2.0 com a 1.2.1 atual: mais antiga que a atual' ($r.Body.alreadyPublished -and -not $r.Body.isCurrent -and $r.Body.olderThanCurrent -and $r.Body.currentVersion -eq '1.2.1')

    $list = @(Items (Call GET '/api/admin/client/releases').Body)
    Check 'so uma versao e a atual' (@($list | Where-Object isCurrent).Count -eq 1 -and ($list | Where-Object isCurrent).version -eq '1.2.1')
    $r = Call DELETE '/api/admin/client/releases/1.2.1'
    Check 'nao apaga a versao atual -> 400' ($r.Code -eq 400) $r.Body.message
    $r = Call DELETE '/api/admin/client/releases/1.2.0'
    Check 'apaga versao que nao e a atual -> 204' ($r.Code -eq 204)
} else {
    Skip 'envio e publicacao do .exe' "faltam $Exe120 e/ou $Exe121"
}

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
