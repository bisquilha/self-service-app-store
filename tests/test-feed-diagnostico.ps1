<#
.SYNOPSIS
    Testa o diagnostico do card "Feed interno (NAS)" (servidor 1.6.0+): o que foi OBSERVADO separado do que e HIPOTESE,
    e que nada e alterado.

.DESCRIPTION
    Rodar com o servidor apontando para um caminho que NAO abre. O parametro -Esperado diz qual situacao foi montada:

      acesso    Loja__InternalFeedPath = C:\Windows\System32\config            (existe e o usuario comum nao pode listar)
      naoachou  Loja__InternalFeedPath = C:\loja-pasta-que-nao-existe-xyz
      semnome   Loja__InternalFeedPath = \\nome-que-nao-resolve-xyz.invalid\loja
      ok        Loja__InternalFeedPath = pasta que abre (o card fica verde)

    O teste nao muda permissao nenhuma e nao grava nada. Para "acesso", rode sem elevar (um administrador elevado consegue listar a pasta).

.EXAMPLE
    .\test-feed-diagnostico.ps1 -Esperado naoachou
#>
param([string]$Base = 'http://localhost:5080', [ValidateSet('acesso', 'naoachou', 'semnome', 'ok')][string]$Esperado = 'naoachou')
$ErrorActionPreference = 'Stop'
$script:fails = 0

function Check($name, $cond, $detail = '') {
    if ($cond) { Write-Output "OK    $name $detail" } else { Write-Output "FALHA $name $detail"; $script:fails++ }
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$status = Invoke-RestMethod "$Base/api/admin/status" -UseDefaultCredentials
$feed = @($status.checks) | Where-Object { $_.name -like 'Feed interno*' } | Select-Object -First 1
$text = "$($feed.detail)"
Write-Output "      detalhe: $text"

if ($Esperado -eq 'ok') {
    Check 'pasta que abre: card ok e sem diagnostico de falha' ($feed.ok -eq $true -and $text -notlike '*Hip*tese*')
    Write-Output "----- $script:fails falha(s)"
    exit $script:fails
}

$me = (whoami)
Check 'o card fica com problema (ok = false)' ($feed.ok -eq $false)
Check 'mostra a conta com que o servidor roda (a evidencia mais importante)' ($text -like "*Conta do servidor: $me*") "whoami=$me"
Check 'diz o resultado de abrir a pasta' ($text -like '*Abrir a pasta:*')
Check 'diz que nada foi alterado' ($text -like '*Nada foi alterado*')
switch ($Esperado) {
    'acesso' {
        Check 'acesso negado aparece como OBSERVACAO (UnauthorizedAccessException)' ($text -like '*acesso negado*' -and $text -like '*UnauthorizedAccessException*')
        Check 'permissao so aparece como HIPOTESE a confirmar, nunca como fato' ($text -like '*Hip*tese a confirmar*permiss*')
        Check 'sem rede envolvida (caminho local): nenhuma frase de DNS ou porta 445' ($text -notlike '*porta 445*' -and $text -notlike '*n*o resolve*')
    }
    'naoachou' {
        Check 'caminho nao encontrado como observacao (DirectoryNotFoundException)' ($text -like '*n*o foi encontrado*' -and $text -like '*DirectoryNotFoundException*')
        Check 'a hipotese fala do nome do compartilhamento/pasta, nao de permissao' ($text -like '*Hip*tese a confirmar*nome do compartilhamento*' -and $text -notlike '*n*o tem permiss*')
    }
    'semnome' {
        Check 'o nome do NAS que nao resolve e uma OBSERVACAO de rede' ($text -like '*Rede:*' -and $text -like '*n*o resolve*')
        Check 'a hipotese fala de nome/rota/NAS desligado, nao de permissao' ($text -like '*Hip*tese a confirmar*' -and $text -notlike '*n*o tem permiss*')
    }
}
Write-Output "----- $script:fails falha(s)"
exit $script:fails
