#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala ou atualiza o servidor da Loja de Aplicativos no IIS (rodar na VM da loja).

.DESCRIPTION
    - Habilita IIS + autenticação do Windows
    - Cria (só na primeira vez) o certificado autoassinado de 10 anos
    - Copia os arquivos preservando appsettings.Production.json e o banco
    - Cria pool/site HTTPS e libera as pastas para a identidade do pool
    - Imprime o SHA-256 do certificado (vai no loja.json do app e no script do Tactical)
    Pode ser executado de novo para atualizar a versão: a configuração e os dados são mantidos.

.EXAMPLE
    .\Install-LojaServer.ps1 -DnsName loja.corp.example.com -PublishPath C:\temp\publicar\servidor
#>
param(
    [Parameter(Mandatory)] [string]$DnsName,
    [Parameter(Mandatory)] [string]$PublishPath,
    [string]$SiteName = 'Loja',
    [string]$InstallPath = 'C:\inetpub\loja',
    [string]$DataPath = 'C:\LojaDados',
    [string]$Domain = 'CORP'
)
$ErrorActionPreference = 'Stop'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $PublishPath 'Loja.Server.dll'))) {
    throw "Não encontrei Loja.Server.dll em $PublishPath (use a pasta publicar\servidor)."
}

Step '1/7 Recursos do IIS'
Install-WindowsFeature Web-Server, Web-Windows-Auth, Web-Mgmt-Console, Web-AppInit | Format-Table -AutoSize

Step '2/7 ASP.NET Core Hosting Bundle'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
if (-not (Test-Path $ancm)) {
    throw 'Instale o "ASP.NET Core 8.0 Runtime - Windows Hosting Bundle" (https://dotnet.microsoft.com/download/dotnet/8.0) e rode este script de novo. Se ele já foi instalado ANTES do IIS, rode o instalador de novo e escolha "Reparar".'
}
Write-Host 'Hosting Bundle encontrado.'
Import-Module WebAdministration

Step '3/7 Certificado autoassinado'
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.FriendlyName -eq 'Loja de Aplicativos' -and $_.Subject -eq "CN=$DnsName" -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
$newCert = $false
if (-not $cert) {
    $cert = New-SelfSignedCertificate -DnsName $DnsName -CertStoreLocation Cert:\LocalMachine\My `
        -NotAfter (Get-Date).AddYears(10) -FriendlyName 'Loja de Aplicativos' `
        -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy Exportable
    $newCert = $true
    Write-Host "Certificado criado (válido até $($cert.NotAfter.ToString('dd/MM/yyyy')))."
} else {
    Write-Host "Usando o certificado existente (válido até $($cert.NotAfter.ToString('dd/MM/yyyy')))."
}
$sha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)).Replace('-', '')

Step '4/7 Arquivos'
New-Item -ItemType Directory -Force $InstallPath, $DataPath | Out-Null
$poolExists = Test-Path "IIS:\AppPools\$SiteName"
if ($poolExists -and (Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') {
    Stop-WebAppPool -Name $SiteName
}
if ($poolExists) {
    # Espera o IIS soltar os arquivos: pool parado e nenhum w3wp desse pool vivo.
    for ($i = 0; $i -lt 30; $i++) {
        $worker = Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" | Where-Object { $_.CommandLine -like "*-ap `"$SiteName`"*" }
        if ((Get-WebAppPoolState -Name $SiteName).Value -eq 'Stopped' -and -not $worker) { break }
        Start-Sleep -Seconds 1
    }
}
robocopy $PublishPath $InstallPath /MIR /R:5 /W:3 /XF appsettings.Production.json /XD App_Data logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou (código $LASTEXITCODE)." }

$sourceVersion = (Get-Item (Join-Path $PublishPath 'Loja.Server.dll')).VersionInfo.ProductVersion
$installedVersion = (Get-Item (Join-Path $InstallPath 'Loja.Server.dll')).VersionInfo.ProductVersion
if ($sourceVersion -ne $installedVersion) {
    throw "A cópia não substituiu o servidor (pacote $sourceVersion, instalado $installedVersion). Rode o script de novo."
}
Write-Host "Versão do pacote copiada: $installedVersion"

$prodConfig = Join-Path $InstallPath 'appsettings.Production.json'
$newConfig = -not (Test-Path $prodConfig)
if ($newConfig) {
    $template = [ordered]@{
        Loja = [ordered]@{
            DataDir          = $DataPath
            DefaultDomain    = $Domain
            AdminGroups      = @('LOJA-Admins')
            InternalFeedPath = '\\NAS\loja-pacotes'
            Tactical         = [ordered]@{
                Url      = 'https://api.SEU-TACTICAL'
                ApiKey   = 'COLE_A_CHAVE_DA_API_AQUI'
                ScriptId = 0
            }
        }
    }
    $template | ConvertTo-Json -Depth 5 | Set-Content $prodConfig -Encoding UTF8
}

Step '5/7 Pool e site HTTPS'
if (-not $poolExists) { New-WebAppPool -Name $SiteName | Out-Null }
$poolPath = "IIS:\AppPools\$SiteName"
Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ''
Set-ItemProperty $poolPath -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $poolPath -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)

if (-not (Get-Website -Name $SiteName)) {
    New-Website -Name $SiteName -PhysicalPath $InstallPath -ApplicationPool $SiteName -Port 443 -Ssl -HostHeader $DnsName -SslFlags 1 | Out-Null
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true
$binding = Get-WebBinding -Name $SiteName -Protocol https
try { $binding.RemoveSslCertificate() } catch { }
$binding.AddSslCertificate($cert.Thumbprint, 'My')

Step '6/7 Autenticação'
# Windows (Kerberos/NTLM) para os usuários; anônimo fica ligado só para o /api/callback do script,
# o próprio servidor exige login em todo o resto.
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $true

Step '7/7 Permissões'
$poolIdentity = "IIS AppPool\$SiteName"
icacls $DataPath /grant "${poolIdentity}:(OI)(CI)M" /T /Q | Out-Null
# Configuração com a chave do Tactical: só Administradores (S-1-5-32-544), SYSTEM (S-1-5-18) e o pool.
icacls $prodConfig /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' "${poolIdentity}:R" /Q | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $DataPath 'loja-certificado.cer') -Force | Out-Null

Start-WebAppPool -Name $SiteName
Start-Website -Name $SiteName -ErrorAction SilentlyContinue

Write-Host "`n================ RESUMO ================" -ForegroundColor Green
Write-Host "Versão:          $installedVersion" -ForegroundColor Green
Write-Host "Site:            https://$DnsName/  (pasta $InstallPath)"
Write-Host "Dados:           $DataPath  (banco loja.db + ícones; incluir no backup)"
Write-Host "SHA-256 do cert: $sha256" -ForegroundColor Yellow
Write-Host '   -> vai no loja.json do app (CertSha256) e no Loja-Choco.ps1 ($LojaCertSha256)'
if ($newCert) {
    Write-Host "`nCertificado NOVO. Guarde uma cópia com a chave privada (se ele se perder, é preciso redistribuir o app):" -ForegroundColor Yellow
    Write-Host "   Export-PfxCertificate -Cert Cert:\LocalMachine\My\$($cert.Thumbprint) -FilePath D:\backup\loja.pfx -Password (Read-Host -AsSecureString 'Senha')"
}
Write-Host "`nSe ainda não fez (precisa de admin do domínio):"
Write-Host "   - DNS: registro A '$DnsName' -> IP desta VM"
Write-Host "   - SPN (Kerberos): setspn -S HTTP/$DnsName $env:COMPUTERNAME"
if ($newConfig) {
    Write-Host "`nPREENCHA $prodConfig (Url, ApiKey e ScriptId do Tactical, caminho do NAS)" -ForegroundColor Yellow
    Write-Host "e depois rode: Restart-WebAppPool -Name $SiteName"
    Write-Host 'Enquanto não estiver preenchido, o site responde erro 500.30.'
}
