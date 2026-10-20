# Gera src\assets\app.ico (nuvem azul com seta de download) em 16/24/32/48/64/256 px, entradas PNG
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\assets\app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 256.0
    $cloud = New-Object Drawing.Drawing2D.GraphicsPath
    $cloud.AddEllipse(20 * $k, 96 * $k, 100 * $k, 100 * $k)
    $cloud.AddEllipse(70 * $k, 46 * $k, 130 * $k, 130 * $k)
    $cloud.AddEllipse(140 * $k, 92 * $k, 100 * $k, 100 * $k)
    $cloud.AddRectangle((New-Object Drawing.RectangleF (70 * $k), (140 * $k), (120 * $k), (56 * $k)))
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $s), ([Drawing.Color]::FromArgb(40, 140, 230)), ([Drawing.Color]::FromArgb(10, 90, 180))
    $region = New-Object Drawing.Region $cloud
    $g.FillRegion($brush, $region)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([math]::Max(1.6, 22 * $k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen, 130 * $k, 92 * $k, 130 * $k, 170 * $k)
    $g.DrawLines($pen, [Drawing.PointF[]]@((New-Object Drawing.PointF (100 * $k), (142 * $k)), (New-Object Drawing.PointF (130 * $k), (172 * $k)), (New-Object Drawing.PointF (160 * $k), (142 * $k))))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"ícone: $out"
