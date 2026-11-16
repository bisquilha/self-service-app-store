<#
    Loja de Aplicativos - lembrete de atualizacao (script para rodar pelo Tactical RMM, como SYSTEM).

    O que faz (pode rodar de novo quantas vezes quiser: so mexe no que mudou):
      1. so age em computador que ja tem a loja (C:\Program Files\Loja de Aplicativos\loja.json); nos outros diz "nada a fazer";
      2. copia o app novo (1.4.0) para uma subpasta propria ("Lembrete"), SEM tocar no .exe do atalho que a GPO mantem;
         o arquivo so e usado se o SHA-256 dele for igual a $Hash abaixo;
      3. cria a tarefa agendada "Loja de Aplicativos - Lembrete": roda no logon de qualquer usuario e se repete a cada 4 horas,
         com as permissoes do proprio usuario (grupo Usuarios). Ela chama o .exe com --lembrete, que so mostra o aviso
         no canto da tela se houver aplicativo com atualizacao.

    Parametros:  -Remover  desfaz tudo (apaga a tarefa e a subpasta).   -Simular  so mostra o que faria.
    Para trocar de versao: gere o .exe novo, copie para a pasta de origem e atualize $Origem e $Hash aqui no Tactical.
    Arquivo sem acentos de proposito. Log: C:\ProgramData\LojaDeAplicativos-lembrete.log
#>
param(
    [switch]$Remover,
    [switch]$Simular
)

# ===================== CONFIGURACAO =====================
$Origem = '\\fileserver\share\loja-deploy\app\LojaDeAplicativos-1.4.0.exe'
$Hash   = 'COLE-AQUI-O-SHA256-DO-INSTALADOR'
$Horas  = 4
# ========================================================

$ErrorActionPreference = 'Stop'
$Loja   = Join-Path $env:ProgramFiles 'Loja de Aplicativos'
$Pasta  = Join-Path $Loja 'Lembrete'
$Exe    = Join-Path $Pasta 'LojaDeAplicativos.exe'
$Cfg    = Join-Path $Loja 'loja.json'
$Tarefa = 'Loja de Aplicativos - Lembrete'
$Log    = Join-Path $env:ProgramData 'LojaDeAplicativos-lembrete.log'

function Escrever($texto) {
    $linha = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $texto
    try { Add-Content -Path $Log -Value $linha } catch { }
    Write-Output $linha
}
function Sha256($caminho) { (Get-FileHash -LiteralPath $caminho -Algorithm SHA256).Hash.ToUpperInvariant() }

try {
    try { if ((Test-Path -LiteralPath $Log) -and (Get-Item -LiteralPath $Log).Length -gt 200KB) { Remove-Item -LiteralPath $Log -Force } } catch { }
    $modo = if ($Remover) { 'remover' } elseif ($Simular) { 'simular' } else { 'instalar' }
    Escrever "inicio ($modo) em $env:COMPUTERNAME"

    # ---------- remover ----------
    if ($Remover) {
        if (Get-ScheduledTask -TaskName $Tarefa -ErrorAction SilentlyContinue) {
            if (-not $Simular) { Unregister-ScheduledTask -TaskName $Tarefa -Confirm:$false }
            Escrever 'tarefa agendada: removida'
        } else { Escrever 'tarefa agendada: nao existia' }
        if (Test-Path -LiteralPath $Pasta) {
            if (-not $Simular) { Remove-Item -LiteralPath $Pasta -Recurse -Force }
            Escrever 'subpasta Lembrete: removida'
        } else { Escrever 'subpasta Lembrete: nao existia' }
        Escrever 'fim ok'
        return
    }

    # ---------- 1. so onde a loja esta instalada ----------
    if (-not (Test-Path -LiteralPath $Cfg)) {
        Escrever 'a loja nao esta instalada neste computador (sem loja.json): nada a fazer'
        return
    }
    if ($Hash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Hash nao preenchido: configure o script antes de usar.' }
    $Hash = $Hash.ToUpperInvariant()

    # ---------- 2. o .exe do lembrete ----------
    if ((Test-Path -LiteralPath $Exe) -and (Sha256 $Exe) -eq $Hash) {
        Escrever 'app do lembrete: ja esta igual'
    } else {
        if (-not (Test-Path -LiteralPath $Origem)) { throw "Nao achei $Origem" }
        $hashOrigem = Sha256 $Origem
        if ($hashOrigem -ne $Hash) { throw "O arquivo da pasta de rede tem SHA-256 diferente do esperado ($hashOrigem). Nada foi copiado." }
        if ($Simular) { Escrever 'app do lembrete: seria copiado' }
        else {
            New-Item -ItemType Directory -Path $Pasta -Force | Out-Null
            $parcial = $Exe + '.part'
            Copy-Item -LiteralPath $Origem -Destination $parcial -Force
            if ((Sha256 $parcial) -ne $Hash) { Remove-Item -LiteralPath $parcial -Force; throw 'A copia nao confere com o SHA-256 esperado.' }
            Move-Item -LiteralPath $parcial -Destination $Exe -Force
            Escrever 'app do lembrete: copiado'
        }
    }

    # ---------- 3. tarefa agendada ----------
    $acao = New-ScheduledTaskAction -Execute $Exe -Argument ('--lembrete --config "{0}"' -f $Cfg) -WorkingDirectory $Pasta
    $gatilho = New-ScheduledTaskTrigger -AtLogOn
    $repete = (New-ScheduledTaskTrigger -Once -At '00:00' -RepetitionInterval (New-TimeSpan -Hours $Horas) -RepetitionDuration (New-TimeSpan -Days 3650)).Repetition
    $gatilho.Repetition = $repete
    $principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
    $config = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
    if ($Simular) {
        Escrever "tarefa agendada: seria criada (logon + a cada $Horas h, grupo Usuarios)"
    } else {
        Register-ScheduledTask -TaskName $Tarefa -Action $acao -Trigger $gatilho -Principal $principal -Settings $config -Description 'Avisa o usuario quando ha aplicativo da Loja com atualizacao.' -Force | Out-Null
        $t = Get-ScheduledTask -TaskName $Tarefa
        Escrever ('tarefa agendada: criada (estado: {0}, repete a cada {1})' -f $t.State, $t.Triggers[0].Repetition.Interval)
    }
    Escrever 'fim ok'
}
catch {
    Escrever "ERRO: $($_.Exception.Message)"
    exit 1
}
