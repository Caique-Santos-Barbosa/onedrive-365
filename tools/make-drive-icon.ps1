# Gera src\assets\drive.ico (logo de 4 quadrados) em 16/24/32/48/64/256 px, entradas PNG
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\assets\drive.ico'
$sizes = 16, 24, 32, 48, 64, 256
$colors = '#F25022', '#7FBA00', '#00A4EF', '#FFB900'
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $pad = [math]::Max(1, [math]::Round($s * 0.06))
    $gap = [math]::Max(1, [math]::Round($s * 0.04))
    $q = [math]::Floor(($s - 2 * $pad - $gap) / 2)
    for ($i = 0; $i -lt 4; $i++) {
        $x = $pad + ($i % 2) * ($q + $gap); $y = $pad + [math]::Floor($i / 2) * ($q + $gap)
        $g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($colors[$i]))), $x, $y, $q, $q)
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
"ícone da unidade: $out"
