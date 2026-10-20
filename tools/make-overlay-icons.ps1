# Gera os selos de status (overlay-cloud/local/sync.ico) em src\assets.
# O Windows sobrepõe o ícone inteiro ao do arquivo, então o selo fica no canto inferior esquerdo e o resto é transparente.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\assets'
$sizes = 16, 24, 32, 48, 64, 256

function Save-Ico($name, [scriptblock]$draw) {
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $d = [math]::Max(9, [math]::Round($s * 0.5))          # diâmetro do selo
        $r = New-Object Drawing.RectangleF 0, ($s - $d), $d, $d
        & $draw $g $r $d
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    $fs = [IO.File]::Create((Join-Path $assets $name)); $w = New-Object IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $px = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$px); $w.Write([byte]$px); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    $w.Close()
}

function Disc($g, $r, $color) {
    $g.FillEllipse([Drawing.Brushes]::White, $r)
    $inner = [Drawing.RectangleF]::Inflate($r, -[math]::Max(1, $r.Width * 0.08), -[math]::Max(1, $r.Width * 0.08))
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($color))), $inner)
}

function WhitePen($d) {
    $p = New-Object Drawing.Pen ([Drawing.Color]::White), ([math]::Max(1.3, $d * 0.13))
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'; $p
}

# Só na nuvem: nuvem azul vazada sobre fundo branco
Save-Ico 'overlay-cloud.ico' {
    param($g, $r, $d)
    $g.FillEllipse([Drawing.Brushes]::White, $r)
    $k = $d / 100.0; $x = $r.X; $y = $r.Y
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x + 14 * $k, $y + 40 * $k, 30 * $k, 30 * $k, 90, 180)
    $path.AddArc($x + 30 * $k, $y + 24 * $k, 40 * $k, 40 * $k, 180, 180)
    $path.AddArc($x + 56 * $k, $y + 40 * $k, 30 * $k, 30 * $k, 270, 180)
    $path.CloseFigure()
    $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#0F6CBD')), ([math]::Max(1.2, $d * 0.09))
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $path)
}

# Disponível no PC: círculo verde com ✓
Save-Ico 'overlay-local.ico' {
    param($g, $r, $d)
    Disc $g $r '#0E7A0D'
    $k = $d / 100.0; $x = $r.X; $y = $r.Y
    $g.DrawLines((WhitePen $d), [Drawing.PointF[]]@(
        (New-Object Drawing.PointF ($x + 28 * $k), ($y + 52 * $k)),
        (New-Object Drawing.PointF ($x + 44 * $k), ($y + 68 * $k)),
        (New-Object Drawing.PointF ($x + 72 * $k), ($y + 36 * $k))))
}

# Sincronizando: círculo azul com duas setas girando
Save-Ico 'overlay-sync.ico' {
    param($g, $r, $d)
    Disc $g $r '#0F6CBD'
    $k = $d / 100.0; $x = $r.X; $y = $r.Y
    $pen = WhitePen $d
    $arc = New-Object Drawing.RectangleF ($x + 28 * $k), ($y + 28 * $k), (44 * $k), (44 * $k)
    $g.DrawArc($pen, $arc, 200, 130)
    $g.DrawArc($pen, $arc, 20, 130)
    $g.DrawLines($pen, [Drawing.PointF[]]@(
        (New-Object Drawing.PointF ($x + 66 * $k), ($y + 22 * $k)), (New-Object Drawing.PointF ($x + 68 * $k), ($y + 36 * $k)), (New-Object Drawing.PointF ($x + 54 * $k), ($y + 34 * $k))))
    $g.DrawLines($pen, [Drawing.PointF[]]@(
        (New-Object Drawing.PointF ($x + 34 * $k), ($y + 78 * $k)), (New-Object Drawing.PointF ($x + 32 * $k), ($y + 64 * $k)), (New-Object Drawing.PointF ($x + 46 * $k), ($y + 66 * $k))))
}
'selos gerados'
