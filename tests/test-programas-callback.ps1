<#
.SYNOPSIS
    Testa o contrato do retorno do script (/api/callback) com a lista de programas do Windows (servidor 1.6.0+):
    script antigo, script novo, texto hostil, limites, token de uso unico e a funcao REAL do script rodando neste PC.

.DESCRIPTION
    Rodar com o servidor de demonstracao em modo de retorno MANUAL (o servidor nao conclui sozinho e escreve o token no log):

        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:Loja__Tactical__MockManualCallback = 'true'
        $env:Loja__DataDir = 'C:\temp\loja-dados'      # pasta nova e vazia
        dotnet run --project ..\src\Loja.Server\Loja.Server.csproj *> C:\temp\loja.log

    -LogFile  arquivo que recebe a saida do servidor (de onde sai o token de cada pedido).
    O script le a funcao Get-WindowsPrograms de tactical\Loja-Choco.ps1 e a executa de verdade (SOMENTE LEITURA do registro).

.EXAMPLE
    .\test-programas-callback.ps1 -LogFile C:\temp\loja.log
#>
param([string]$Base = 'http://localhost:5080', [string]$LogFile = '')
$ErrorActionPreference = 'Stop'
$script:fails = 0
$script:skips = 0
$pc = $env:COMPUTERNAME

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
# O retorno do script e anonimo (o token e a autenticacao): sem credenciais e sem o cabecalho do painel.
function Callback($payload) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress -Depth 5))
    try {
        $r = Invoke-WebRequest -Uri "$Base/api/callback" -Method POST -Body $bytes -ContentType 'application/json; charset=utf-8' -UseBasicParsing
        return [int]$r.StatusCode
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        throw
    }
}
function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}
function Skip($name, $why) { Write-Output "PULOU $name ($why)"; $script:skips++ }

# Pede uma consulta ao PC e devolve o token que o servidor escreveu no log.
function NewToken() {
    $seen = @()
    if (Test-Path $LogFile) { $seen = @([regex]::Matches("$(Get-Content $LogFile -Raw -ErrorAction SilentlyContinue)", 'LOJA_TOKEN=([0-9A-F]{64})') | ForEach-Object { $_.Groups[1].Value }) }
    $r = Call POST "/api/admin/machines/$pc/action" @{ action = 'list' }
    if ($r.Code -ne 200) { throw "nao consegui pedir a consulta: $($r.Body.message)" }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $now = @([regex]::Matches("$(Get-Content $LogFile -Raw -ErrorAction SilentlyContinue)", 'LOJA_TOKEN=([0-9A-F]{64})') | ForEach-Object { $_.Groups[1].Value })
        if ($now.Count -gt $seen.Count) { return $now[-1] }
    }
    throw 'o token nao apareceu no log do servidor'
}
function Machine() { (Call GET "/api/admin/machines/$pc").Body }

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

if (-not $LogFile -or -not (Test-Path $LogFile)) {
    Skip 'tudo' 'informe -LogFile com a saida do servidor em modo MockManualCallback'
    Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
    exit $script:fails
}

# ---------- O script real: funcao Get-WindowsPrograms ----------
$scriptPath = Join-Path $PSScriptRoot '..\tactical\Loja-Choco.ps1'
$source = [IO.File]::ReadAllText($scriptPath)
Check 'script: so caracteres ASCII (o PowerShell 5.1 le sem BOM como ANSI)' (-not ($source.ToCharArray() | Where-Object { [int]$_ -gt 127 } | Select-Object -First 1))
$m = [regex]::Match($source, '(?s)function Get-WindowsPrograms \{.*?\r?\n\}\r?\n')
Check 'script: funcao Get-WindowsPrograms encontrada' $m.Success
Check 'script: so le o registro (nenhum comando que grava ou apaga em HKLM/HKCU)' ($m.Value -notmatch 'Set-ItemProperty|New-Item|Remove-Item|Remove-ItemProperty|New-ItemProperty|reg\s+(add|delete)|Start-Process|msiexec|Uninstall-Package')
Check 'script: consulta as duas chaves (64 e 32 bits)' ($m.Value -match 'CurrentVersion\\Uninstall' -and $m.Value -match 'WOW6432Node')
Invoke-Expression $m.Value
$real = Get-WindowsPrograms
$lines = @($real -split "`n" | Where-Object { $_ -ne '' })
Check 'script: devolveu programas deste PC' ($lines.Count -gt 0) "n=$($lines.Count)"
Check 'script: cada linha tem exatamente um TAB (nome<TAB>versao)' (@($lines | Where-Object { ($_ -split "`t").Count -ne 2 }).Count -eq 0)
Check 'script: nenhum caractere de controle nas linhas (alem do TAB)' (@($lines | Where-Object { ($_ -replace "`t", '') -match '[\p{C}]' }).Count -eq 0)
Check 'script: nenhuma linha repetida' (($lines | Sort-Object -Unique).Count -eq $lines.Count)
Check 'script: sem atualizacoes do Windows (KB...) na lista' (@($lines | Where-Object { $_ -match '^(KB\d+|Update for |Security Update for |Hotfix for )' }).Count -eq 0)
Check 'script: no maximo 1500 programas' ($lines.Count -le 1500)

# ---------- Retorno de script ANTIGO (sem InstalledWindows) ----------
$d0 = Machine
Check 'inicio: maquina sem programas coletados' ($null -eq $d0.programs -and $null -eq $d0.programsUtc)
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = "chocolatey|2.4.3`n7zip|1.0" }
Check 'script antigo (so Chocolatey) -> 200' ($code -eq 200)
$d = Machine
Check '... a lista do Chocolatey entrou' (@($d.packages | Where-Object { $_.packageId -eq '7zip' }).Count -eq 1)
Check '... programas do Windows continuam NAO coletados (nada afirmado)' ($null -eq $d.programs -and $null -eq $d.programsUtc)

# ---------- Retorno do script NOVO com a lista real ----------
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = "chocolatey|2.4.3`n7zip|1.0"; installedWindows = $real }
Check 'script novo com a lista real -> 200' ($code -eq 200)
$d = Machine
Check '... a data da coleta foi gravada' ($null -ne $d.programsUtc)
Check '... todos os programas chegaram (mesmo numero de linhas)' (@($d.programs).Count -eq $lines.Count) "servidor=$(@($d.programs).Count) script=$($lines.Count)"
Check '... o Chocolatey continua junto' (@($d.packages | Where-Object { $_.packageId -eq '7zip' }).Count -eq 1)
$firstProgramsUtc = $d.programsUtc

# ---------- Texto hostil e limites ----------
$ctl = [string][char]7
$long200 = 'N' * 200
$long201 = 'N' * 201
$ver50 = '1' * 50
$ver51 = '1' * 51
$hostile = @(
    "Bom Programa`t1.0",
    "bom programa`t1.0",                      # repetido (so muda a caixa): vira um so
    "Com${ctl}Controle`t2.0${ctl}",           # controle some
    "$long200`t1",                            # 200 letras: cabe
    "$long201`t1",                            # 201: descartado
    "Versao Longa`t$ver50",                   # 50: cabe
    "Versao Longa Demais`t$ver51",            # 51: descartado
    "Tres`tpartes`textras",                   # 3 campos: descartado
    "`t1.0",                                  # sem nome: descartado
    "   `t1.0",                               # nome so de espacos: descartado
    "Sem Versao",                             # sem TAB: nome sem versao
    "<script>alert(1)</script>`t1.0",         # texto de verdade, nunca HTML
    "=cmd|' /C calc'!A0`t1.0"                 # formula de planilha: so texto
) -join "`n"
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = $hostile }
Check 'lista hostil -> 200 (o servidor aceita e limpa)' ($code -eq 200)
$d = Machine
$byName = @{}
foreach ($p in @($d.programs)) { $byName[$p.name] = $p.version }
Check '... o repetido (so a caixa muda) virou um so' ((@($d.programs | Where-Object { $_.name -like 'bom programa' })).Count -eq 1)
Check '... caractere de controle saiu do nome e da versao' ($byName.ContainsKey('ComControle') -and $byName['ComControle'] -eq '2.0')
Check '... nome de 200 letras ficou; de 201 foi descartado' ($byName.ContainsKey($long200) -and -not $byName.ContainsKey($long201))
Check '... versao de 50 ficou; de 51 foi descartada' ($byName.ContainsKey('Versao Longa') -and -not $byName.ContainsKey('Versao Longa Demais'))
Check '... linha com 3 campos, sem nome e so espacos foram descartadas' (-not $byName.ContainsKey('Tres') -and -not $byName.ContainsKey('') -and -not $byName.ContainsKey('   '))
Check '... sem TAB: guarda o nome com versao vazia' ($byName.ContainsKey('Sem Versao') -and $byName['Sem Versao'] -eq '')
Check '... HTML e formula ficam como texto puro, sem alteracao' ($byName.ContainsKey('<script>alert(1)</script>') -and $byName.ContainsKey("=cmd|' /C calc'!A0"))
Check '... a lista anterior foi SUBSTITUIDA (nao acumula)' (@($d.programs).Count -eq 7) "n=$(@($d.programs).Count)"
Check '... o Chocolatey vazio zerou os pacotes (lista nova substitui)' (@($d.packages).Count -eq 0)

# teto de programas por maquina
$many = (1..2600 | ForEach-Object { "Programa $_`t1.$_" }) -join "`n"
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = $many }
$d = Machine
Check 'mais de 2000 programas: o servidor guarda no maximo 2000' ($code -eq 200 -and @($d.programs).Count -eq 2000) "n=$(@($d.programs).Count)"

# ---------- Script antigo depois do novo: nao apaga o que ja foi coletado ----------
$t = NewToken
$before = (Machine).programsUtc
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = "chocolatey|2.4.3" }
$d = Machine
Check 'retorno de script antigo depois do novo -> 200' ($code -eq 200)
Check '... nao apaga os programas ja coletados nem muda a data' (@($d.programs).Count -eq 2000 -and $d.programsUtc -eq $before)

# ---------- Coleta vazia de verdade e coleta que falhou ----------
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = '' }
$d = Machine
Check 'coleta que achou ZERO programas (texto vazio): lista vazia e data atualizada' ($code -eq 200 -and @($d.programs).Count -eq 0 -and $null -ne $d.programsUtc) "n=$(@($d.programs).Count)"
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = $null }
Check 'coleta que FALHOU (campo nulo): trata como "nao coletou" e nao mexe' ($code -eq 200 -and $null -ne (Machine).programsUtc)

# ---------- Token ----------
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = 'Um Programa' }
$again = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = 'Outro' }
Check 'token de uso unico: o segundo uso -> 404' ($code -eq 200 -and $again -eq 404)
Check '... e o segundo uso nao alterou a lista' (((@((Machine).programs) | ForEach-Object { $_.name }) -join ',') -eq 'Um Programa')
Check 'token malformado -> 404' ((Callback @{ token = 'abc'; exitCode = 0; output = ''; installedWindows = 'X' }) -eq 404)
$t = NewToken
$code = Callback @{ token = $t; exitCode = 0; output = 'ok'; installed = ''; installedWindows = 'Campo Extra'; campoQueNaoExiste = 123 }
Check 'campo desconhecido no JSON e ignorado (compatibilidade com versoes futuras)' ($code -eq 200)

Write-Output "----- $script:fails falha(s), $script:skips pulado(s)"
exit $script:fails
