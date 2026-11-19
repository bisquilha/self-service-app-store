<#
.SYNOPSIS
    Gera o pacote de implantacao do agente da loja: um LojaAgent.exe unico (nao exige .NET na estacao),
    o SHA-256 dele e o Instalar-Agente.ps1 ja preenchido.

.EXAMPLE
    .\Publicar-Agente.ps1 -ServerUrl https://loja.corp.example.com -CertSha256 <SHA256-do-certificado> -Origem \\fileserver\share\loja-deploy\agente

    Resultado em publicar\agente:
      LojaAgent.exe            copie para a pasta de rede informada em -Origem
      LojaAgent.exe.sha256     o hash (para conferir)
      Instalar-Agente.ps1      use na GPO (inicializacao do computador), no Tactical, no Intune, ou rode como administrador
#>
param(
    [Parameter(Mandatory)][string]$ServerUrl,
    [string]$CertSha256 = '',
    [Parameter(Mandatory)][string]$Origem,
    [string]$Saida = '',
    [string]$FeedInterno = ''      # pasta dos pacotes internos (ex.: \\fileserver\share\loja-pacotes); vai para o agent.json de cada estacao
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
if (-not $Saida) { $Saida = Join-Path $raiz 'publicar\agente' }
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($ServerUrl -notmatch '^https://' ) { Write-Warning 'ServerUrl sem https: so aceito pelo agente em localhost (testes).' }
if ($ServerUrl -match '^https://' -and $CertSha256 -notmatch '^[0-9A-Fa-f:]{64,95}$') { Write-Warning 'CertSha256 vazio ou fora do padrao: o agente vai usar a validacao normal do Windows.' }

Remove-Item $Saida -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Saida -Force | Out-Null

& $dotnet publish "$raiz\src\Loja.Agent\Loja.Agent.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "$Saida\tmp" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o agente.' }

Move-Item "$Saida\tmp\LojaAgent.exe" "$Saida\LojaAgent.exe" -Force
Remove-Item "$Saida\tmp" -Recurse -Force
$hash = (Get-FileHash "$Saida\LojaAgent.exe" -Algorithm SHA256).Hash
Set-Content "$Saida\LojaAgent.exe.sha256" "$hash  LojaAgent.exe" -Encoding ASCII

$modelo = Get-Content "$PSScriptRoot\Instalar-Agente.ps1" -Raw
$modelo = $modelo.Replace('__ORIGEM__', $Origem).Replace('__SERVER__', $ServerUrl.TrimEnd('/')).Replace('__CERT__', ($CertSha256 -replace '[: ]', '')).Replace('__HASH__', $hash).Replace('__FEED__', $FeedInterno)
Set-Content "$Saida\Instalar-Agente.ps1" $modelo -Encoding ASCII

$versao = (Get-Item "$Saida\LojaAgent.exe").VersionInfo.FileVersion
Write-Host ''
Write-Host "Agente $versao pronto em $Saida" -ForegroundColor Green
Write-Host "  LojaAgent.exe  $([Math]::Round((Get-Item "$Saida\LojaAgent.exe").Length / 1MB, 1)) MB   SHA-256 $hash"
Write-Host ''
Write-Host 'Proximos passos:'
Write-Host "  1. Copie LojaAgent.exe para $Origem"
Write-Host '  2. Implante o Instalar-Agente.ps1 (GPO de inicializacao, Tactical ou manual como administrador)'
Write-Host '  3. No servidor da loja: Loja:Agent:Enabled = true (veja docs\AGENTE.md)'
