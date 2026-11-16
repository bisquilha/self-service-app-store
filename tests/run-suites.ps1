<#
.SYNOPSIS
    Roda todas as suites de teste da API, cada uma num servidor de demonstracao NOVO, com pasta de dados NOVA.

.DESCRIPTION
    Para cada suite: apaga e recria a pasta de dados, sobe o servidor (Development, Tactical simulado, porta 5080), roda o
    script e para o servidor. No fim imprime um resumo (OK, FALHA e PULOU por suite) e a saida de cada uma fica em -Saida.
    Termina com o total de falhas como codigo de saida.

    Pre-requisitos: PC no dominio (as suites de grupos leem o AD), internet (busca no Chocolatey), python no PATH
    (suites que mexem no banco), e uma pasta -Feed com os pacotes .nupkg de teste "app-interno" e "teste-falha".
    Nao toca em nada fora de -Temp (pastas de dados e logs). Nao publica nada.

.PARAMETER Feed    Pasta com os .nupkg de teste do feed interno (obrigatorio).
.PARAMETER Temp    Onde ficam as pastas de dados, os logs e a saida de cada suite (padrao: uma pasta no TEMP).
.PARAMETER Dotnet  Caminho do dotnet.exe (padrao: %LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe, senao o do PATH).
.PARAMETER Only    Nomes das suites a rodar (padrao: todas). Ex.: -Only test-programas,test-saude-catalogo

.EXAMPLE
    .\run-suites.ps1 -Feed C:\temp\loja-pacotes
#>
param(
    [Parameter(Mandatory)][string]$Feed,
    [string]$Temp = (Join-Path $env:TEMP 'loja-suites'),
    [string]$Dotnet = '',
    [string[]]$Only = @()
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here '..\src\Loja.Server\Loja.Server.csproj'
if (-not $Dotnet) {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    $Dotnet = if (Test-Path $local) { $local } else { 'dotnet' }
}
# Pasta curta: o servidor grava arquivos dentro dela e o Windows nao le a versao de um .exe com caminho de mais de 260 letras.
if ($Temp.Length -gt 120) { throw "Use uma pasta -Temp curta (menos de 120 letras). Esta tem $($Temp.Length)." }
New-Item -ItemType Directory -Force $Temp | Out-Null
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

$suites = @(
    @{ name = 'test-api';                 pass = @() },
    @{ name = 'test-fase2';               pass = @() },
    @{ name = 'test-grupos';              pass = @('DataDir') },
    @{ name = 'test-grupos-sid';          pass = @('DataDir') },
    @{ name = 'test-auditoria';           pass = @('DataDir') },
    @{ name = 'test-lote-grupos';         pass = @('DataDir') },
    @{ name = 'test-configuracoes';       pass = @('DataDir') },
    @{ name = 'test-config';              pass = @('DataDir') },
    @{ name = 'test-historico';           pass = @('DataDir') },
    @{ name = 'test-programas';           pass = @('DataDir') },
    @{ name = 'test-programas-callback';  pass = @('LogFile'); manual = $true },
    @{ name = 'test-maquinas-reexecucao'; pass = @('DataDir') },
    @{ name = 'test-saude-catalogo';      pass = @('DataDir') },
    @{ name = 'test-feed-diagnostico';    pass = @('Esperado'); scenarios = @(
        @{ esperado = 'acesso';   feed = 'C:\Windows\System32\config' },
        @{ esperado = 'naoachou'; feed = 'C:\loja-pasta-que-nao-existe-xyz' },
        @{ esperado = 'semnome';  feed = '\\nome-que-nao-resolve-xyz.invalid\loja' },
        @{ esperado = 'ok';       feed = $Feed }) }
)
# Com -File o PowerShell nao separa "a,b" em lista: separa aqui.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Only.Count -gt 0) { $suites = @($suites | Where-Object { $Only -contains $_.name }) }
if ($suites.Count -eq 0) { throw 'Nenhuma suite com esse nome. Veja a lista no inicio de run-suites.ps1.' }

function Stop-Server {
    foreach ($c in @(Get-NetTCPConnection -State Listen -LocalPort 5080 -ErrorAction SilentlyContinue)) {
        $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'Loja.Server') { Stop-Process -Id $p.Id -Force }
    }
    Start-Sleep -Milliseconds 700
}

Stop-Server
$build = & $Dotnet build $project -nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { $build | Select-String 'error' | Select-Object -First 10; throw 'O build falhou.' }

$summary = @()
$totalFails = 0
foreach ($s in $suites) {
    foreach ($sc in $(if ($s.scenarios) { $s.scenarios } else { @(@{ esperado = ''; feed = $Feed }) })) {
        $label = if ($sc.esperado) { "$($s.name):$($sc.esperado)" } else { $s.name }
        $data = Join-Path $Temp ('dados-' + ($label -replace ':', '-'))
        if (Test-Path $data) { Remove-Item $data -Recurse -Force }
        New-Item -ItemType Directory $data | Out-Null
        $log = Join-Path $Temp 'servidor.log'

        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:Loja__DataDir = $data
        $env:Loja__InternalFeedPath = $sc.feed
        if ($s.manual) { $env:Loja__Tactical__MockManualCallback = 'true' }
        try {
            Start-Process -FilePath $Dotnet -ArgumentList @('run', '--no-build', '--project', "`"$project`"") `
                -RedirectStandardOutput $log -RedirectStandardError (Join-Path $Temp 'servidor.err') -WindowStyle Hidden | Out-Null
        }
        finally {
            Remove-Item Env:Loja__Tactical__MockManualCallback -ErrorAction SilentlyContinue
        }
        for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 700; try { Invoke-WebRequest 'http://localhost:5080/' -UseBasicParsing -TimeoutSec 3 | Out-Null; break } catch { } }

        $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here ($s.name + '.ps1')), '-Base', 'http://localhost:5080')
        if ($s.pass -contains 'DataDir') { $argv += @('-DataDir', $data) }
        if ($s.pass -contains 'LogFile') { $argv += @('-LogFile', $log) }
        if ($s.pass -contains 'Esperado') { $argv += @('-Esperado', $sc.esperado) }
        # Aviso no stderr (ex.: DeprecationWarning do Python) nao pode derrubar o run-suites.
        $ErrorActionPreference = 'Continue'
        $out = & $ps @argv 2>&1 | ForEach-Object { "$_" }
        $ErrorActionPreference = 'Stop'
        $file = Join-Path $Temp ('saida-' + ($label -replace ':', '-') + '.txt')
        $out | Set-Content $file -Encoding UTF8
        Stop-Server

        $fails = @($out | Where-Object { $_ -like 'FALHA*' })
        $totalFails += $fails.Count
        $summary += '{0,-34} OK={1} FALHA={2} PULOU={3}' -f $label, @($out | Where-Object { $_ -like 'OK *' }).Count, $fails.Count, @($out | Where-Object { $_ -like 'PULOU*' }).Count
        foreach ($f in $fails) { $summary += "    $f" }
    }
}
Remove-Item Env:ASPNETCORE_ENVIRONMENT, Env:Loja__DataDir, Env:Loja__InternalFeedPath -ErrorAction SilentlyContinue
$summary
"----- $totalFails falha(s) no total. Saidas em $Temp"
exit $totalFails
