# ohmyZip.ico 생성: src/OhMyZip.App/Theme/AppIcon.xaml 의 DrawingImage 를 크기별 PNG 로 그려 ICO 하나에 담는다(32px 이하는 Oz.AppIconSmall, 그보다 크면 Oz.AppIcon).
# 사용: powershell -NoProfile -ExecutionPolicy Bypass -File ohmyzip/packaging/make_icon.ps1 [-Preview <png>]
# 아이콘 그림을 고치면 이 스크립트를 다시 돌리고 바뀐 ohmyZip.ico 를 함께 커밋한다.
param([string]$Preview = "")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OhMyZip.App'
$xamlPath = Join-Path $app 'Theme\AppIcon.xaml'
$icoPath = Join-Path $app 'ohmyZip.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

$stream = [System.IO.File]::OpenRead($xamlPath)
try { $dict = [System.Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$image = $dict['Oz.AppIcon']
$small = $dict['Oz.AppIconSmall']
if ($null -eq $image -or $null -eq $small) { throw "Oz.AppIcon/Oz.AppIconSmall not found in $xamlPath" }

function Render([int]$size) {
    # 작은 크기에서는 세부가 1px 미만으로 뭉개지므로 단순판을 그린다.
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
    # 쉼표: byte[] 가 파이프라인에서 바이트 단위로 풀리지 않게 한다.
    return , $ms.ToArray()
}

# ICO: ICONDIR(6) + ICONDIRENTRY(16)×n + PNG 데이터(Windows Vista 이후 PNG 항목 지원).
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
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
"wrote $icoPath ($($out.Length) bytes, sizes: $($sizes -join ','))"

if ($Preview) {
    # 확인용 한 장: 밝은/어두운 바탕 위에 크기별로 실제 픽셀 크기와 8배 확대본.
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
