<#
.SYNOPSIS
    Gera o painel de administração (src\Loja.Admin) dentro do wwwroot do servidor (wwwroot\admin).

.DESCRIPTION
    Roda só no PC de desenvolvimento (precisa do Node 20.19+ ou 22.12+). O servidor de produção não precisa de Node:
    ele recebe os arquivos já prontos no pacote. O Publicar.ps1 chama este script antes de publicar o servidor.
    As dependências só são reinstaladas (npm ci) quando faltam ou quando o package-lock.json mudou.
    A pasta wwwroot\admin é recriada a cada execução: não guarde nada dentro dela (o painel antigo está arquivado em legado\).

.EXAMPLE
    .\Build-Admin.ps1                # instala o que faltar e gera
    .\Build-Admin.ps1 -Reinstalar    # reinstala as dependências do zero e gera
#>
param([switch]$Reinstalar)
$ErrorActionPreference = 'Stop'

$raiz = Resolve-Path (Join-Path $PSScriptRoot '..')
$admin = Join-Path $raiz 'src\Loja.Admin'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js não encontrado. Instale o Node 20.19+ (ou 22.12+) no PC de desenvolvimento.'
}
$versao = [version]((node --version).Trim().TrimStart('v'))
if (-not ($versao -ge [version]'22.12.0' -or ($versao.Major -eq 20 -and $versao -ge [version]'20.19.0'))) {
    throw "O Node $versao é antigo demais para o painel: precisa do 20.19+ ou do 22.12+."
}

$env:npm_config_update_notifier = 'false'
Push-Location $admin
try {
    $lock = Join-Path $admin 'package-lock.json'
    $marca = Join-Path $admin 'node_modules\.package-lock.json'
    $instalar = $Reinstalar -or -not (Test-Path $marca) -or ((Get-Item $lock).LastWriteTime -gt (Get-Item $marca).LastWriteTime)
    if ($instalar) {
        Write-Host 'Instalando as dependências do painel (npm ci)...' -ForegroundColor Cyan
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci falhou.' }
    }

    Write-Host 'Gerando o painel de administração (tsc + vite)...' -ForegroundColor Cyan
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o painel de administração.' }
}
finally {
    Pop-Location
}

Write-Host "Painel pronto em $(Join-Path $raiz 'src\Loja.Server\wwwroot\admin')" -ForegroundColor Green
