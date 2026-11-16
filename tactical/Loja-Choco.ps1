<#
    Loja de Aplicativos - script executado pelo Tactical RMM (como SYSTEM).

    Cadastro no Tactical: Settings > Script Manager > New Script
      - Name: Loja-Choco    - Shell: PowerShell    - Timeout padrao: 3600
    Antes de salvar, preencha as variaveis da secao CONFIGURACAO abaixo.
    O ID do script (coluna "Id" no Script Manager) vai em Loja:Tactical:ScriptId no servidor da loja.

    Acoes: install, upgrade, uninstall e list (so consulta). Todo retorno leva a lista de
    pacotes do Chocolatey da maquina, para a loja mostrar "Instalado" / "Atualizar", e (a partir da
    loja 1.6.0) a lista de programas do Windows (chaves Uninstall do registro, SOMENTE LEITURA), para a
    loja mostrar "Ja instalado no computador" em programas que nao vieram por ela.
    Ordem de atualizacao: primeiro o servidor 1.6.0, depois este script, depois o app 1.3.0. O servidor
    antigo ignora o campo novo; o script antigo simplesmente nao envia a lista de programas.

    O servidor da loja envia os parametros como argumentos e tambem como variaveis de
    ambiente (LOJA_ACTION, LOJA_PACKAGE, LOJA_SOURCE, LOJA_TOKEN). As variaveis de ambiente
    tem prioridade. Tudo e validado aqui de novo: nada fora do padrao e executado.

    Arquivo mantido sem acentos de proposito (o PowerShell 5.1 le scripts sem BOM como ANSI).
#>
param(
    [string]$Action,
    [string]$Package,
    [string]$Source,
    [string]$Token
)

# ===================== CONFIGURACAO =====================
$LojaUrl        = 'https://loja.SEUDOMINIO.local'          # endereco do servidor da loja (sem barra no final)
$LojaCertSha256 = 'COLE_AQUI_O_SHA256_DO_CERTIFICADO'      # impresso pelo Install-LojaServer.ps1
$InternalFeed   = '\\NAS\loja-pacotes'                     # pasta com os pacotes internos (.nupkg)
$CommunityFeed  = 'https://community.chocolatey.org/api/v2/'
# ========================================================

if ($env:LOJA_ACTION)  { $Action  = $env:LOJA_ACTION }
if ($env:LOJA_PACKAGE) { $Package = $env:LOJA_PACKAGE }
if ($env:LOJA_SOURCE)  { $Source  = $env:LOJA_SOURCE }
if ($env:LOJA_TOKEN)   { $Token   = $env:LOJA_TOKEN }

$chocoRoot = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } else { Join-Path $env:ProgramData 'chocolatey' }
$choco = Join-Path $chocoRoot 'bin\choco.exe'

# Pacotes instalados pelo Chocolatey, uma linha "pacote|versao" por pacote (vai junto em todo retorno).
function Get-ChocoList {
    if (-not (Test-Path $choco)) { return '' }
    $major = 0
    $ver = & $choco --version 2>$null | Select-Object -First 1
    [void][int]::TryParse((("$ver") -split '\.')[0], [ref]$major)
    $listArgs = if ($major -ge 2) { @('list', '-r') } else { @('list', '--local-only', '-r') }
    $lines = & $choco @listArgs 2>$null | Where-Object { $_ -match '^[A-Za-z0-9][A-Za-z0-9._-]*\|[^|\s]+$' }
    return (@($lines) -join "`n")
}

# Programas do Windows (chaves Uninstall do registro, 64 e 32 bits), uma linha "nome<TAB>versao" por programa.
# SOMENTE LEITURA: nada e alterado, instalado ou removido por aqui. Serve so para a loja mostrar "Ja instalado no computador".
# Alcance: programas instalados para a maquina toda (HKLM). Nao aparecem programas instalados so para um usuario (HKCU,
# como muitos instaladores da Microsoft Store e do Chrome por usuario) nem programas portateis (sem instalador).
function Get-WindowsPrograms {
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    $seen = @{}
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($key in @(Get-ChildItem -Path $root -ErrorAction SilentlyContinue)) {
            try {
                $p = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
                $name = "$($p.DisplayName)"
                $name = ($name -replace '[\p{C}]', ' ' -replace '\s+', ' ').Trim()
                if (-not $name -or $name.Length -gt 200) { continue }
                if ($p.SystemComponent -eq 1) { continue }   # componentes escondidos do sistema
                if ($p.ParentKeyName) { continue }           # atualizacao ou patch de outro programa
                if ($p.ReleaseType -in @('Update', 'Hotfix', 'Security Update', 'Service Pack')) { continue }
                if ($name -match '^(KB\d+|Update for |Security Update for |Hotfix for )') { continue }
                $version = "$($p.DisplayVersion)"
                $version = ($version -replace '[\p{C}]', ' ' -replace '\s+', ' ').Trim()
                if ($version.Length -gt 50) { $version = $version.Substring(0, 50) }
                $id = ($name + "`t" + $version).ToLowerInvariant()
                if ($seen.ContainsKey($id)) { continue }
                $seen[$id] = $true
                $lines.Add($name + "`t" + $version)
            }
            catch { continue }
        }
    }
    $sorted = @($lines | Sort-Object | Select-Object -First 1500)
    return ($sorted -join "`n")
}

function Send-LojaResult {
    param([int]$Code, [string]$Text)

    if ($null -eq $Text) { $Text = '' }
    if ($Text.Length -gt 6000) { $Text = $Text.Substring($Text.Length - 6000) }
    Write-Output $Text
    Write-Output "LOJA_EXIT=$Code"

    if ($Token -notmatch '^[0-9A-Fa-f]{64}$') {
        Write-Output 'Token ausente ou invalido: resultado nao enviado a loja.'
        return
    }

    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        if (-not ('LojaCertPin' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class LojaCertPin
{
    private static string expected;

    public static void Enable(string sha256)
    {
        expected = sha256.Replace(":", "").Replace(" ", "").Trim();
        ServicePointManager.ServerCertificateValidationCallback = Check;
    }

    private static bool Check(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        using (SHA256 sha = SHA256.Create())
        {
            string hash = BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
            return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
'@
        }
        [LojaCertPin]::Enable($LojaCertSha256)
    }
    catch {
        Write-Output "Falha ao preparar a conexao com a loja: $($_.Exception.Message)"
        return
    }

    $installed = Get-ChocoList
    $payload = @{ token = $Token; exitCode = $Code; output = $Text; installed = $installed }
    # Se a coleta falhar, o campo NAO vai (em vez de ir vazio): a loja so mostra "fora da loja" quando a lista foi coletada de verdade.
    try {
        $windows = Get-WindowsPrograms
        if ($null -ne $windows) { $payload['installedWindows'] = [string]$windows }
    }
    catch {
        Write-Output "Nao foi possivel listar os programas do Windows: $($_.Exception.Message)"
    }
    $body = $payload | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($body)
    for ($i = 1; $i -le 3; $i++) {
        try {
            Invoke-RestMethod -Uri "$LojaUrl/api/callback" -Method Post -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 30 | Out-Null
            Write-Output 'Resultado enviado a loja.'
            return
        }
        catch {
            Write-Output "Falha ao avisar a loja (tentativa $i): $($_.Exception.Message)"
            Start-Sleep -Seconds (10 * $i)
        }
    }
}

# --- Validacao: so o que a loja conhece passa daqui ---
if ($Action -notin @('install', 'upgrade', 'uninstall', 'list')) {
    Send-LojaResult 90 "Acao invalida: '$Action'"
    exit 90
}
if ($Action -ne 'list') {
    if ($Package -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') {
        Send-LojaResult 91 "Nome de pacote invalido: '$Package'"
        exit 91
    }
    if ($Source -notin @('community', 'internal')) {
        Send-LojaResult 92 "Origem invalida: '$Source'"
        exit 92
    }
}

if (-not (Test-Path $choco)) {
    Send-LojaResult 93 "Chocolatey nao encontrado em $choco"
    exit 93
}

# So consultar: a lista de pacotes vai no retorno.
if ($Action -eq 'list') {
    Send-LojaResult 0 'Lista de pacotes consultada.'
    exit 0
}

if ($Action -eq 'uninstall') {
    # -x remove tambem as dependencias instaladas junto (ex.: 7zip -> 7zip.install).
    $chocoArgs = @('uninstall', $Package, '-y', '--no-progress', '-x')
}
else {
    # Pacotes internos podem depender de pacotes da comunidade, por isso as duas fontes.
    $feed = if ($Source -eq 'internal') { "$InternalFeed;$CommunityFeed" } else { $CommunityFeed }
    $chocoArgs = @($Action, $Package, '-y', '--no-progress', "--source=$feed")
    if ($Action -eq 'upgrade') { $chocoArgs += '--fail-on-not-installed' }
}

Write-Output "Loja: choco $Action $Package (origem: $Source)"
$output = & $choco @chocoArgs 2>&1 | Out-String
$code = $LASTEXITCODE

Send-LojaResult $code $output
exit $code
