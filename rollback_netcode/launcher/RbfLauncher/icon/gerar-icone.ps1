# ---------------------------------------------------------------------------
# gerar-icone.ps1 - desenha o icone do launcher (um controle de arcade) e grava
# app.ico ao lado deste script.
#
#   & "D:\Rollback Netcode\rollback_netcode\launcher\RbfLauncher\icon\gerar-icone.ps1"
#
# Desenho proprio, feito aqui com System.Drawing - nao e o emoji do Windows
# (aquele e um glifo da fonte Segoe UI Emoji, da Microsoft, e nao pode ir
# embutido no nosso exe). Cores da paleta do launcher (Theme.xaml).
#
# Cada tamanho e desenhado de novo, nao reduzido do 256: reduzir borra o
# contorno, e em 16 px os botoes viram sujeira - por isso somem abaixo de 32.
#
# -Preview <arquivo.png> grava tambem o 256 em PNG, para olhar sem instalar.
# ---------------------------------------------------------------------------
param(
    [string]$Out = (Join-Path $PSScriptRoot "app.ico"),
    [string]$Preview = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function Cor([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    return [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Tudo e desenhado num espaco de 256x256 e escalado pela transformacao.
function Desenhar([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($S / 256.0, $S / 256.0)

    $detalhe = $S -ge 32

    # ---- ladrilho de fundo --------------------------------------------------
    $tile = RoundedPath 8 8 240 240 52
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 8), (New-Object System.Drawing.PointF 0, 248), (Cor "#2A3040"), (Cor "#12141A")
    $g.FillPath($bg, $tile)
    if ($detalhe) {
        $pen = New-Object System.Drawing.Pen (Cor "#3A4252"), 4
        $g.DrawPath($pen, $tile)
    }

    # ---- base (tampo + frente) ----------------------------------------------
    $frente = RoundedPath 36 166 184 56 20
    $g.FillPath((New-Object System.Drawing.SolidBrush (Cor "#20242D")), $frente)
    $tampo = RoundedPath 36 146 184 50 20
    $tb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 146), (New-Object System.Drawing.PointF 0, 196), (Cor "#4A5263"), (Cor "#353C4A")
    $g.FillPath($tb, $tampo)

    # ---- botoes no tampo ----------------------------------------------------
    if ($detalhe) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#2A6BE0")), 52, 164, 34, 18)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#4C8DFF")), 52, 159, 34, 18)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#B8A52F")), 170, 164, 34, 18)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#E8D44D")), 170, 159, 34, 18)
    }

    # ---- haste --------------------------------------------------------------
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#171A21")), 102, 160, 52, 20)
    $hb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 118, 0), (New-Object System.Drawing.PointF 138, 0), (Cor "#E7EAF0"), (Cor "#7C8597")
    $g.FillRectangle($hb, 118, 96, 20, 74)

    # ---- bola ---------------------------------------------------------------
    $bola = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bola.AddEllipse(78, 28, 100, 100)
    $pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $bola
    $pb.CenterPoint = New-Object System.Drawing.PointF 112, 58
    $pb.CenterColor = Cor "#FF7A72"
    $pb.SurroundColors = @(Cor "#B8302B")
    $g.FillPath($pb, $bola)
    if ($detalhe) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (Cor "#FFFFFF" 120)), 98, 44, 32, 22)
    }

    $g.Dispose()
    return $bmp
}

$tamanhos = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $tamanhos) {
    $bmp = Desenhar $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256 -and $Preview) { $bmp.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}

# ---- .ico: cabecalho, um diretorio de 16 bytes por imagem, e os PNGs --------
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$tamanhos.Count)
$offset = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $s = $tamanhos[$i]
    $lado = if ($s -ge 256) { 0 } else { $s }   # 0 quer dizer 256
    $w.Write([byte]$lado); $w.Write([byte]$lado)
    $w.Write([byte]0); $w.Write([byte]0)          # sem paleta, reservado
    $w.Write([UInt16]1); $w.Write([UInt16]32)     # planos, bits por pixel
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

Write-Host ":: icone gravado: $Out ($((Get-Item $Out).Length) bytes, $($tamanhos -join '/') px)"
