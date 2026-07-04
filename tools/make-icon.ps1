# 앱 아이콘(.ico) 생성 — 파란 그라데이션 배경에 흰색 "한" 글자
param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\DeepTranslation\Assets\app.ico")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$OutPath = [System.IO.Path]::GetFullPath($OutPath)
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # 둥근 사각형 배경
    $rad = [Math]::Max(3, [int]($s * 0.22))
    $d = $rad * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $c1 = [System.Drawing.Color]::FromArgb(255, 79, 124, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 124, 92, 255)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [single]45)
    $g.FillPath($brush, $path)

    # 중앙에 "한"
    $fontSize = [single]([Math]::Max(7, $s * 0.5))
    $font = New-Object System.Drawing.Font("Malgun Gothic", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, [single]($s * 0.03), $s, $s)
    $g.DrawString("한", $font, [System.Drawing.Brushes]::White, $textRect, $sf)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += ,@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
}

# ICO 컨테이너 작성 (PNG 프레임, Vista+ 형식)
$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)               # reserved
$bw.Write([uint16]1)               # type: icon
$bw.Write([uint16]$frames.Count)   # image count

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f.Size
    $b = $f.Bytes
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))  # width (0 = 256)
    $bw.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))  # height
    $bw.Write([byte]0)      # palette
    $bw.Write([byte]0)      # reserved
    $bw.Write([uint16]1)    # planes
    $bw.Write([uint16]32)   # bpp
    $bw.Write([uint32]$b.Length)
    $bw.Write([uint32]$offset)
    $offset += $b.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Dispose()
$fs.Dispose()

Write-Host "아이콘 생성 완료: $OutPath"
