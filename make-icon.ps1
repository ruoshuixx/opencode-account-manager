# Generates Assets/app.ico (multi-size) for OpenCodeAccountManager.
# Design: rounded square, indigo->violet gradient, white key glyph (accounts/credentials).
# Re-run after tweaking:  powershell -File make-icon.ps1
param(
    [string]$OutIco = "src\AccountManager.App\Assets\app.ico",
    [string]$OutPng = "icon-preview.png"
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $s = $size / 256.0

    # rounded-square background
    $r = 57 * $s
    $d = $r * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - 1 - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - 1 - $d, $size - 1 - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - 1 - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($size, $size),
        [System.Drawing.Color]::FromArgb(255, 61, 78, 219),
        [System.Drawing.Color]::FromArgb(255, 123, 63, 242))
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    # white key glyph, rotated so the shaft points to the bottom-right
    $g.TranslateTransform($size / 2.0, $size / 2.0)
    $g.RotateTransform(45)

    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 26 * $s)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ringR = 34 * $s
    $g.DrawEllipse($pen, -38 * $s - $ringR, -$ringR, 2 * $ringR, 2 * $ringR)
    $g.DrawLine($pen, -6 * $s, 0, 66 * $s, 0)

    $pen2 = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 20 * $s)
    $pen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen2, 34 * $s, 2 * $s, 34 * $s, 30 * $s)
    $g.DrawLine($pen2, 58 * $s, 2 * $s, 58 * $s, 30 * $s)
    $pen.Dispose()
    $pen2.Dispose()

    $g.Dispose()
    return $bmp
}

# render frames
$frames = @()
$pngs = @()
foreach ($sz in $sizes) {
    $f = New-Frame $sz
    if ($sz -eq 256) {
        $f.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $m = [System.IO.MemoryStream]::new()
    $f.Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $m.ToArray()
    $frames += , $f
}

# pack ICO (PNG-compressed entries, supported since Windows Vista)
$out = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
for ($i = 0; $i -lt $frames.Count; $i++) {
    $b = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$b); $bw.Write([byte]$b)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()

$dir = Split-Path $OutIco
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())
$frames | ForEach-Object { $_.Dispose() }
$bw.Dispose()

"Wrote $OutIco ($((Get-Item $OutIco).Length) bytes, $($frames.Count) sizes) and preview $OutPng"
