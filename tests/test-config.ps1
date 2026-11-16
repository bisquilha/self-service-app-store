<#
.SYNOPSIS
    Testa o motor de configuracoes (servidor 1.7+): descricao vinda do servidor, validacao tudo-ou-nada, versao, auditoria,
    e o efeito real de ligar/desligar o Chocolatey e os aplicativos internos (catalogo, busca, cadastro, pedidos).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (Tactical simulado) e pasta de dados NOVA. Precisa de internet (cadastra o 7zip)
    e do feed interno de teste com o pacote "app-interno" (o run-suites.ps1 ja configura os dois).
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$pc = $env:COMPUTERNAME

function Call($method, $path, $body = $null, [switch]$NoHeader) {
    $headers = @{}
    if (-not $NoHeader) { $headers['X-Requested-With'] = 'loja-admin' }
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
function Cfg { (Call GET '/api/admin/config').Body }
function Setting($cfg, $key) { foreach ($g in $cfg.groups) { foreach ($s in $g.settings) { if ($s.key -eq $key) { return $s } } } }
function Put($values) { Call PUT '/api/admin/config' @{ values = $values } }
function AuditTotal { (Call GET '/api/admin/audit?size=1').Body.total }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

# ---------- Descricao e padroes ----------
$r = Call GET '/api/admin/config'
$cfg = $r.Body
$all = @($cfg.groups | ForEach-Object { $_.settings })
Check 'GET /config -> 200, versao 0 e 3 grupos' ($r.Code -eq 200 -and $cfg.version -eq 0 -and @($cfg.groups).Count -eq 3) "versao=$($cfg.version)"
Check 'sao 10 configuracoes' ($all.Count -eq 10) "total=$($all.Count)"
Check 'toda configuracao tem rotulo, ajuda e tipo' (@($all | Where-Object { -not $_.label -or -not $_.help -or -not $_.type }).Count -eq 0)
Check 'nada foi mexido: tudo no padrao' (@($all | Where-Object { -not $_.isDefault }).Count -eq 0)
Check 'Chocolatey e aplicativos internos ligados por padrao' ((Setting $cfg 'chocolatey.enabled').value -eq $true -and (Setting $cfg 'internalApps.enabled').value -eq $true)
Check 'endereco do Chocolatey = o do appsettings' ((Setting $cfg 'chocolatey.communityFeedUrl').value -eq 'https://community.chocolatey.org/api/v2/')
$info = (Call GET '/api/admin/info').Body
Check 'pasta interna = a do appsettings' ((Setting $cfg 'internalApps.feedPath').value -eq $info.internalFeed) "pasta=$($info.internalFeed)"
Check 'faixa do batimento vem do servidor (15-600)' ((Setting $cfg 'agent.pollSeconds').min -eq 15 -and (Setting $cfg 'agent.pollSeconds').max -eq 600)
Check 'dependencia declarada (endereco depende de "usar Chocolatey")' ((Setting $cfg 'chocolatey.communityFeedUrl').dependsOn -eq 'chocolatey.enabled')
$legacy = (Call GET '/api/admin/settings').Body
Check 'parametros antigos (/settings) continuam funcionando' (@($legacy.PSObject.Properties).Count -eq 6)

# ---------- Validacao ----------
$r = Call PUT '/api/admin/config' @{ values = @{ 'chocolatey.enabled' = $false } } -NoHeader
Check 'sem o cabecalho anti-CSRF -> 403' ($r.Code -eq 403)
$r = Put @{ 'nao.existe' = 1 }
Check 'chave desconhecida -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*nao.existe*') $r.Body.message
$r = Put @{ 'agent.pollSeconds' = 5 }
Check 'fora da faixa -> 400 com o rotulo e a faixa' ($r.Code -eq 400 -and $r.Body.message -like '*Batimento*' -and $r.Body.message -like '*15*600*') $r.Body.message
$r = Put @{ 'agent.pollSeconds' = 'abc' }
Check 'texto onde se espera numero -> 400' ($r.Code -eq 400)
$r = Put @{ 'chocolatey.enabled' = 'sim' }
Check 'texto onde se espera sim/nao -> 400' ($r.Code -eq 400)
$r = Put @{ 'chocolatey.communityFeedUrl' = 'isto nao e endereco' }
Check 'endereco invalido -> 400' ($r.Code -eq 400 -and $r.Body.message -like '*http*') $r.Body.message
$r = Put @{ 'chocolatey.communityFeedUrl' = 'ftp://x.example/api/v2/' }
Check 'endereco que nao e http/https -> 400' ($r.Code -eq 400)
$r = Call PUT '/api/admin/config' @{ values = @{} }
Check 'nada para salvar -> 400' ($r.Code -eq 400)
$audit0 = AuditTotal
$r = Put @{ 'chocolatey.enabled' = $false; 'agent.pollSeconds' = 1 }
$cfg = Cfg
Check 'tudo ou nada: um valor ruim recusa o pacote inteiro' ($r.Code -eq 400 -and (Setting $cfg 'chocolatey.enabled').value -eq $true -and $cfg.version -eq 0)
Check 'recusado nao grava auditoria' ((AuditTotal) -eq $audit0)

# ---------- Cadastra um app de cada fonte (para ver o efeito das chaves) ----------
$a1 = Call POST '/api/admin/apps' @{ packageId = '7zip'; source = 'community'; groups = @('*') }
Check 'cadastra 7zip (Chocolatey)' ($a1.Code -eq 200) $a1.Body.message
$a2 = Call POST '/api/admin/apps' @{ packageId = 'app-interno'; source = 'internal'; groups = @('*') }
Check 'cadastra app-interno (interno)' ($a2.Code -eq 200) $a2.Body.message
function Ids { @((Call GET "/api/catalog?hostname=$pc").Body.items | ForEach-Object { $_.packageId }) }
Check 'catalogo mostra os dois' ((Ids) -contains '7zip' -and (Ids) -contains 'app-interno')

# ---------- Desligar o Chocolatey ----------
$audit1 = AuditTotal
$r = Put @{ 'chocolatey.enabled' = $false }
$cfg = $r.Body
Check 'desligar o Chocolatey -> 200, versao 1' ($r.Code -eq 200 -and $cfg.version -eq 1 -and (Setting $cfg 'chocolatey.enabled').value -eq $false -and -not (Setting $cfg 'chocolatey.enabled').isDefault) "versao=$($cfg.version)"
Check 'mudanca gerou 1 linha de auditoria' ((AuditTotal) -eq $audit1 + 1)
$row = (Call GET '/api/admin/audit?size=1').Body.items | Select-Object -First 1
Check 'auditoria mostra o que mudou (antes Sim, depois Nao)' ($row.entityType -eq 'settings' -and $row.entityId -eq 'chocolatey.enabled' -and $row.field -eq 'Usar o Chocolatey' -and $row.oldValue -eq 'Sim' -and $row.newValue -eq 'Não') ($row | ConvertTo-Json -Compress)
Check 'catalogo esconde o app do Chocolatey e mantem o interno' (-not ((Ids) -contains '7zip') -and (Ids) -contains 'app-interno')
$r = Call GET '/api/admin/search?q=7zip&source=community'
Check 'busca no Chocolatey recusada (409)' ($r.Code -eq 409 -and $r.Body.message -like '*Chocolatey*') $r.Body.message
$r = Call POST '/api/admin/apps' @{ packageId = 'notepadplusplus'; source = 'community'; groups = @('*') }
Check 'cadastrar app do Chocolatey recusado' ($r.Code -eq 400 -and $r.Body.message -like '*Chocolatey*') $r.Body.message
$r = Call POST '/api/install' @{ appId = $a1.Body.id; hostname = $pc }
Check 'instalar app do Chocolatey recusado (409)' ($r.Code -eq 409) $r.Body.message
$r = Call POST '/api/install' @{ appId = $a1.Body.id; hostname = $pc; action = 'uninstall' }
Check 'desinstalar nao e barrado pela fonte desligada' ($r.Code -ne 409 -or $r.Body.message -notlike '*dispon*') "codigo=$($r.Code) $($r.Body.message)"
$st = (Call GET '/api/admin/status').Body
$ck = $st.checks | Where-Object { $_.name -like '*Chocolatey*' } | Select-Object -First 1
Check 'status mostra o Chocolatey como desligado (sem alarme)' ($ck.ok -and $ck.detail -like '*Desligado*') $ck.detail

# salvar o mesmo valor nao muda nada
$audit2 = AuditTotal
$r = Put @{ 'chocolatey.enabled' = $false }
Check 'salvar o mesmo valor nao sobe a versao nem grava auditoria' ($r.Body.version -eq 1 -and (AuditTotal) -eq $audit2)

# ---------- Ligar de volta e desligar os internos ----------
$r = Put @{ 'chocolatey.enabled' = $true; 'internalApps.enabled' = $false }
Check 'ligar Chocolatey e desligar internos numa so chamada -> versao 2' ($r.Code -eq 200 -and $r.Body.version -eq 2)
Check 'catalogo: volta o do Chocolatey, some o interno' ((Ids) -contains '7zip' -and -not ((Ids) -contains 'app-interno'))
$r = Call GET '/api/admin/search?q=app&source=internal'
Check 'busca interna recusada (409)' ($r.Code -eq 409 -and $r.Body.message -like '*internos*') $r.Body.message
$st = (Call GET '/api/admin/status').Body
$ck = $st.checks | Where-Object { $_.name -like '*Feed interno*' } | Select-Object -First 1
Check 'status mostra o feed interno como desligado' ($ck.ok -and $ck.detail -like '*Desligado*') $ck.detail

# ---------- Trocar valores e voltar ao padrao ----------
$r = Put @{ 'internalApps.enabled' = $true; 'agent.pollSeconds' = 120; 'chocolatey.communityFeedUrl' = 'https://espelho.exemplo.local/api/v2/' }
$cfg = $r.Body
Check 'numero e endereco salvos' ((Setting $cfg 'agent.pollSeconds').value -eq 120 -and (Setting $cfg 'chocolatey.communityFeedUrl').value -eq 'https://espelho.exemplo.local/api/v2/')
Check 'o valor padrao continua visivel ao lado do atual' ((Setting $cfg 'agent.pollSeconds').default -eq 60)
$r = Put @{ 'agent.pollSeconds' = $null; 'chocolatey.communityFeedUrl' = $null }
$cfg = $r.Body
Check 'nulo volta ao padrao' ((Setting $cfg 'agent.pollSeconds').value -eq 60 -and (Setting $cfg 'agent.pollSeconds').isDefault -and (Setting $cfg 'chocolatey.communityFeedUrl').isDefault)
Check 'tudo no padrao de novo' (@($cfg.groups | ForEach-Object { $_.settings } | Where-Object { -not $_.isDefault }).Count -eq 0)
Check 'catalogo completo de novo' ((Ids) -contains '7zip' -and (Ids) -contains 'app-interno')

Write-Output "----- $script:fails falha(s)"
exit $script:fails
