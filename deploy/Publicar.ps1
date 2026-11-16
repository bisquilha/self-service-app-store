<#
.SYNOPSIS
    Gera os pacotes para implantar a Loja de Aplicativos (rodar no PC de desenvolvimento).

.DESCRIPTION
    publicar\servidor  -> copiar para a VM e instalar com Install-LojaServer.ps1
    publicar\app       -> LojaDeAplicativos.exe + loja.json (distribuídos por GPO)
    publicar\tactical  -> Loja-Choco.ps1 (colar no Script Manager do Tactical)

.EXAMPLE
    .\Publicar.ps1 -ServerUrl https://loja.corp.example.com -CertSha256 <SHA256-do-certificado>
#>
param(
    [string]$ServerUrl = 'https://loja.SEUDOMINIO.local',
    [string]$CertSha256 = '',
    [string]$Saida = (Join-Path $PSScriptRoot '..\publicar')
)
$ErrorActionPreference = 'Stop'

$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (Test-Path $Saida) { Remove-Item $Saida -Recurse -Force }
New-Item -ItemType Directory -Force "$Saida\tactical" | Out-Null

Write-Host 'Painel web de administração (Vite/React) -> wwwroot\admin...' -ForegroundColor Cyan
& "$PSScriptRoot\Build-Admin.ps1"

Write-Host 'Servidor...' -ForegroundColor Cyan
& $dotnet publish "$raiz\src\Loja.Server\Loja.Server.csproj" -c Release -r win-x64 --self-contained false -o "$Saida\servidor" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o servidor.' }

Write-Host 'App (arquivo único, não precisa de .NET instalado nas máquinas)...' -ForegroundColor Cyan
& $dotnet publish "$raiz\src\Loja.App\Loja.App.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o "$Saida\app" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o app.' }

# O app viaja dentro da atualização do servidor (pasta "cliente"): ao subir, o servidor o oferece às estações sozinho.
New-Item -ItemType Directory -Force "$Saida\servidor\cliente" | Out-Null
Copy-Item "$Saida\app\LojaDeAplicativos.exe" "$Saida\servidor\cliente\LojaDeAplicativos.exe"

@{ ServerUrl = $ServerUrl; CertSha256 = $CertSha256 } | ConvertTo-Json | Set-Content "$Saida\app\loja.json" -Encoding UTF8
Copy-Item "$raiz\tactical\Loja-Choco.ps1" "$Saida\tactical\"

Write-Host ''
Write-Host "Pronto em $((Resolve-Path $Saida).Path)" -ForegroundColor Green
Get-ChildItem "$Saida\app" | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
if (-not $CertSha256) {
    Write-Warning 'loja.json sem CertSha256. Preencha com o valor impresso pelo Install-LojaServer.ps1 antes de distribuir.'
}
