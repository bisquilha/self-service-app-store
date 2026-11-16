<#
.SYNOPSIS
    Cadastra dados de exemplo no servidor de demonstracao para conferir as telas do painel.

.DESCRIPTION
    Rodar com o servidor de demonstracao (Tactical simulado) no ar e a pasta de dados NOVA.
    Cria 7 aplicativos da internet e 2 internos, com grupos reais do AD (LOJA-Todos, LOJA-Admins) e "*";
    desativa um, liga a atualizacao automatica de outro, bloqueia a desinstalacao de outro;
    instala 2 aplicativos (simulado) neste PC, que ficam desatualizados em relacao a versao da loja.
    Com -DataDir, inclui tambem um grupo que nao existe mais no AD em um dos aplicativos (aparece em ambar no painel).
    Precisa de internet (busca no Chocolatey).
#>
param([string]$Base = 'http://localhost:5080', [string]$DataDir = '', [switch]$PublicarApp)
$ErrorActionPreference = 'Stop'
$pc = $env:COMPUTERNAME
$admin = @{ 'X-Requested-With' = 'loja-admin' }

function Admin($method, $path, $body = $null) {
    $p = @{ Uri = "$Base/api/admin$path"; Method = $method; UseDefaultCredentials = $true; Headers = $admin; UseBasicParsing = $true }
    if ($null -ne $body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 5)); $p.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @p
}

for ($i = 0; $i -lt 40; $i++) { try { Invoke-WebRequest "$Base/" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }

$catalogo = @(
    @{ id = '7zip'; src = 'community'; groups = @('LOJA-Todos') },
    @{ id = 'notepadplusplus'; src = 'community'; groups = @('LOJA-Todos') },
    @{ id = 'vlc'; src = 'community'; groups = @('LOJA-Todos') },
    @{ id = 'putty'; src = 'community'; groups = @('LOJA-Admins') },
    @{ id = 'firefox'; src = 'community'; groups = @('*') },
    @{ id = 'googlechrome'; src = 'community'; groups = @('LOJA-Todos') },
    @{ id = 'adobereader'; src = 'community'; groups = @('LOJA-Todos', 'LOJA-Admins') },
    @{ id = 'app-interno'; src = 'internal'; groups = @('LOJA-Admins') },
    @{ id = 'teste-falha'; src = 'internal'; groups = @('S-1-5-32-546') }
)
$ids = @{}
foreach ($a in $catalogo) {
    try {
        $app = Admin POST '/apps' @{ packageId = $a.id; source = $a.src; groups = $a.groups }
        $ids[$a.id] = $app.id
        Write-Output ("OK    {0,-16} id={1} versao={2}" -f $a.id, $app.id, $app.version)
    } catch { Write-Output ("FALHA {0}: {1}" -f $a.id, $_.Exception.Message) }
}

function SetFlags($pkg, $enabled, $auto, $uninstall) {
    if (-not $ids.ContainsKey($pkg)) { return }
    $app = (Admin GET '/apps') | Where-Object { $_.id -eq $ids[$pkg] }
    Admin PUT "/apps/$($ids[$pkg])" @{ groups = @($app.groups); enabled = $enabled; autoUpdate = $auto; allowUninstall = $uninstall } | Out-Null
}
SetFlags 'teste-falha' $false $false $true
SetFlags '7zip' $true $true $true
SetFlags 'googlechrome' $true $false $false

# instalacoes simuladas neste PC: o Tactical simulado instala sempre a versao 1.0, entao ficam "desatualizadas"
foreach ($pkg in '7zip', 'notepadplusplus') {
    if (-not $ids.ContainsKey($pkg)) { continue }
    $req = Invoke-RestMethod "$Base/api/install" -Method POST -UseDefaultCredentials -UseBasicParsing -ContentType 'application/json' -Body (@{ appId = $ids[$pkg]; hostname = $pc } | ConvertTo-Json -Compress)
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 800
        $r = Invoke-RestMethod "$Base/api/requests/$($req.id)" -UseDefaultCredentials -UseBasicParsing
        if ($r.status -ne 'running') { break }
    }
    Write-Output ("INFO  instalacao simulada de {0}: {1}" -f $pkg, $r.status)
}

if ($DataDir -and $ids.ContainsKey('vlc') -and (Get-Command python -ErrorAction SilentlyContinue)) {
    python -I -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1], timeout=10); c.execute('INSERT OR IGNORE INTO app_groups (app_id, group_name) VALUES (?,?)', (int(sys.argv[2]), sys.argv[3])); c.commit()" (Join-Path $DataDir 'loja.db') $ids['vlc'] 'LOJA-Antigo-Renomeado'
    Write-Output 'INFO  vlc ganhou o grupo LOJA-Antigo-Renomeado (nao existe no AD)'
}
# -PublicarApp: publica as versoes 1.2.0 e 1.2.1 do app da loja (Downloads) e registra a versao usada em duas maquinas
# (a 1.2.1 fica como atual; PC-DUPLICADO usa a 1.2.0, que o painel marca como antiga)
if ($PublicarApp) {
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler; $handler.UseDefaultCredentials = $true
    $http = New-Object System.Net.Http.HttpClient($handler); $http.Timeout = [TimeSpan]::FromMinutes(10)
    $http.DefaultRequestHeaders.Add('X-Requested-With', 'loja-admin')
    $http.GetAsync("$Base/api/admin/info").Result | Out-Null
    foreach ($v in '1.2.0', '1.2.1') {
        $exe = Join-Path $env:USERPROFILE "Downloads\LojaDeAplicativos-$v.exe"
        if (-not (Test-Path $exe)) { Write-Output "PULOU app $v (falta $exe)"; continue }
        $form = New-Object System.Net.Http.MultipartFormDataContent
        $fs = [IO.File]::OpenRead($exe)
        try {
            $form.Add((New-Object System.Net.Http.StreamContent($fs)), 'file', 'LojaDeAplicativos.exe')
            $resp = $http.PostAsync("$Base/api/admin/client/releases?mandatory=false", $form).Result
            Write-Output ("INFO  app da loja {0}: HTTP {1}" -f $v, [int]$resp.StatusCode)
        } finally { $fs.Dispose() }
    }
    foreach ($par in @(@($pc, '1.2.1'), @('PC-DUPLICADO', '1.2.0'))) {
        try { Invoke-WebRequest "$Base/api/me?hostname=$($par[0])" -UseDefaultCredentials -UseBasicParsing -Headers @{ 'X-Loja-App-Version' = $par[1] } | Out-Null } catch { }
        Write-Output ("INFO  {0} usa a loja {1}" -f $par[0], $par[1])
    }
}
Write-Output "----- pronto: $($ids.Count) aplicativos"
