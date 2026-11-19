<#
    Loja de Aplicativos - implantacao do AGENTE (modelo; o Publicar-Agente.ps1 gera a copia com os valores preenchidos).

    Serve igual para GPO (script de inicializacao do computador), Tactical RMM, Intune ou execucao manual (como administrador).
    E seguro rodar de novo: se o agente ja esta instalado com este mesmo .exe e em execucao, nao faz nada.

    O que faz:
      1. confere se o agente ja esta instalado e em dia (compara o SHA-256 do .exe) e, se estiver, sai;
      2. copia o LojaAgent.exe da origem (pasta de rede ou endereco https) para uma pasta temporaria;
      3. SO continua se o SHA-256 do arquivo for igual a $HashExe (arquivo trocado na rede = nada e instalado);
      4. roda "LojaAgent.exe install --server ... --cert ..." (cria o servico, grava a configuracao, inicia).

    Servidores: o proprio instalador do agente recusa (sai sem erro). Em GPO, use tambem o filtro WMI "Somente estacoes Windows".
    Log: C:\ProgramData\LojaAgent-instalacao.log
    Arquivo sem acentos de proposito (o PowerShell 5.1 le script sem BOM como ANSI).
#>
param(
    [string]$Origem = '__ORIGEM__',          # pasta de rede (\\servidor\pasta) ou endereco https onde esta o LojaAgent.exe
    [string]$Servidor = '__SERVER__',        # endereco do servidor da loja
    [string]$CertSha256 = '__CERT__',        # SHA-256 do certificado do servidor (vazio so em teste com http)
    [string]$HashExe = '__HASH__',           # SHA-256 do LojaAgent.exe que sera instalado
    [string]$ChocoPath = '',
    [string]$InternalFeed = '__FEED__'      # pasta dos pacotes internos (.nupkg), a mesma do feed da loja; sem ela o agente nao acha os apps internos
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:ProgramData 'LojaAgent-instalacao.log'
function Escrever($t) {
    $l = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $t
    try { Add-Content -Path $log -Value $l } catch { }
    Write-Output $l
}
try { if ((Test-Path $log) -and (Get-Item $log).Length -gt 200KB) { Remove-Item $log -Force } } catch { }

try {
    Escrever "inicio (origem: $Origem)"
    if ($HashExe -notmatch '^[0-9A-Fa-f]{64}$') { throw 'HashExe nao preenchido: use a copia gerada pelo Publicar-Agente.ps1.' }
    if ($Servidor -notmatch '^https?://') { throw 'Servidor nao preenchido.' }
    $HashExe = $HashExe.ToUpperInvariant()

    # 1. ja esta instalado e em dia?
    $exeInstalado = Join-Path $env:ProgramFiles 'Loja de Aplicativos\Agent\LojaAgent.exe'
    $servico = Get-Service -Name 'LojaAgent' -ErrorAction SilentlyContinue
    if ($servico -and $servico.Status -eq 'Running' -and (Test-Path $exeInstalado) -and (Get-FileHash $exeInstalado -Algorithm SHA256).Hash -eq $HashExe) {
        # Em dia. So confere a pasta dos pacotes internos no agent.json: agente instalado sem ela nao acha os apps internos.
        $cfgPath = Join-Path $env:ProgramData 'LojaAgent\agent.json'
        $cfg = $null
        try { $cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json } catch { }
        if ($InternalFeed -and $cfg -and $cfg.InternalFeed -ne $InternalFeed) {
            $cfg | Add-Member -NotePropertyName InternalFeed -NotePropertyValue $InternalFeed -Force
            [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
            Restart-Service -Name 'LojaAgent' -Force
            Start-Sleep -Seconds 5
            $sv = Get-Service -Name 'LojaAgent' -ErrorAction SilentlyContinue
            if ($sv -and $sv.Status -ne 'Running') { Start-Service -Name 'LojaAgent' }
            Escrever 'pasta de pacotes internos configurada no agente e servico reiniciado'
        }
        else {
            Escrever 'agente ja instalado e em execucao: nada a fazer'
        }
        exit 0
    }

    # 2. copia para uma pasta temporaria
    $tmpDir = Join-Path $env:TEMP ('loja-agente-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
    $tmp = Join-Path $tmpDir 'LojaAgent.exe'
    try {
        if ($Origem -match '^https?://') {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri ($Origem.TrimEnd('/') + '/LojaAgent.exe') -OutFile $tmp -UseBasicParsing
        }
        else {
            Copy-Item -LiteralPath (Join-Path $Origem 'LojaAgent.exe') -Destination $tmp -Force
        }

        # 3. so instala o arquivo que a TI aprovou
        $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($hash -ne $HashExe) { throw "O LojaAgent.exe da origem tem SHA-256 diferente do esperado ($hash). Nada foi instalado." }

        # 4. instala
        $args = @('install', '--server', $Servidor)
        if ($CertSha256) { $args += @('--cert', $CertSha256) }
        if ($ChocoPath) { $args += @('--choco', $ChocoPath) }
        if ($InternalFeed) { $args += @('--internal-feed', $InternalFeed) }
        $saida = & $tmp @args 2>&1 | Out-String
        $codigo = $LASTEXITCODE
        Escrever ("LojaAgent install: codigo $codigo | " + ($saida -replace '\s+', ' ').Trim())
        if ($codigo -ne 0) { throw "O instalador do agente falhou (codigo $codigo)." }
        Start-Sleep -Seconds 3
        $sv = Get-Service -Name 'LojaAgent' -ErrorAction SilentlyContinue
        if ($sv -and $sv.Status -ne 'Running') { Start-Service -Name 'LojaAgent'; Escrever 'servico iniciado depois da instalacao' }
    }
    finally {
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Escrever 'fim ok'
}
catch {
    Escrever "ERRO: $($_.Exception.Message)"
    exit 1
}
