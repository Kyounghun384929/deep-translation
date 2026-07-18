# 앱 아이콘(.ico) 생성 — 블루→퍼플 그라데이션 배경에 말풍선 두 개(채움 "한" / 아웃라인 "A")
# 크기별 단순화: 40px 미만은 말풍선 속 글자 생략, 24px 미만은 말풍선 하나 + 텍스트 라인 힌트만
param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\DeepTranslation\Assets\app.ico")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$OutPath = [System.IO.Path]::GetFullPath($OutPath)
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null

function Argb([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function New-RoundRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# 글자를 GraphicsPath로 만들어 (cx, cy)에 잉크 기준 광학 중심 정렬
function New-TextPath([string]$text, [string]$family, [single]$em, [single]$cx, [single]$cy) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ff = New-Object System.Drawing.FontFamily($family)
    $origin = New-Object System.Drawing.PointF(0, 0)
    $p.AddString($text, $ff, [int][System.Drawing.FontStyle]::Bold, $em, $origin, [System.Drawing.StringFormat]::GenericDefault)
    $b = $p.GetBounds()
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate($cx - $b.X - $b.Width / 2, $cy - $b.Y - $b.Height / 2)
    $p.Transform($m)
    $p
}

# 말풍선: 둥근 사각형 + 아래쪽 꼬리를 닫힌 단일 figure로 (외곽선을 그려도 내부 선이 안 생김)
function New-BubblePath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r,
                        [single]$tailX1, [single]$tailX2, [single]$tipX, [single]$tipY) {
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.StartFigure()
    $p.AddArc($x, $y, $d, $d, 180, 90)                          # 좌상
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)                # 우상
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)        # 우하
    $p.AddLine($tailX2, $y + $h, $tipX, $tipY)                  # 꼬리 내려가기
    $p.AddLine($tipX, $tipY, $tailX1, $y + $h)                  # 꼬리 올라오기
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)                 # 좌하
    $p.CloseFigure()
    $p
}

function Render-IconFrame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # 둥근 사각형 배경 (모서리 반경 22%) + 블루→퍼플 그라데이션
    $bg = New-RoundRectPath 0 0 $s $s ([Math]::Max(3, $s * 0.22))
    $rect = New-Object System.Drawing.RectangleF(-1, -1, ($s + 2), ($s + 2))
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, (Argb 255 79 124 255), (Argb 255 124 92 255), [single]45)
    $g.FillPath($bgBrush, $bg)

    if ($s -ge 24) {
        # 좌상단 방사형 하이라이트 조명
        $ep = New-Object System.Drawing.Drawing2D.GraphicsPath
        $ep.AddEllipse(($s * 0.18 - $s * 0.85), ($s * 0.08 - $s * 0.7), ($s * 1.7), ($s * 1.4))
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($ep)
        $pgb.CenterColor = (Argb 80 255 255 255)
        $pgb.SurroundColors = @((Argb 0 255 255 255))
        $g.SetClip($bg)
        $g.FillPath($pgb, $ep)
        $g.ResetClip()
        $pgb.Dispose(); $ep.Dispose()
    }

    $white = [System.Drawing.Brushes]::White

    if ($s -ge 24) {
        # 채움 말풍선 (좌상, 꼬리 좌하향)
        $b1 = New-BubblePath ($s * 0.11) ($s * 0.13) ($s * 0.52) ($s * 0.40) ($s * 0.115) `
                             ($s * 0.235) ($s * 0.345) ($s * 0.165) ($s * 0.645)
        # 아웃라인 말풍선 (우하, 꼬리 우하향) — 내부를 배경 그라데이션으로 되살려 앞뒤 겹침 표현
        $b2 = New-BubblePath ($s * 0.37) ($s * 0.46) ($s * 0.52) ($s * 0.38) ($s * 0.115) `
                             ($s * 0.655) ($s * 0.765) ($s * 0.815) ($s * 0.93)
        $g.FillPath($white, $b1)
        $g.SetClip($bg)
        $g.FillPath($bgBrush, $b2)
        $g.ResetClip()
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, ([Math]::Max(1.0, $s * 0.032)))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($pen, $b2)
        $pen.Dispose()

        if ($s -ge 40) {
            # 말풍선 속 글자 (소형에서는 생략)
            $han = New-TextPath "한" "Malgun Gothic" ($s * 0.235) ($s * 0.37) ($s * 0.325)
            $hanBrush = New-Object System.Drawing.SolidBrush((Argb 255 72 108 240))
            $g.FillPath($hanBrush, $han)
            $a = New-TextPath "A" "Segoe UI" ($s * 0.235) ($s * 0.63) ($s * 0.645)
            $g.FillPath($white, $a)
            $hanBrush.Dispose(); $han.Dispose(); $a.Dispose()
        }
        $b1.Dispose(); $b2.Dispose()
    }
    else {
        # 16px: 흰 말풍선 하나 + 텍스트 라인 힌트 2개 (극소 글자는 뭉개져서 획 힌트로 대체, 픽셀 격자에 스냅)
        $b1 = New-BubblePath ($s * 0.14) ($s * 0.17) ($s * 0.72) ($s * 0.50) ($s * 0.14) `
                             ($s * 0.30) ($s * 0.46) ($s * 0.24) ($s * 0.86)
        $g.FillPath($white, $b1)
        $b1.Dispose()
        $lineBrush = New-Object System.Drawing.SolidBrush((Argb 255 61 111 232))
        $bx = [int][Math]::Round($s * 0.28); $bh = [Math]::Max(1, [int][Math]::Round($s * 0.10))
        $g.FillRectangle($lineBrush, $bx, [int][Math]::Round($s * 0.31), [int][Math]::Round($s * 0.44), $bh)
        $g.FillRectangle($lineBrush, $bx, [int][Math]::Round($s * 0.50), [int][Math]::Round($s * 0.30), $bh)
        $lineBrush.Dispose()
    }

    $bgBrush.Dispose(); $bg.Dispose(); $g.Dispose()
    $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($s in $sizes) {
    $bmp = Render-IconFrame $s
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
