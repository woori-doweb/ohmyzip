# Generates ohMypic.ico: renders the DrawingImage resources of src/OhMyPic.App/Theme/AppIcon.xaml into PNGs of each size
# and packs them into one ICO (Op.AppIconSmall for 32 px and below, Op.AppIcon above).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File ohmypic/packaging/make_icon.ps1 [-Preview <png>]
# Re-run this script after changing the icon drawing and commit the updated ohMypic.ico together with it.
param([string]$Preview = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OhMyPic.App'
$xamlPath = Join-Path $app 'Theme\AppIcon.xaml'
$icoPath = Join-Path $app 'ohMypic.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$stream = [System.IO.File]::OpenRead($xamlPath)
try { $dict = [System.Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$image = $dict['Op.AppIcon']
$small = $dict['Op.AppIconSmall']
if ($null -eq $image -or $null -eq $small) { throw "Op.AppIcon/Op.AppIconSmall not found in $xamlPath" }

function Render([int]$size) {
    # Fine details blur below 1 px at small sizes, so the simplified drawing is used there.
    $source = if ($size -le 32) { $small } else { $image }
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($source, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    return $bmp
}
function PngBytes($bmp) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    # Leading comma: keep the byte[] from being unrolled by the pipeline.
    return , $ms.ToArray()
}

# ICO: ICONDIR(6) + ICONDIRENTRY(16) x n + PNG data (PNG entries are supported since Windows Vista).
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $pngs.Add((PngBytes (Render $s))) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
# Atomic replace: write a temp file next to the target, then move it over.
$tmp = "$icoPath.tmp"
[System.IO.File]::WriteAllBytes($tmp, $out.ToArray())
Move-Item -LiteralPath $tmp -Destination $icoPath -Force
"wrote $icoPath ($($out.Length) bytes, sizes: $($sizes -join ','))"

if ($Preview) {
    # One preview sheet: every size at real pixel size on light and dark backgrounds, plus 16 px enlarged 8x.
    $shown = 16, 24, 32, 48, 64, 128, 256
    $width = 20 + ($shown | Measure-Object -Sum).Sum + 20 * $shown.Count
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawRectangle([System.Windows.Media.Brushes]::White, $null, (New-Object System.Windows.Rect 0, 0, $width, 300))
    $dc.DrawRectangle((New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 32, 32))), $null, (New-Object System.Windows.Rect 0, 300, $width, 300))
    $x = 20
    foreach ($s in $shown) {
        $bmp = Render $s
        $dc.DrawImage($bmp, (New-Object System.Windows.Rect $x, 20, $s, $s))
        $dc.DrawImage($bmp, (New-Object System.Windows.Rect $x, 320, $s, $s))
        $x += $s + 20
    }
    $big = Render 16
    $dc.DrawImage($big, (New-Object System.Windows.Rect 20, 160, 128, 128))
    $dc.Close()
    $sheet = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $width, 600, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($visual)
    [System.IO.File]::WriteAllBytes($Preview, (PngBytes $sheet))
    "preview $Preview"
}
