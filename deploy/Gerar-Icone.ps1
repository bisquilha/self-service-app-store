# Gera o icone da Loja de Aplicativos (GDI+, sem nada externo): PNGs de varios tamanhos + loja.ico (BMP ate 128, PNG em 256) + folha de previa. So ASCII.
param([string]$Saida)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory $Saida -Force | Out-Null

function Cor($hex, $alpha = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B) }

function Caminho-Arredondado([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Desenha o icone numa tela de 256 x 256 (coordenadas de projeto); depois e reduzido.
# $simples = versao para tamanhos pequenos (sem alca, sacola maior, 4 quadradinhos grandes).
function Desenhar([bool]$simples) {
    $bmp = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform(4, 4)   # 1024 px de trabalho; coordenadas de projeto 0..256

    # fundo: quadrado arredondado com degrade azul (as cores do app)
    $fundo = Caminho-Arredondado 6 6 244 244 54
    $deg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(256, 256)), (Cor '#3D8BFF'), (Cor '#173F86'))
    $g.FillPath($deg, $fundo)
    # brilho suave no alto
    $brilho = New-Object System.Drawing.Drawing2D.GraphicsPath; $brilho.AddEllipse(-40, -150, 336, 260)
    $clip = $g.Clip; $g.SetClip($fundo)
    $g.FillPath((New-Object System.Drawing.SolidBrush((Cor '#FFFFFF' 34))), $brilho)
    $g.Clip = $clip
    # contorno fino
    $g.DrawPath((New-Object System.Drawing.Pen((Cor '#0B2A63' 120), 2.5)), $fundo)

    if ($simples) { $bx = 50; $by = 74; $bw = 156; $bh = 140; $br = 20; $tile = 40; $gap = 14 }
    else { $bx = 62; $by = 100; $bw = 132; $bh = 112; $br = 18; $tile = 32; $gap = 12 }

    # sombra da sacola (camadas translucidas, simulando desfoque)
    foreach ($k in 1..6) {
        $sombra = Caminho-Arredondado ($bx - 2 + $k * 0.4) ($by + 6 + $k * 1.6) ($bw + 4 - $k * 0.8) $bh $br
        $g.FillPath((New-Object System.Drawing.SolidBrush((Cor '#06173D' 20))), $sombra)
    }

    # alca
    if (-not $simples) {
        $pen = New-Object System.Drawing.Pen((Cor '#FFFFFF'), 12); $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, ($bx + $bw / 2 - 34), ($by - 50), 68, 92, 180, 180)
    }

    # corpo da sacola (branco com leve degrade)
    $corpo = Caminho-Arredondado $bx $by $bw $bh $br
    $dg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF(0, $by)), (New-Object System.Drawing.PointF(0, ($by + $bh))), (Cor '#FFFFFF'), (Cor '#DCE8FB'))
    $g.FillPath($dg, $corpo)
    # furinhos da alca na sacola
    if (-not $simples) {
        $furo = New-Object System.Drawing.SolidBrush((Cor '#2F6FD0'))
        $g.FillEllipse($furo, ($bx + $bw / 2 - 34 - 5), ($by + 12), 10, 10)
        $g.FillEllipse($furo, ($bx + $bw / 2 + 34 - 5), ($by + 12), 10, 10)
    }

    # grade 2x2 de "apps" na sacola
    $gw = $tile * 2 + $gap
    $gx = $bx + ($bw - $gw) / 2
    $gy = $by + ($bh - $gw) / 2 + $(if ($simples) { 0 } else { 8 })
    $cores = @('#2F7BEA', '#22B35A', '#F59E0B', '#9B51E0')
    $i = 0
    foreach ($lin in 0..1) { foreach ($col in 0..1) {
        $t = Caminho-Arredondado ($gx + $col * ($tile + $gap)) ($gy + $lin * ($tile + $gap)) $tile $tile 9
        $g.FillPath((New-Object System.Drawing.SolidBrush((Cor $cores[$i]))), $t)
        $i++
    } }
    $g.Dispose()
    return $bmp
}

function Reduzir($origem, [int]$tam) {
    $dst = New-Object System.Drawing.Bitmap $tam, $tam, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($origem, (New-Object System.Drawing.Rectangle(0, 0, $tam, $tam)), 0, 0, $origem.Width, $origem.Height, 'Pixel')
    $g.Dispose()
    return $dst
}

$completo = Desenhar $false
$simples = Desenhar $true
$tamanhos = 256, 128, 64, 48, 32, 24, 16
$imagens = @{}
foreach ($t in $tamanhos) {
    $fonte = if ($t -le 32) { $simples } else { $completo }
    $imagens[$t] = Reduzir $fonte $t
    $imagens[$t].Save((Join-Path $Saida "loja-$t.png"), [System.Drawing.Imaging.ImageFormat]::Png)
}

# ---- monta o .ico: BMP (32 bpp + mascara AND) ate 128 px, PNG em 256 px
$entradas = @()
foreach ($t in ($tamanhos | Sort-Object)) {
    $bm = $imagens[$t]
    if ($t -ge 256) {
        $ms = New-Object IO.MemoryStream; $bm.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $dados = $ms.ToArray()
    } else {
        $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($ms)
        $bw.Write([int]40); $bw.Write([int]$t); $bw.Write([int]($t * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $t - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $t; $x++) { $c = $bm.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
        $linhaMascara = [int]([Math]::Ceiling($t / 32.0) * 4)
        $bw.Write((New-Object byte[] ($linhaMascara * $t)))
        $bw.Flush(); $dados = $ms.ToArray()
    }
    $entradas += [pscustomobject]@{ Tam = $t; Dados = $dados }
}
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ico)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entradas.Count)
$offset = 6 + 16 * $entradas.Count
foreach ($e in $entradas) {
    $dim = if ($e.Tam -ge 256) { 0 } else { $e.Tam }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$e.Dados.Length); $w.Write([int]$offset); $offset += $e.Dados.Length
}
foreach ($e in $entradas) { $w.Write($e.Dados) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $Saida 'loja.ico'), $ico.ToArray())

# ---- folha de previa: fundo claro e escuro, em varios tamanhos
$prev = New-Object System.Drawing.Bitmap 760, 340
$g = [System.Drawing.Graphics]::FromImage($prev); $g.SmoothingMode = 'AntiAlias'
$g.FillRectangle((New-Object System.Drawing.SolidBrush((Cor '#F3F5F8'))), 0, 0, 380, 340)
$g.FillRectangle((New-Object System.Drawing.SolidBrush((Cor '#12161C'))), 380, 0, 380, 340)
foreach ($lado in 0, 380) {
    $g.DrawImage($imagens[128], ($lado + 20), 20, 128, 128)
    $g.DrawImage($imagens[64], ($lado + 170), 20, 64, 64)
    $g.DrawImage($imagens[48], ($lado + 250), 20, 48, 48)
    $g.DrawImage($imagens[32], ($lado + 316), 20, 32, 32)
    $g.DrawImage($imagens[24], ($lado + 170), 100, 24, 24)
    $g.DrawImage($imagens[16], ($lado + 210), 100, 16, 16)
    $g.DrawImage($imagens[256], ($lado + 20), 160, 160, 160)
}
$g.Dispose()
$prev.Save((Join-Path $Saida 'loja-previa.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"ico: " + (Get-Item (Join-Path $Saida 'loja.ico')).Length + " bytes, " + $entradas.Count + " tamanhos"
