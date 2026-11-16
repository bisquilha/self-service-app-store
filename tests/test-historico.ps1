<#
.SYNOPSIS
    Testa o historico de pedidos do painel: filtros, paginacao, ordenacao no servidor, CSV e codigo de saida (servidor 1.5.0+).

.DESCRIPTION
    Rodar com o servidor de demonstracao no ar (ambiente Development) e uma pasta de dados NOVA.
    Como nao ha pedidos suficientes para paginar, o script grava 60 pedidos de mentira direto no banco
    (precisa do python no PATH e de -DataDir com a pasta do loja.db). Sem isso, ele pula tudo.

    -DataDir  pasta de dados do servidor (a que tem o loja.db). Obrigatorio.

.EXAMPLE
    .\test-historico.ps1 -DataDir C:\temp\loja-dados
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0

function Call($method, $path) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        if ($r.Headers['Content-Type'] -like 'application/json*') { $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json }
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json; Raw = $r.RawContentStream.ToArray(); Headers = $r.Headers }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { throw }
        return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $null; Raw = @(); Headers = $resp.Headers }
    }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}
function Skip($name, $why) { Write-Output "PULOU $name ($why)"; $script:skips++ }
function Items($b) { @($b | Where-Object { $null -ne $_ }) }
function Enc($s) { [uri]::EscapeDataString($s) }
function Hist($query) { Call GET "/api/admin/history?$query" }
# true se a lista esta em ordem (sem diferenciar maiusculas, como o servidor faz)
function Sorted($values, $descending = $false) {
    for ($i = 1; $i -lt $values.Count; $i++) {
        $c = [string]::Compare([string]$values[$i - 1], [string]$values[$i], [StringComparison]::OrdinalIgnoreCase)
        if ($descending) { if ($c -lt 0) { return $false } } elseif ($c -gt 0) { return $false }
    }
    $true
}
# Percorre todas as paginas (size=10) e devolve os itens na ordem em que vieram.
function AllPages($query) {
    $all = @(); $page = 1
    do {
        $r = Hist "$query&size=10&page=$page"
        $all += @(Items $r.Body.items)
        $pages = [Math]::Ceiling($r.Body.total / 10)
        $page++
    } while ($page -le $pages)
    $all
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

if (-not ($DataDir -and (Test-Path (Join-Path $DataDir 'loja.db')) -and (Get-Command python -ErrorAction SilentlyContinue))) {
    Skip 'historico inteiro' 'passe -DataDir com a pasta do loja.db e tenha o python no PATH'
    Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
    exit $script:fails
}

# ---------- Pedidos de mentira ----------
$py = Join-Path $env:TEMP 'loja-test-historico.py'
@'
import sqlite3, sys, datetime
db, bs = sys.argv[1], chr(92)
users = ['ana.souza', 'Bruno.Lima', 'carla.dias', 'Diego.Ramos', 'eva.melo']
hosts = ['PC-HQ-101', 'pc-sp-203', 'PC-OFFLINE']
apps = [('7zip', '7-Zip'), ('vlc', 'VLC media player'), ('putty', 'PuTTY'), ('firefox', 'Mozilla Firefox')]
statuses = [('success', 'Instalado com sucesso.', 0), ('failed', 'Erro 1603 durante a instalacao.', 1603), ('denied', 'Sem permissao para este aplicativo.', 1), ('timeout', 'Sem retorno do computador.', None)]
actions = ['install', 'upgrade', 'uninstall']
base = datetime.datetime(2026, 9, 1, 12, 0, 0)
c = sqlite3.connect(db, timeout=10)
for i in range(60):
    t = base + datetime.timedelta(days=i % 20, minutes=i * 7)
    f = t + datetime.timedelta(seconds=30)
    a = apps[i % 4]; s = statuses[i % 4 if i % 3 else (i + 1) % 4]
    c.execute('insert into requests (created_utc,finished_utc,username,hostname,agent_id,app_id,package_id,title,source,action,status,message,exit_code,output) values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
        (t.strftime('%Y-%m-%dT%H:%M:%S.0000000Z'), f.strftime('%Y-%m-%dT%H:%M:%S.0000000Z'), 'CORP' + bs + users[i % 5], hosts[i % 3], 'mock-' + hosts[i % 3], None, a[0], a[1], 'community', actions[i % 3], s[0], s[1], s[2], 'saida ' + str(i)))
# quatro pedidos recentes para conferir os numeros da visao geral: tres hoje (a partir de 00:01, hora local) e uma falha de 8 dias atras
today0 = datetime.datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
def utc(t): return t.astimezone(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.0000000Z')
for when, status, code in [(today0 + datetime.timedelta(minutes=1), 'success', 0), (today0 + datetime.timedelta(minutes=2), 'failed', 1603), (today0 + datetime.timedelta(minutes=3), 'timeout', None), (today0 - datetime.timedelta(days=8) + datetime.timedelta(hours=12), 'failed', 1603)]:
    c.execute('insert into requests (created_utc,finished_utc,username,hostname,agent_id,app_id,package_id,title,source,action,status,message,exit_code,output) values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
        (utc(when), utc(when + datetime.timedelta(seconds=20)), 'CORP' + bs + 'eva.melo', 'PC-RECENTE', 'mock-recente', None, '7zip', '7-Zip', 'community', 'install', status, 'Erro 1603 durante a instalacao.' if status == 'failed' else 'pedido recente', code, 'saida recente'))
# uma consulta "list": nunca entra no historico
c.execute("insert into requests (created_utc,username,hostname,agent_id,app_id,package_id,title,source,action,status) values ('2026-09-30T12:00:00.0000000Z','Homelab' || ? || 'zz.consulta','PC-HQ-101','mock',NULL,'-','Consulta','community','list','success')", (bs,))
c.commit()
'@ | Set-Content -Path $py -Encoding UTF8
python -I $py (Join-Path $DataDir 'loja.db')
Remove-Item $py -ErrorAction SilentlyContinue

# ---------- Total e consultas "list" ----------
$r = Hist 'size=10'
$total = $r.Body.total
Check 'GET /history responde 200' ($r.Code -eq 200) "total=$total"
Check 'total conta os 60 pedidos e nao a consulta "list"' ($total -ge 60 -and -not (@(AllPages '') | Where-Object { $_.user -like '*zz.consulta*' }))
Check 'size menor que 10 vira 10' (@(Items (Hist 'size=1').Body.items).Count -eq 10)
Check 'size maior que 500 vira 500' ((Hist 'size=99999').Body.size -eq 500)

# ---------- Ordem padrao e ordenacao ----------
$def = @(AllPages '')
Check 'sem sort: mais recente primeiro (id decrescente)' ((Sorted @($def | ForEach-Object { '{0:D12}' -f $_.id }) $true)) "$($def.Count) itens"

$keys = @{ user = 'user'; host = 'hostname'; app = 'title'; action = 'action'; status = 'status' }
foreach ($k in $keys.Keys) {
    $field = $keys[$k]
    $asc = @(AllPages "sort=$k&dir=asc")
    Check "sort=$k asc: ordem certa nas $([Math]::Ceiling($total / 10)) paginas" ((Sorted @($asc | ForEach-Object { $_.$field })) -and $asc.Count -eq $total) "itens=$($asc.Count)"
    $desc = @(AllPages "sort=$k&dir=desc")
    Check "sort=$k desc: ordem inversa" ((Sorted @($desc | ForEach-Object { $_.$field }) $true) -and $desc.Count -eq $total)
}
# PowerShell 5.1 pode devolver a data como texto ou como DateTime: normaliza para ISO antes de comparar.
function Iso($v) { ([datetime]$v).ToUniversalTime().ToString('o') }
$asc = @(AllPages 'sort=created&dir=asc')
Check 'sort=created asc: do mais antigo ao mais novo' ((Sorted @($asc | ForEach-Object { Iso $_.createdUtc })) -and $asc.Count -eq $total)
$desc = @(AllPages 'sort=created&dir=desc')
Check 'sort=created desc' ((Sorted @($desc | ForEach-Object { Iso $_.createdUtc }) $true))
$idsAsc = @(@(AllPages 'sort=user&dir=asc') | ForEach-Object id | Sort-Object -Unique)
Check 'ordenar nao repete nem perde pedidos entre paginas' ($idsAsc.Count -eq $total) "unicos=$($idsAsc.Count) de $total"

# ---------- Valores invalidos ----------
$r = Hist 'sort=nao_existe&dir=asc&size=10'
Check 'sort desconhecido -> 200, na ordem padrao' ($r.Code -eq 200 -and (Sorted @(@(Items $r.Body.items) | ForEach-Object { '{0:D12}' -f $_.id }) $true))
$r = Hist ('sort=' + (Enc 'id; DROP TABLE requests; --') + '&size=10')
Check 'sort com SQL -> 200 e nada acontece' ($r.Code -eq 200 -and $r.Body.total -eq $total)
Check 'a tabela continua la depois' ((Hist 'size=10').Body.total -eq $total)
$r = Hist 'sort=user&dir=banana&size=10'
Check 'dir invalido vale como desc' ($r.Code -eq 200 -and (Sorted @(@(Items $r.Body.items) | ForEach-Object user) $true))

# ---------- Filtros ----------
$r = Hist 'status=failed&size=500'
$failed = @(Items $r.Body.items)
Check 'status=failed' ($failed.Count -gt 0 -and -not ($failed | Where-Object status -ne 'failed')) "itens=$($failed.Count)"
Check 'codigo de saida vem nos itens (failed = 1603)' (-not ($failed | Where-Object { $_.exitCode -ne 1603 }))
$ok = @(Items (Hist 'status=success&size=500').Body.items)
Check 'codigo de saida 0 nos concluidos' ($ok.Count -gt 0 -and -not ($ok | Where-Object { $null -eq $_.exitCode -or $_.exitCode -ne 0 }))
$to = @(Items (Hist 'status=timeout&size=500').Body.items)
Check 'sem codigo de saida quando nao houve (timeout)' ($to.Count -gt 0 -and -not ($to | Where-Object { $null -ne $_.exitCode }))
$union = @(Items (Hist 'status=failed,timeout&size=500').Body.items)
Check 'status com varias situacoes (failed,timeout) soma as duas' ($union.Count -eq $failed.Count + $to.Count -and -not ($union | Where-Object { $_.status -notin 'failed', 'timeout' })) "itens=$($union.Count) = $($failed.Count) + $($to.Count)"
$r = Hist 'status=failed,,%20timeout%20,failed&size=500'
Check 'virgulas sobrando e repeticao sao ignoradas' ($r.Code -eq 200 -and $r.Body.total -eq $union.Count)
$r = Hist 'status=,&size=10'
Check 'status so com virgulas = sem filtro' ($r.Code -eq 200 -and $r.Body.total -eq $total)
$r = Hist ('status=' + (Enc "failed' OR '1'='1") + '&size=10')
Check 'status com SQL nao devolve tudo' ($r.Code -eq 200 -and $r.Body.total -eq 0)
$r = Hist 'action=uninstall&size=500'
Check 'action=uninstall' (@(Items $r.Body.items).Count -gt 0 -and -not (@(Items $r.Body.items) | Where-Object action -ne 'uninstall'))
$r = Hist 'host=pc-sp-203&size=500'
Check 'host (nome exato)' (@(Items $r.Body.items).Count -gt 0 -and -not (@(Items $r.Body.items) | Where-Object hostname -cne 'pc-sp-203')) "itens=$(@(Items $r.Body.items).Count)"
$r = Hist ('text=' + (Enc 'Erro 1603') + '&size=500')
Check 'text procura na mensagem' (@(Items $r.Body.items).Count -eq $failed.Count) "itens=$(@(Items $r.Body.items).Count)"
$r = Hist ('text=' + (Enc 'bruno.lima') + '&size=500')
Check 'text procura no usuario sem diferenciar maiusculas' (@(Items $r.Body.items).Count -ge 10) "itens=$(@(Items $r.Body.items).Count)"
$r = Hist ('text=' + (Enc "'; DROP TABLE requests; --") + '&size=10')
Check 'text com SQL e so texto' ($r.Code -eq 200 -and $r.Body.total -eq 0)
$r = Hist 'from=2026-09-05&to=2026-09-05&size=500'
$day = @(Items $r.Body.items)
Check 'from=to traz o dia inteiro' ($day.Count -gt 0 -and -not ($day | Where-Object { ([datetime]$_.createdUtc).ToLocalTime().Date -ne [datetime]'2026-09-05' })) "itens=$($day.Count)"
$r = Hist 'from=2026-09-10&to=2026-09-05&size=10'
Check 'de depois de ate -> vazio, sem erro' ($r.Code -eq 200 -and $r.Body.total -eq 0)
$r = Hist 'from=lixo&to=tambem&size=10'
Check 'data invalida e ignorada' ($r.Code -eq 200 -and $r.Body.total -eq $total)
$r = Hist 'status=failed&action=install&host=PC-HQ-101&size=500'
Check 'filtros combinados' ($r.Code -eq 200 -and -not (@(Items $r.Body.items) | Where-Object { $_.status -ne 'failed' -or $_.action -ne 'install' -or $_.hostname -ne 'PC-HQ-101' }))

# ---------- Paginacao ----------
$r = Hist 'page=999&size=10'
Check 'pagina alem do fim -> vazia, com o total certo' ($r.Code -eq 200 -and @(Items $r.Body.items).Count -eq 0 -and $r.Body.total -eq $total)
$r = Hist 'page=0&size=10'
Check 'pagina 0 vale como 1' ($r.Body.page -eq 1)

# ---------- Visao geral: os numeros batem com o historico ----------
$day0 = Get-Date -Format 'yyyy-MM-dd'
$day6 = (Get-Date).AddDays(-6).ToString('yyyy-MM-dd')
$day8 = (Get-Date).AddDays(-8).ToString('yyyy-MM-dd')
$ov = (Call GET '/api/admin/overview').Body
Check 'visao geral: pedidos hoje = historico de hoje' ($ov.requestsToday -ge 3 -and $ov.requestsToday -eq (Hist "from=$day0&size=10").Body.total) "hoje=$($ov.requestsToday)"
$problems7 = (Hist "status=failed,timeout&from=$day6&size=10").Body.total
Check 'visao geral: falhas de 7 dias = historico (falhou ou sem retorno, de hoje-6 em diante)' ($ov.failures7d -ge 2 -and $ov.failures7d -eq $problems7) "kpi=$($ov.failures7d) historico=$problems7"
Check 'a falha de 8 dias atras nao entra nos 7 dias' ((Hist "status=failed,timeout&from=$day8&size=10").Body.total -eq $problems7 + 1)
Check 'pedidos de 7 dias = historico desde hoje-6' ($ov.requests7d -eq (Hist "from=$day6&size=10").Body.total) "kpi=$($ov.requests7d)"
$perDay = @(Items $ov.perDay)
Check 'grafico: 14 dias, o ultimo e hoje' ($perDay.Count -eq 14 -and $perDay[-1].day -eq (Get-Date).ToString('dd/MM')) "ultimo=$($perDay[-1].day)"
Check 'grafico: soma de hoje = pedidos hoje' (($perDay[-1].success + $perDay[-1].failed + $perDay[-1].denied) -eq $ov.requestsToday)
Check 'ultimos pedidos: ate 10 e com a saida do Chocolatey' (@(Items $ov.recent).Count -le 10 -and @(Items $ov.recent | Where-Object { $_.output }).Count -gt 0)

# ---------- CSV ----------
$r = Call GET '/api/admin/history.csv?sort=user&dir=asc&status=failed'
Check 'CSV responde 200' ($r.Code -eq 200)
Check 'CSV tem BOM UTF-8 (Excel)' ($r.Raw[0] -eq 0xEF -and $r.Raw[1] -eq 0xBB -and $r.Raw[2] -eq 0xBF)
$text = [Text.Encoding]::UTF8.GetString($r.Raw).TrimStart([char]0xFEFF)
$lines = @($text -split "`r?`n" | Where-Object { $_ })
Check 'CSV: cabecalho e uma linha por pedido filtrado' ($lines[0] -like 'Data;Usu*rio;Computador;*' -and $lines.Count - 1 -eq $failed.Count) "linhas=$($lines.Count - 1) esperado=$($failed.Count)"
$csvUsers = @($lines | Select-Object -Skip 1 | ForEach-Object { ($_ -split ';')[1].Trim('"') })
Check 'CSV sai na mesma ordem pedida (usuario crescente)' (Sorted $csvUsers) "primeiro=$($csvUsers[0]) ultimo=$($csvUsers[-1])"

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
