<#
    Loja de Aplicativos - distribuicao por GPO (script de INICIALIZACAO do computador; roda como SYSTEM).

    O que faz, a cada inicializacao (so mexe no que mudou):
      1. copia LojaDeAplicativos.exe da pasta de distribuicao para "C:\Program Files\Loja de Aplicativos";
      2. escreve o loja.json (endereco do servidor + SHA-256 do certificado) ao lado do .exe;
      3. cria o atalho "Loja de Aplicativos" na Area de Trabalho de TODOS os usuarios e no Menu Iniciar,
         com o icone da loja (o do proprio .exe).
    O app se atualiza sozinho depois (autoupdate pelo painel). Este .exe e so o "atalho" que abre a versao certa.

    SEGURANCA: este script e o loja.json ficam NA GPO (SYSVOL, so a TI altera). O .exe vem de uma pasta de rede, entao so e
    copiado se o SHA-256 dele for IGUAL a $HashExe abaixo. Se alguem trocar o arquivo na pasta de rede, nada e instalado
    (fica registrado no log). Para distribuir um .exe novo, atualize $HashExe na copia do script que esta na GPO.

    Antes de usar, preencha a secao CONFIGURACAO. Arquivo sem acentos de proposito (o PowerShell 5.1 le script sem BOM como ANSI).
    Log: C:\ProgramData\LojaDeAplicativos-gpo.log
#>
param(
    [string]$Origem = '\\fileserver\share\loja-deploy\gpo',
    [string]$Destino = (Join-Path $env:ProgramFiles 'Loja de Aplicativos'),
    [string]$AtalhoDesktop = (Join-Path $env:PUBLIC 'Desktop'),
    [string]$AtalhoMenu = (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
    [string]$Log = (Join-Path $env:ProgramData 'LojaDeAplicativos-gpo.log')
)

# ===================== CONFIGURACAO =====================
$HashExe     = 'COLE_AQUI_O_SHA256_DO_EXE'                 # SHA-256 do LojaDeAplicativos.exe que esta na pasta de distribuicao
$ServidorUrl = 'https://loja.SEUDOMINIO.local'             # endereco do servidor da loja (sem barra no final)
$CertSha256  = 'COLE_AQUI_O_SHA256_DO_CERTIFICADO'         # impresso pelo Install-LojaServer.ps1
# ========================================================

$ErrorActionPreference = 'Stop'

function Escrever($texto) {
    $linha = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $texto
    try { Add-Content -Path $Log -Value $linha } catch { }
    Write-Output $linha
}

try { if ((Test-Path -LiteralPath $Log) -and (Get-Item -LiteralPath $Log).Length -gt 200KB) { Remove-Item -LiteralPath $Log -Force } } catch { }

function Sha256($caminho) { (Get-FileHash -LiteralPath $caminho -Algorithm SHA256).Hash.ToUpperInvariant() }

try {
    Escrever "inicio (origem: $Origem)"
    # guarda: so estacoes (ProductType 1). Windows Server e controlador de dominio nao recebem a loja.
    $tipoSo = (Get-CimInstance -ClassName ('Win32' + [char]95 + 'OperatingSystem')).ProductType
    if ($tipoSo -ne 1) { Escrever "pulado: Windows Server ou controlador de dominio (ProductType=$tipoSo)"; exit 0 }
    if ($HashExe -notmatch '^[0-9A-Fa-f]{64}$') { throw 'HashExe nao preenchido: configure o script antes de usar.' }
    $HashExe = $HashExe.ToUpperInvariant()

    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    $exeDestino = Join-Path $Destino 'LojaDeAplicativos.exe'

    # --- 1. o .exe (so se o hash for o esperado)
    $exeOrigem = Join-Path $Origem 'LojaDeAplicativos.exe'
    if ((Test-Path -LiteralPath $exeDestino) -and (Sha256 $exeDestino) -eq $HashExe) {
        Escrever 'LojaDeAplicativos.exe: ja esta igual'
    }
    else {
        if (-not (Test-Path -LiteralPath $exeOrigem)) { throw "Nao achei $exeOrigem" }
        # copia para um arquivo temporario LOCAL e so entao confere o hash (calcular o hash direto na rede e lento em unidade remota)
        $exeTemp = Join-Path $Destino 'LojaDeAplicativos.exe.novo'
        Copy-Item -LiteralPath $exeOrigem -Destination $exeTemp -Force
        $hashTemp = Sha256 $exeTemp
        if ($hashTemp -ne $HashExe) { Remove-Item -LiteralPath $exeTemp -Force -ErrorAction SilentlyContinue; throw "O .exe da pasta de rede tem SHA-256 diferente do esperado ($hashTemp). Nada foi instalado." }
        Move-Item -LiteralPath $exeTemp -Destination $exeDestino -Force
        Escrever 'LojaDeAplicativos.exe: copiado'
    }

    # --- 2. loja.json (vem deste script, nao da rede)
    $json = (@{ ServerUrl = $ServidorUrl; CertSha256 = $CertSha256 } | ConvertTo-Json)
    $jsonDestino = Join-Path $Destino 'loja.json'
    $atual = if (Test-Path -LiteralPath $jsonDestino) { Get-Content -LiteralPath $jsonDestino -Raw } else { '' }
    if ($atual.Trim() -eq $json.Trim()) { Escrever 'loja.json: ja esta igual' }
    else {
        [IO.File]::WriteAllText($jsonDestino, $json, (New-Object Text.UTF8Encoding($false)))
        Escrever 'loja.json: gravado'
    }

    # --- 3. atalhos (so cria se nao existir; o icone e o do proprio .exe)
    $ws = New-Object -ComObject WScript.Shell
    foreach ($pasta in @($AtalhoDesktop, $AtalhoMenu)) {
        $lnk = Join-Path $pasta 'Loja de Aplicativos.lnk'
        if (Test-Path -LiteralPath $lnk) { continue }
        New-Item -ItemType Directory -Path $pasta -Force | Out-Null
        $s = $ws.CreateShortcut($lnk)
        $s.TargetPath = $exeDestino
        $s.WorkingDirectory = $Destino
        $s.IconLocation = "$exeDestino,0"
        $s.Description = 'Loja de Aplicativos: instale os programas liberados para voce'
        $s.Save()
        Escrever "atalho criado: $lnk"
    }

    Escrever 'fim ok'
}
catch {
    Escrever "ERRO: $($_.Exception.Message)"
    exit 1
}
