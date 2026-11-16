# Testa a atualizacao do app da loja "direto pelo servidor": o LojaDeAplicativos.exe viaja com o servidor (Loja:ClientBundlePath)
# e, ao subir, vira a versao atual oferecida as estacoes; sem ninguem publicar nada pelo painel. Sobe o proprio servidor (porta 5083).
# Precisa de dois executaveis de versoes diferentes (padrao: Downloads\LojaDeAplicativos-1.3.0.exe e -1.4.0.exe); sem eles, pula.
param(
    [string]$Exe130 = (Join-Path $env:USERPROFILE 'Downloads\LojaDeAplicativos-1.3.0.exe'),
    [string]$Exe140 = (Join-Path $env:USERPROFILE 'Downloads\LojaDeAplicativos-1.4.0.exe')
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$raiz = Split-Path -Parent $here
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$port = 5083; $Base = "http://localhost:$port"
$work = Join-Path $env:TEMP 'loja-app-test'
$script:fails = 0

function Call($method, $path) {
    $p = @{ Uri = "$Base$path"; Method = $method; UseBasicParsing = $true; UseDefaultCredentials = $true; Headers = @{ 'X-Requested-With' = 'loja-admin' } }
    try {
        $r = Invoke-WebRequest @p
        $json = $null
        if ($r.Headers['Content-Type'] -like 'application/json*') { $json = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray()) | ConvertFrom-Json }
        return [pscustomobject]@{ Code = [int]$r.StatusCode; Body = $json; Raw = $r.RawContentStream }
    } catch { $resp = $_.Exception.Response; if (-not $resp) { throw }; return [pscustomobject]@{ Code = [int]$resp.StatusCode; Body = $null; Raw = $null } }
}
function Check($name, $cond, $detail = '') { if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ } }
function Wait-Until([scriptblock]$cond, [int]$seconds) { for ($i = 0; $i -lt $seconds; $i++) { if (& $cond) { return $true }; Start-Sleep -Seconds 1 }; return $false }
$script:server = $null
function Start-Server($bundle, $mandatory) {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Urls = $Base; $env:Loja__DataDir = "$work\data"
    $env:Loja__ClientBundlePath = $bundle; $env:Loja__ClientUpdateMandatory = "$mandatory"
    $script:server = Start-Process $dotnet -ArgumentList "`"$serverDll`"" -WorkingDirectory (Split-Path $serverDll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$work\server.out.log" -RedirectStandardError "$work\server.err.log"
    if (-not (Wait-Until { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; $true } catch { $false } } 40)) { throw 'O servidor nao subiu.' }
}
function Stop-Server { if ($script:server -and -not $script:server.HasExited) { Stop-Process -Id $script:server.Id -Force }; Start-Sleep -Seconds 1 }

if (-not (Test-Path $Exe130) -or -not (Test-Path $Exe140)) { Write-Output "PULOU (faltam $Exe130 e/ou $Exe140)"; exit 0 }
& $dotnet build "$raiz\src\Loja.Server\Loja.Server.csproj" -c Debug -nologo -v q | Out-Null
$serverDll = Join-Path $raiz 'src\Loja.Server\bin\Debug\net8.0\Loja.Server.dll'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$work\data" -Force | Out-Null
$sha130 = (Get-FileHash $Exe130 -Algorithm SHA256).Hash
$sha140 = (Get-FileHash $Exe140 -Algorithm SHA256).Hash

try {
    Write-Output '=== Servidor sem o app junto: nada muda ==='
    Start-Server "$work\nao-existe.exe" $false
    Check 'sem versao publicada, o servidor nao oferece nada (204)' ((Call GET '/api/client/latest').Code -eq 204)
    Check 'a lista de versoes do painel esta vazia' (@((Call GET '/api/admin/client/releases').Body).Count -eq 0)
    Stop-Server

    Write-Output '=== Servidor chega com o app 1.3.0 dentro ==='
    Start-Server $Exe130 $false
    $l = Call GET '/api/client/latest'
    Check 'a 1.3.0 passou a ser a versao atual sozinha' ($l.Code -eq 200 -and $l.Body.version -eq '1.3.0' -and $l.Body.sha256 -eq $sha130) "versao=$($l.Body.version)"
    Check 'nao e obrigatoria por padrao' ($l.Body.mandatory -eq $false)
    $d = Call GET '/api/client/download/1.3.0'
    $tmp = Join-Path $work 'baixado.exe'; [IO.File]::WriteAllBytes($tmp, $d.Raw.ToArray())
    Check 'o download entrega o mesmo arquivo (SHA-256 igual)' ($d.Code -eq 200 -and (Get-FileHash $tmp -Algorithm SHA256).Hash -eq $sha130)
    $rel = @((Call GET '/api/admin/client/releases').Body)
    Check 'o painel (API) registra quem publicou: a atualizacao do servidor' ($rel.Count -eq 1 -and $rel[0].publishedBy -eq 'atualização do servidor' -and $rel[0].isCurrent)
    Stop-Server

    Write-Output '=== Reiniciar com o mesmo app nao publica de novo ==='
    Start-Server $Exe130 $false
    $rel = @((Call GET '/api/admin/client/releases').Body)
    Check 'continua uma versao so' ($rel.Count -eq 1)
    Stop-Server

    Write-Output '=== Atualizar o servidor com o app 1.4.0 ==='
    Start-Server $Exe140 $true
    $l = Call GET '/api/client/latest'
    Check 'a 1.4.0 virou a atual e obrigatoria (configuracao do servidor)' ($l.Body.version -eq '1.4.0' -and $l.Body.sha256 -eq $sha140 -and $l.Body.mandatory -eq $true) "versao=$($l.Body.version) obrigatoria=$($l.Body.mandatory)"
    $rel = @((Call GET '/api/admin/client/releases').Body)
    Check 'a 1.3.0 fica guardada (historico), so a 1.4.0 e a atual' ($rel.Count -eq 2 -and @($rel | Where-Object isCurrent).Count -eq 1 -and ($rel | Where-Object isCurrent).version -eq '1.4.0')
    Check 'a 1.3.0 ainda pode ser baixada (maquinas que ainda nao atualizaram)' ((Call GET '/api/client/download/1.3.0').Code -eq 200)
    Stop-Server

    Write-Output '=== Arquivo que nao e a loja e recusado sem derrubar o servidor ==='
    $lixo = Join-Path $work 'lixo.exe'; Set-Content $lixo 'MZ nao e a loja'
    Start-Server $lixo $false
    $l = Call GET '/api/client/latest'
    Check 'servidor sobe e segue com a versao anterior (1.4.0)' ($l.Code -eq 200 -and $l.Body.version -eq '1.4.0')
}
finally {
    Stop-Server
    foreach ($n in 'Urls', 'Loja__DataDir', 'Loja__ClientBundlePath', 'Loja__ClientUpdateMandatory', 'ASPNETCORE_ENVIRONMENT') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
}
Write-Output ''
if ($script:fails -eq 0) { Write-Output 'TUDO CERTO'; exit 0 } else { Write-Output "$($script:fails) FALHA(S). Logs em $work"; exit 1 }
