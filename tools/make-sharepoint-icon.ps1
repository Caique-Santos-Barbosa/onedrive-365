# Gera src\assets\sharepoint.ico: prédio branco sobre quadrado azul arredondado (pastas do SharePoint)
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\assets\sharepoint.ico'
$sizes = 16, 20, 24, 32, 48, 64, 256

function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure(); $p
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 100.0
    $bg = RoundRect (6 * $k) (6 * $k) (88 * $k) (88 * $k) (16 * $k)
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF 0, $s), ([Drawing.ColorTranslator]::FromHtml('#2B88D8')), ([Drawing.ColorTranslator]::FromHtml('#0F5CA8'))
    $g.FillPath($grad, $bg)
    # prédio
    $white = [Drawing.Brushes]::White
    $g.FillRectangle($white, 30 * $k, 24 * $k, 40 * $k, 58 * $k)
    # janelas (some em tamanhos pequenos para não virar borrão)
    if ($s -ge 16) {
        $win = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#1A73C7'))
        foreach ($row in 0..3) { foreach ($col in 0..1) {
            $g.FillRectangle($win, (37 + $col * 16) * $k, (31 + $row * 11) * $k, 10 * $k, 6 * $k)
        } }
    }
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
"ícone SharePoint: $out"
