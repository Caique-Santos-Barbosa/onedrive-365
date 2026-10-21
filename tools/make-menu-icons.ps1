# Ícones do submenu "OneDrive 365" (traço fino, estilo Fluent) em src\assets\menuicons\*.ico
# e as 16 cores de pasta em src\assets\foldercolors\*.ico (mesma paleta do "Cor da pasta" do OneDrive).
Add-Type -AssemblyName System.Drawing
$root = Join-Path $PSScriptRoot '..\src\assets'
$menuDir = Join-Path $root 'menuicons'; $colorDir = Join-Path $root 'foldercolors'
New-Item -ItemType Directory -Force $menuDir, $colorDir | Out-Null

function Write-Ico($path, $sizes, [scriptblock]$draw) {
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
        & $draw $g ($s / 20.0) $s
        $g.Dispose()
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray()
    }
    $fs = [IO.File]::Create($path); $w = New-Object IO.BinaryWriter $fs
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $px = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$px); $w.Write([byte]$px); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset); $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }; $w.Close()
}

function Pen($k) { $p = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 36, 36, 36)), ([math]::Max(1.0, 1.25 * $k)); $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'; $p }
function P($x, $y, $k) { New-Object Drawing.PointF ($x * $k), ($y * $k) }
$menuSizes = 16, 20, 24, 32, 40, 48

# Compartilhar: caixa com seta saindo
Write-Ico (Join-Path $menuDir 'share.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawLines($p, [Drawing.PointF[]]@((P 8 4 $k), (P 4.5 4 $k), (P 3.5 5 $k), (P 3.5 15.5 $k), (P 4.5 16.5 $k), (P 15 16.5 $k), (P 16 15.5 $k), (P 16 12 $k)))
    $g.DrawLine($p, (P 8.5 11.5 $k), (P 16.5 3.5 $k)); $g.DrawLines($p, [Drawing.PointF[]]@((P 11.5 3.5 $k), (P 16.5 3.5 $k), (P 16.5 8.5 $k)))
}
# Copiar link: dois elos
Write-Ico (Join-Path $menuDir 'link.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawArc($p, 2.5 * $k, 6.5 * $k, 7 * $k, 7 * $k, 90, 180); $g.DrawLine($p, (P 6 6.5 $k), (P 8.5 6.5 $k)); $g.DrawLine($p, (P 6 13.5 $k), (P 8.5 13.5 $k))
    $g.DrawArc($p, 10.5 * $k, 6.5 * $k, 7 * $k, 7 * $k, 270, 180); $g.DrawLine($p, (P 11.5 6.5 $k), (P 14 6.5 $k)); $g.DrawLine($p, (P 11.5 13.5 $k), (P 14 13.5 $k))
    $g.DrawLine($p, (P 7 10 $k), (P 13 10 $k))
}
# Gerenciar acesso: pessoa
Write-Ico (Join-Path $menuDir 'access.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawEllipse($p, 6.5 * $k, 2.5 * $k, 7 * $k, 7 * $k)
    $g.DrawArc($p, 3.5 * $k, 11 * $k, 13 * $k, 11 * $k, 180, 180)
}
# Exibir online: globo
Write-Ico (Join-Path $menuDir 'web.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawEllipse($p, 2.5 * $k, 2.5 * $k, 15 * $k, 15 * $k); $g.DrawEllipse($p, 6.5 * $k, 2.5 * $k, 7 * $k, 15 * $k)
    $g.DrawLine($p, (P 2.5 10 $k), (P 17.5 10 $k))
}
# Abrir com salvamento automático: documento com setas circulares (salva sozinho na nuvem)
Write-Ico (Join-Path $menuDir 'office.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawLines($p, [Drawing.PointF[]]@((P 9 17 $k), (P 4.5 17 $k), (P 3.5 16 $k), (P 3.5 3.5 $k), (P 4.5 2.5 $k), (P 10.5 2.5 $k), (P 14.5 6.5 $k), (P 14.5 8 $k)))
    $g.DrawLines($p, [Drawing.PointF[]]@((P 10.5 2.5 $k), (P 10.5 6.5 $k), (P 14.5 6.5 $k)))
    $g.DrawArc($p, 10.5 * $k, 10 * $k, 7 * $k, 7 * $k, 200, 250)
    $g.DrawLines($p, [Drawing.PointF[]]@((P 10.3 10.6 $k), (P 10.6 12.6 $k), (P 12.6 12.3 $k)))
}
# Mover para o OneDrive 365: nuvem com seta subindo
Write-Ico (Join-Path $menuDir 'move.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $g.DrawArc($p, 2.5 * $k, 8 * $k, 6 * $k, 6 * $k, 90, 180)
    $g.DrawArc($p, 5 * $k, 3.5 * $k, 9 * $k, 9 * $k, 180, 150)
    $g.DrawArc($p, 11 * $k, 7.5 * $k, 6.5 * $k, 6.5 * $k, 250, 200)
    $g.DrawLine($p, (P 5.5 14 $k), (P 7.5 14 $k)); $g.DrawLine($p, (P 12.5 14 $k), (P 14.3 14 $k))
    $g.DrawLine($p, (P 10 17.5 $k), (P 10 9.5 $k)); $g.DrawLines($p, [Drawing.PointF[]]@((P 7.5 12 $k), (P 10 9.5 $k), (P 12.5 12 $k)))
}
# Cor da pasta: paleta
Write-Ico (Join-Path $menuDir 'color.ico') $menuSizes { param($g, $k)
    $p = Pen $k
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(2.5 * $k, 2.5 * $k, 15 * $k, 15 * $k, 60, 300); $g.DrawPath($p, $path)
    $g.DrawEllipse($p, 10.5 * $k, 11.5 * $k, 3 * $k, 3 * $k)
    foreach ($c in @(@(6, 8), @(9, 5.5), @(13, 6.5))) { $g.FillEllipse([Drawing.Brushes]::Black, ($c[0] - 0.9) * $k, ($c[1] - 0.9) * $k, 1.8 * $k, 1.8 * $k) }
}

# 16 cores de pasta (pasta estilo Windows 11: aba + corpo, frente um pouco mais clara)
$colors = [ordered]@{
    yellow = '#FFC83D'; red = '#E5432F'; orange = '#F07A16'; green = '#3C9A44'; teal = '#25958F'; blue = '#1C83D4'; purple = '#8B52C8'; pink = '#C9479F'
    gray = '#9FA5AB'; lightred = '#F7A99E'; lightorange = '#FBBE85'; lightgreen = '#8FD192'; lightteal = '#7CD1CB'; lightblue = '#83C3F5'; lightpurple = '#C7A6EE'; lightpink = '#F2A6D8'
}
foreach ($name in $colors.Keys) {
    $base = [Drawing.ColorTranslator]::FromHtml($colors[$name])
    $back = [Drawing.Color]::FromArgb(255, [int]($base.R * 0.82), [int]($base.G * 0.82), [int]($base.B * 0.82))
    Write-Ico (Join-Path $colorDir "$name.ico") @(16, 20, 24, 32, 48, 64, 256) { param($g, $k)
        $back2 = New-Object Drawing.SolidBrush $back; $front = New-Object Drawing.SolidBrush $base
        $tab = New-Object Drawing.Drawing2D.GraphicsPath
        $tab.AddLines([Drawing.PointF[]]@((P 1.5 4 $k), (P 7.5 4 $k), (P 9.5 6 $k), (P 18.5 6 $k), (P 18.5 16.5 $k), (P 1.5 16.5 $k))); $tab.CloseFigure()
        $g.FillPath($back2, $tab)
        $g.FillRectangle($front, 1.5 * $k, 7.5 * $k, 17 * $k, 9.5 * $k)
    }
}
"ícones do menu e 16 cores gerados"

