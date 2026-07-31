<#
.SYNOPSIS
    Regenerates assets/ClaudeCounter.ico from the design in icon.svg.

.DESCRIPTION
    Draws the gauge icon with System.Drawing at each size Windows asks for and
    packs the results into a multi-resolution .ico with PNG-compressed entries.

    Redrawing per size beats rasterising one large bitmap: the ring stroke and
    the corner radius are scaled as a fraction of the canvas, so the 16 px
    entry stays legible instead of turning to mush.

    Run this only when the icon design changes. The generated .ico is checked
    in so a normal build needs nothing.

.EXAMPLE
    pwsh -File assets/generate-icon.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'ClaudeCounter.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # --- rounded-square plate, vertical green gradient -------------------
        $radius = [math]::Max(2.0, $size * 0.219)   # 56/256
        $plate  = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
        $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d      = $radius * 2
        $path.AddArc($plate.X, $plate.Y, $d, $d, 180, 90)
        $path.AddArc($plate.Right - $d, $plate.Y, $d, $d, 270, 90)
        $path.AddArc($plate.Right - $d, $plate.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($plate.X, $plate.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()

        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $plate,
            [System.Drawing.Color]::FromArgb(255, 63, 191, 87),
            [System.Drawing.Color]::FromArgb(255, 31, 122, 52),
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()

        # --- gauge ------------------------------------------------------------
        # 270 degree sweep opening at the bottom: starts at 135 deg (GDI+
        # measures clockwise from 3 o'clock), so the gap straddles 6 o'clock.
        # Small entries get a proportionally fatter ring pulled closer to the
        # edge. At the SVG's true proportions the 16 px gauge is a hairline and
        # reads as a smudge in the taskbar.
        if ($size -le 32) {
            $stroke = $size * 0.150
            $inset  = $size * 0.100 + $stroke / 2
        } else {
            $stroke = $size * 0.117                 # 30/256
            $inset  = $size * 0.141 + $stroke / 2   # centre the ring on r=92/256
        }
        # Precompute: PowerShell's comma binds tighter than '*', so an inline
        # "$size - 2 * $inset" inside New-Object's argument list parses as an
        # array multiplication and blows up.
        $arcSide = $size - 2 * $inset
        $arc = New-Object System.Drawing.RectangleF($inset, $inset, $arcSide, $arcSide)

        $track = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(71, 255, 255, 255), $stroke)
        $track.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $track.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($track, $arc, 135, 270)
        $track.Dispose()

        $fill = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(255, 255, 255, 255), $stroke)
        $fill.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $fill.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($fill, $arc, 135, 195)           # ~72% of the sweep
        $fill.Dispose()

        # --- hub ---------------------------------------------------------------
        # Dropped below 24 px: at that scale it merges with the ring and the
        # whole glyph reads as a blob.
        if ($size -ge 24) {
            $hub = $size * 0.086                    # r = 22/256
            $hubX = $size / 2 - $hub
            $hubD = $hub * 2
            $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
            $g.FillEllipse($white, [single]$hubX, [single]$hubX, [single]$hubD, [single]$hubD)
            $white.Dispose()
        }
    }
    finally { $g.Dispose() }

    return $bmp
}

# ---------------------------------------------------------------- pack the ico
$pngs = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $Sizes) {
    $bmp = New-IconBitmap $size
    try {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs.Add($ms.ToArray())
        $ms.Dispose()
    } finally { $bmp.Dispose() }
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
try {
    # ICONDIR
    $w.Write([uint16]0)                 # reserved
    $w.Write([uint16]1)                 # type: 1 = icon
    $w.Write([uint16]$Sizes.Count)

    # ICONDIRENTRY per image. Offsets follow the whole directory.
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $size = $Sizes[$i]
        $data = $pngs[$i]
        # 256 is encoded as 0 in the single width/height byte.
        $dim = if ($size -ge 256) { [byte]0 } else { [byte]$size }
        $w.Write($dim)                  # width
        $w.Write($dim)                  # height
        $w.Write([byte]0)               # palette entries (0 = truecolour)
        $w.Write([byte]0)               # reserved
        $w.Write([uint16]1)             # colour planes
        $w.Write([uint16]32)            # bits per pixel
        $w.Write([uint32]$data.Length)
        $w.Write([uint32]$offset)
        $offset += $data.Length
    }

    foreach ($data in $pngs) { $w.Write($data) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
}
finally {
    $w.Dispose()
    $out.Dispose()
}

$kb = [math]::Round((Get-Item $OutputPath).Length / 1KB, 1)
Write-Host "Wrote $OutputPath ($kb KB, $($Sizes.Count) sizes: $($Sizes -join ', '))"
