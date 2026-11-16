<#
.SYNOPSIS
  Monta um pacote Chocolatey (.nupkg) interno para a Loja de Aplicativos a partir de um instalador (.exe ou .msi).
  NAO instala nada e NAO executa o instalador. Gera so o .nupkg.

.EXAMPLE
  .\Novo-PacoteLoja.ps1 -Instalador "$env:USERPROFILE\Downloads\App-Setup.exe" -Id corp-meuapp -Titulo "Meu App" -Versao 1.0.0 -Tipo inno

  -Tipo: inno (Inno Setup), nsis (Nullsoft), msi. Para descobrir, pergunte a quem gerou o instalador ou rode:
  Select-String -Path .\App-Setup.exe -Pattern 'Inno Setup','Nullsoft' -SimpleMatch -List | Select-Object Pattern
  -Id deve ser DIFERENTE do pacote publico (por isso o prefixo corp-).
#>
param(
    [Parameter(Mandatory)] [string] $Instalador,
    [Parameter(Mandatory)] [string] $Id,
    [Parameter(Mandatory)] [string] $Titulo,
    [Parameter(Mandatory)] [string] $Versao,
    [ValidateSet('inno', 'nsis', 'msi')] [string] $Tipo = 'inno',
    [string] $Autores = 'Minha Empresa',
    [string] $Descricao = '',
    [string] $Tags = 'interno',
    [string] $NomeNoWindows = '',          # regex do nome em "Aplicativos instalados" (para desinstalar). Padrao: o titulo
    [string] $Icone = '',                  # PNG opcional; sem ele, tenta tirar o icone do proprio instalador
    [string] $Saida = (Join-Path $env:USERPROFILE 'Downloads'),
    [string] $ExeAtalho = '',              # nome do .exe do programa (dentro da pasta de instalacao): cria atalho na Area de Trabalho de todos (C:\Users\Public\Desktop) e remove na desinstalacao
    [string] $NomeAtalho = '',             # nome do atalho (padrao: o titulo)
    [switch] $Assinar,                  # assina o instalador (copia dentro do pacote) com o certificado interno e faz o pacote conferir a assinatura
    [string] $AssinaturaNome = 'Minha Empresa - Assinatura Interna'   # nome (CN) do certificado em Cert:\CurrentUser\My
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Instalador)) { throw "Instalador nao encontrado: $Instalador" }
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) { throw 'Chocolatey (choco) nao esta instalado neste PC.' }
if ($Id -notmatch '^[a-z0-9][a-z0-9.-]*$') { throw 'O Id so pode ter letras minusculas, numeros, ponto e hifen.' }
if (-not $NomeNoWindows) { $NomeNoWindows = [regex]::Escape($Titulo) }
if (-not $Descricao) { $Descricao = "$Titulo $Versao. Pacote interno com o instalador original, instalacao silenciosa para todos os usuarios do computador." }

$nomeArquivo = Split-Path $Instalador -Leaf
$dir = Join-Path ([IO.Path]::GetTempPath()) ("pacote-" + $Id)
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory "$dir\tools" | Out-Null
Copy-Item $Instalador "$dir\tools\$nomeArquivo"

# assinatura digital (so na copia que vai no pacote; o arquivo original nao e alterado)
$digital = ''
if ($Assinar) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $PSItem.Subject -eq "CN=$AssinaturaNome" -and $PSItem.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "Certificado 'CN=$AssinaturaNome' nao encontrado em Cert:\CurrentUser\My (rode este script no PC onde ele foi criado)." }
    $r = Set-AuthenticodeSignature -FilePath "$dir\tools\$nomeArquivo" -Certificate $cert -HashAlgorithm SHA256
    if (-not $r.SignerCertificate) { throw "Falha ao assinar: $($r.StatusMessage)" }
    $digital = $cert.Thumbprint
    "Assinado com: $($cert.Subject) ($digital)"
}

# icone que a loja mostra no cartao
$temIcone = $false
try {
    if ($Icone -and (Test-Path $Icone)) { Copy-Item $Icone "$dir\icon.png"; $temIcone = $true }
    else {
        Add-Type -AssemblyName System.Drawing
        $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($Instalador)
        if ($ico) {
            $bmp = New-Object System.Drawing.Bitmap 48, 48
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = 'HighQualityBicubic'
            $g.DrawImage($ico.ToBitmap(), 0, 0, 48, 48)
            $g.Dispose()
            $bmp.Save("$dir\icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            $temIcone = $true
        }
    }
} catch { Write-Warning "Sem icone: $($PSItem.Exception.Message)" }

function Esc([string] $s) { [System.Security.SecurityElement]::Escape($s) }
$arquivosIcone = if ($temIcone) { '    <file src="icon.png" target="icon.png" />' + "`r`n" } else { '' }
$nuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd">
  <metadata>
    <id>$(Esc $Id)</id>
    <version>$(Esc $Versao)</version>
    <title>$(Esc $Titulo)</title>
    <authors>$(Esc $Autores)</authors>
    <description>$(Esc $Descricao)</description>
    <tags>$(Esc $Tags)</tags>
  </metadata>
  <files>
    <file src="tools\**" target="tools" />
$arquivosIcone  </files>
</package>
"@
Set-Content "$dir\$Id.nuspec" $nuspec -Encoding UTF8

# ---------- instalacao ----------
switch ($Tipo) {
    'inno' { $tipoArq = 'exe'; $args = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS' }
    'nsis' { $tipoArq = 'exe'; $args = '/S' }
    'msi'  { $tipoArq = 'msi'; $args = '/qn /norestart' }
}
$install = @'
$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileType       = '@@TIPOARQ@@'
    file           = Join-Path $toolsDir '@@ARQUIVO@@'
    silentArgs     = '@@ARGS@@'
    validExitCodes = @(0, 3010)
}
Install-ChocolateyInstallPackage @packageArgs
'@
$install = $install.Replace('@@TIPOARQ@@', $tipoArq).Replace('@@ARQUIVO@@', $nomeArquivo).Replace('@@ARGS@@', $args)
if ($digital) {
    $verifica = @'

# Confere a assinatura interna antes de rodar o instalador (a cadeia pode nao ser confiavel no PC; o que importa e o arquivo nao ter sido alterado e ser do nosso certificado)
$sig = Get-AuthenticodeSignature $packageArgs.file
if ($sig.Status -in 'NotSigned', 'HashMismatch', 'Incompatible' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne '@@DIGITAL@@') {
    throw "Instalador sem a assinatura interna esperada (status: $($sig.Status)). Instalacao cancelada."
}
'@
    $install = $install.Replace("Install-ChocolateyInstallPackage @packageArgs", ($verifica.Replace('@@DIGITAL@@', $digital) + "`r`nInstall-ChocolateyInstallPackage @packageArgs"))
}
if (-not $NomeAtalho) { $NomeAtalho = $Titulo }
if ($ExeAtalho) {
    $atalho = @'

# Atalho na Area de Trabalho de todos os usuarios (o instalador silencioso nao cria o icone opcional)
$nomeApp = '@@NOMEAPP@@'
$reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $PSItem.DisplayName -and $PSItem.DisplayName -match $nomeApp -and $PSItem.InstallLocation } | Select-Object -First 1
if ($reg) {
    $alvo = Get-ChildItem $reg.InstallLocation -Filter '@@EXEATALHO@@' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($alvo) {
        Install-ChocolateyShortcut -ShortcutFilePath (Join-Path $env:PUBLIC 'Desktop\@@NOMEATALHO@@.lnk') -TargetPath $alvo.FullName -WorkingDirectory $alvo.DirectoryName
    } else { Write-Warning 'Executavel do atalho nao encontrado; atalho nao criado.' }
} else { Write-Warning 'Programa nao encontrado apos instalar; atalho nao criado.' }
'@
    $install = $install + $atalho.Replace('@@NOMEAPP@@', $NomeNoWindows.Replace("'", "''")).Replace('@@EXEATALHO@@', $ExeAtalho).Replace('@@NOMEATALHO@@', $NomeAtalho.Replace("'", "''"))
}
Set-Content "$dir\tools\chocolateyInstall.ps1" $install -Encoding UTF8

# ---------- desinstalacao: acha o programa em "Aplicativos instalados" pelo nome ----------
$uninstall = @'
$ErrorActionPreference = 'Stop'
$nome = '@@NOME@@'
$chaves = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
$item = Get-ItemProperty $chaves -ErrorAction SilentlyContinue |
    Where-Object { $PSItem.DisplayName -and $PSItem.DisplayName -match $nome -and $PSItem.UninstallString } | Select-Object -First 1
if (-not $item) { Write-Warning 'Programa nao encontrado neste computador; nada a desinstalar.'; return }

$tipo = '@@TIPO@@'
if ($tipo -eq 'msi') {
    $guid = [regex]::Match($item.UninstallString, '\{[0-9A-Fa-f-]{36}\}').Value
    if (-not $guid) { throw 'Codigo do MSI nao encontrado.' }
    $packageArgs = @{ packageName = $env:ChocolateyPackageName; fileType = 'msi'; file = $guid; silentArgs = '/qn /norestart'; validExitCodes = @(0, 3010) }
} else {
    $cmd = $item.UninstallString.Trim()
    $exe = if ($cmd.StartsWith('"')) { $cmd.Substring(1, $cmd.IndexOf('"', 1) - 1) } else { $cmd }
    if (-not (Test-Path $exe)) { throw "Desinstalador nao encontrado: $exe" }
    $s = if ($tipo -eq 'inno') { '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' } else { '/S' }
    $packageArgs = @{ packageName = $env:ChocolateyPackageName; fileType = 'exe'; file = $exe; silentArgs = $s; validExitCodes = @(0, 3010) }
}
Uninstall-ChocolateyPackage @packageArgs
'@
$uninstall = $uninstall.Replace('@@NOME@@', $NomeNoWindows.Replace("'", "''")).Replace('@@TIPO@@', $Tipo)
if ($ExeAtalho) {
    $rem = "`r`nRemove-Item (Join-Path `$env:PUBLIC 'Desktop\$($NomeAtalho.Replace("'", "''")).lnk') -Force -ErrorAction SilentlyContinue"
    $uninstall = $uninstall.Replace("`$ErrorActionPreference = 'Stop'", "`$ErrorActionPreference = 'Stop'" + $rem)
}
Set-Content "$dir\tools\chocolateyUninstall.ps1" $uninstall -Encoding UTF8

# ---------- empacota ----------
Push-Location $dir
try { & choco pack --limit-output 2>&1 | ForEach-Object { "$PSItem" } } finally { Pop-Location }
$pkg = Join-Path $dir "$Id.$Versao.nupkg"
if (-not (Test-Path $pkg)) { throw 'choco pack nao gerou o .nupkg.' }

# o choco pack recusa <icon> no nuspec; a loja le o icone embutido, entao liga-se depois, dentro do .nupkg
if ($temIcone) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($pkg, 'Update')
    try {
        $e = $zip.Entries | Where-Object { $PSItem.FullName -like '*.nuspec' } | Select-Object -First 1
        $sr = New-Object IO.StreamReader($e.Open(), [Text.Encoding]::UTF8)
        $txt = $sr.ReadToEnd(); $sr.Dispose()
        $nome = $e.FullName; $e.Delete()
        $txt = $txt.Replace('</metadata>', '<icon>icon.png</icon></metadata>')
        $n = $zip.CreateEntry($nome)
        $sw = New-Object IO.StreamWriter($n.Open(), (New-Object Text.UTF8Encoding($false)))
        $sw.Write($txt); $sw.Dispose()
    } finally { $zip.Dispose() }
}

New-Item -ItemType Directory $Saida -Force | Out-Null
$final = Join-Path $Saida (Split-Path $pkg -Leaf)
Copy-Item $pkg $final -Force
Remove-Item $dir -Recurse -Force
$h = (Get-FileHash $final -Algorithm SHA256).Hash
"Pacote: $final"
"Tamanho: {0:N1} MB" -f ((Get-Item $final).Length / 1MB)
"SHA256:  $h"
