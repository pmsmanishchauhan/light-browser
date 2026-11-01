# Draws the LightBrowser logo and writes assets\icon.ico (16-256 px) and assets\logo.png (512 px).
# Re-run after changing the design:  powershell -File tools\make-icon.ps1 [-Variant B] [-Preview path.png]
param([string]$Variant = 'B', [string]$Out = (Join-Path $PSScriptRoot '..\assets'), [string]$Preview = '')

Add-Type -AssemblyName System.Drawing

function New-Logo([int]$size, [string]$variant) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    $pad = $s * 0.04; $w = $s - 2 * $pad; $r = $s * 0.22

    # Rounded-square tile.
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90); $tile.AddArc($pad + $w - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $tile.AddArc($pad + $w - 2*$r, $pad + $w - 2*$r, 2*$r, 2*$r, 0, 90); $tile.AddArc($pad, $pad + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $tile.CloseFigure()

    if ($variant -eq 'A') {
        # Blue tile, white ring (the web) with three profile-coloured dots.
        $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)), ([System.Drawing.PointF]::new($s,$s)), ([System.Drawing.Color]::FromArgb(255,110,168,255)), ([System.Drawing.Color]::FromArgb(255,52,98,222))
        $g.FillPath($br, $tile)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($s * 0.085)
        $c = $s * 0.5; $rr = $s * 0.27
        $g.DrawEllipse($pen, $c - $rr, $c - $rr, 2*$rr, 2*$rr)
        $dots = @(@(-0.5, '#3DBE7A'), @(0.9, '#F5A442'), @(2.4, '#E5575B'))
        foreach ($d in $dots) {
            $x = $c + $rr * [math]::Cos($d[0] * 1.0) ; $y = $c + $rr * [math]::Sin($d[0] * 1.0)
            $dr = $s * 0.085
            $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($d[1]))), $x - $dr, $y - $dr, 2*$dr, 2*$dr)
        }
    } else {
        # Dark tile, three overlapping profile circles that glow where they meet (many accounts, one light browser).
        $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0,0)), ([System.Drawing.PointF]::new($s,$s)), ([System.Drawing.Color]::FromArgb(255,44,48,66)), ([System.Drawing.Color]::FromArgb(255,24,25,32))
        $g.FillPath($br, $tile)
        $cr = $s * 0.235
        $pts = @(@(0.5, 0.385, '#4F8EF7'), @(0.385, 0.62, '#3DBE7A'), @(0.615, 0.62, '#F5A442'))
        $g.SetClip($tile)
        foreach ($p in $pts) {
            $col = [System.Drawing.ColorTranslator]::FromHtml($p[2])
            $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, $col))
            $g.FillEllipse($b, $s * $p[0] - $cr, $s * $p[1] - $cr, 2*$cr, 2*$cr)
        }
        # Bright centre where all three overlap.
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse($s * 0.5 - $cr, $s * 0.385 - $cr, 2*$cr, 2*$cr)
        $reg = New-Object System.Drawing.Region $path
        foreach ($p in $pts[1..2]) {
            $p2 = New-Object System.Drawing.Drawing2D.GraphicsPath
            $p2.AddEllipse($s * $p[0] - $cr, $s * $p[1] - $cr, 2*$cr, 2*$cr)
            $reg.Intersect($p2)
        }
        $g.FillRegion((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))), $reg)
        $g.ResetClip()
    }
    $g.Dispose()
    $bmp
}

function Png-Bytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $ms.ToArray() }

if ($Preview) {
    $sheet = New-Object System.Drawing.Bitmap 760, 300
    $g = [System.Drawing.Graphics]::FromImage($sheet); $g.Clear([System.Drawing.Color]::FromArgb(255, 37, 38, 43))
    $x = 10
    foreach ($v in 'A', 'B') {
        $big = New-Logo 256 $v; $g.DrawImage($big, $x, 10, 256, 256)
        $sx = $x + 270
        foreach ($sz in 48, 32, 16) { $sm = New-Logo $sz $v; $g.DrawImage($sm, $sx, 10, $sz, $sz); $sx += $sz + 8 }
        $x += 380
    }
    $g.Dispose(); $sheet.Save($Preview); return
}

New-Item $Out -ItemType Directory -Force | Out-Null
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { $b = New-Logo $sz $Variant; [pscustomobject]@{ Size = $sz; Bytes = [byte[]](Png-Bytes $b) } }
$ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $d = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
[System.IO.File]::WriteAllBytes((Join-Path $Out 'icon.ico'), $ms.ToArray())
$big = New-Logo 512 $Variant; $big.Save((Join-Path $Out 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"wrote icon.ico ($($images.Count) sizes) and logo.png to $((Resolve-Path $Out).Path)"
