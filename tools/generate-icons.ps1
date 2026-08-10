<#
.SYNOPSIS
    vmonitor のアプリアイコンを書き出す。

.DESCRIPTION
    採用案「拡張」— 横長の画面（PC）に縦長の画面（スマホ）が重なり、
    続きが映っている形。

    SVG を経由せず System.Drawing で直接描く。SVG を変換する道具は
    環境によって入っていたりいなかったりするので、依存を増やさない。

    出力:
      mobile-app/android/app/src/main/res/mipmap-*/ic_launcher.png  (48〜192)
      tools/icons/playstore-512.png                                  (ストア用)
      tools/icons/vmonitor.ico                                       (16/32/48/256)
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Root      = Split-Path $PSScriptRoot -Parent
$IconDir   = Join-Path $PSScriptRoot 'icons'
$MipmapDir = Join-Path $Root 'mobile-app\android\app\src\main\res'

New-Item -ItemType Directory -Force -Path $IconDir | Out-Null

# ── 配色 ───────────────────────────────────────────────────────────────
# 青はアプリの UI で使っている #2563EB に合わせてある。
$Accent = [System.Drawing.Color]::FromArgb(0x25, 0x63, 0xEB)
$Ink    = [System.Drawing.Color]::FromArgb(0x10, 0x15, 0x1C)
$Plate  = [System.Drawing.Color]::FromArgb(0xF4, 0xF6, 0xFA)

function New-RoundedPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2

    if ($r -le 0) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
        return $path
    }

    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()

    return $path
}

<#
    1 枚描く。

    形は 64x64 の座標で決めてあるので、出力サイズに合わせて倍率をかける。
    こうしておくと、どのサイズでも同じ見た目になる。
#>
function New-IconBitmap([int]$size, [bool]$withPlate = $true) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 64.0

    # 下地。透過のままだと、暗いタスクバーで黒い枠線が消える。
    if ($withPlate) {
        # 変数名は色の $Plate と衝突させないこと。
        # PowerShell は大文字小文字を区別しないため、$plate と書くと色が消える。
        $platePath  = New-RoundedPath 0 0 $size $size ($size * 0.18)
        $plateBrush = New-Object System.Drawing.SolidBrush($Plate)
        $g.FillPath($plateBrush, $platePath)
        $plateBrush.Dispose(); $platePath.Dispose()
    }

    # 小さいほど余白が邪魔になるので、少しだけ内側の余白を詰める
    $inset = if ($size -le 32) { 1.0 } else { 3.0 }
    $k     = ($size - $inset * 2) / 64.0

    function Fill([double]$x, [double]$y, [double]$w, [double]$h, [double]$r, $color) {
        $path  = New-RoundedPath ($inset + $x * $k) ($inset + $y * $k) ($w * $k) ($h * $k) ($r * $k)
        $brush = New-Object System.Drawing.SolidBrush($color)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # PC の画面（横長）
    Fill 2 12 38 27 3.5 $Accent

    # スマホ（縦長）。枠は塗りの重ねで作る。
    Fill 36 20 26 38 6 $Ink
    Fill 40 24 18 30 3 $Plate
    Fill 43 28 12 22 2 $Accent

    $g.Dispose()
    return $bmp
}

<#
.SYNOPSIS
    iOS 用に 1 枚描く。

    Android 用との違いは 2 点だけ。透過を残さないことと、
    角を丸めないこと。どちらも iOS 側の決まりによる
    （角丸は OS が付けるので、こちらで付けると二重になる）。
#>
function New-IosIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # 隅まで塗りつぶす。透過は 1 ピクセルも残さない。
    $g.Clear($Plate)

    # OS が角を切り落とすぶん、絵柄を内側へ寄せる。
    # 寄せないと、スマホの角が丸みに削られる。
    $inset = $size * 0.12
    $k     = ($size - $inset * 2) / 64.0

    function FillIos([double]$x, [double]$y, [double]$w, [double]$h, [double]$r, $color) {
        $path  = New-RoundedPath ($inset + $x * $k) ($inset + $y * $k) ($w * $k) ($h * $k) ($r * $k)
        $brush = New-Object System.Drawing.SolidBrush($color)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # 絵柄は Android 用と揃える（PC の画面 + スマホ）
    FillIos 2 12 38 27 3.5 $Accent

    FillIos 36 20 26 38 6 $Ink
    FillIos 40 24 18 30 3 $Plate
    FillIos 43 28 12 22 2 $Accent

    $g.Dispose()
    return $bmp
}

# ── Android ────────────────────────────────────────────────────────────
Write-Host '==> Android のアイコンを書き出しています...' -ForegroundColor Cyan

$densities = @{
    'mipmap-mdpi'    =  48
    'mipmap-hdpi'    =  72
    'mipmap-xhdpi'   =  96
    'mipmap-xxhdpi'  = 144
    'mipmap-xxxhdpi' = 192
}

foreach ($entry in $densities.GetEnumerator() | Sort-Object Value) {
    $dir = Join-Path $MipmapDir $entry.Key
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $bmp = New-IconBitmap $entry.Value
    $out = Join-Path $dir 'ic_launcher.png'
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host ("    {0,-16} {1}px" -f $entry.Key, $entry.Value)
}

# ストア用
$store = New-IconBitmap 512
$storePath = Join-Path $IconDir 'playstore-512.png'
$store.Save($storePath, [System.Drawing.Imaging.ImageFormat]::Png)
$store.Dispose()
Write-Host "    playstore-512.png"

# ── iOS ────────────────────────────────────────────────────────────────
#
# iOS のアイコンには 2 つ決まりがある。
#
#   1. 透過を含めてはいけない。App Store は弾くし、端末上でも
#      背景が抜けて汚く見える。
#   2. 角丸を自分で付けてはいけない。OS が同じ形に切り抜くので、
#      付けると二重に丸まって縁が痩せる。
#
# つまり「隅まで塗った、角の立った正方形」を出す。
# Android 用とは別に描き直す必要があり、流用はできない。
Write-Host ''
Write-Host '==> iOS のアイコンを書き出しています...' -ForegroundColor Cyan

$AppIconDir = Join-Path $Root 'mobile-app\ios\Runner\Assets.xcassets\AppIcon.appiconset'

if (-not (Test-Path $AppIconDir)) {
    Write-Host "    見つかりません: $AppIconDir" -ForegroundColor Yellow
} else {
    # 必要な寸法は Contents.json が持っている。
    # ここに書き写すと、Xcode 側の更新に追従できず食い違う。
    $contents = Get-Content (Join-Path $AppIconDir 'Contents.json') -Raw | ConvertFrom-Json

    $wanted = @{}   # ファイル名 → ピクセル数

    foreach ($image in $contents.images) {
        if (-not $image.filename) { continue }

        $base  = [double]($image.size -split 'x')[0]
        $scale = [double]($image.scale -replace 'x', '')

        $wanted[$image.filename] = [int][Math]::Round($base * $scale)
    }

    foreach ($entry in $wanted.GetEnumerator() | Sort-Object Value) {
        $bmp = New-IosIconBitmap $entry.Value
        $bmp.Save((Join-Path $AppIconDir $entry.Key),
                  [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        Write-Host ("    {0,-32} {1}px" -f $entry.Key, $entry.Value)
    }
}

# ── Windows (.ico) ─────────────────────────────────────────────────────
#
# .ico は「ヘッダ + サイズごとの見出し + PNG の中身」を並べただけの形。
# 変換ツールを持ち込まずに自分で組める。
Write-Host ''
Write-Host '==> Windows の .ico を書き出しています...' -ForegroundColor Cyan

$icoSizes = @(16, 32, 48, 256)
$blobs    = @()

foreach ($size in $icoSizes) {
    $bmp    = New-IconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $blobs += ,@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()

    Write-Host ("    {0}px" -f $size)
}

$icoPath = Join-Path $IconDir 'vmonitor.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)                 # 予約
$bw.Write([uint16]1)                 # 1 = アイコン
$bw.Write([uint16]$blobs.Count)

# 見出しは固定長 16 バイト。中身はその後ろに順に置く。
$offset = 6 + 16 * $blobs.Count

foreach ($blob in $blobs) {
    # 256px は 0 で表す（1 バイトに収まらないため）
    $dim = if ($blob.Size -ge 256) { 0 } else { $blob.Size }

    $bw.Write([byte]$dim)            # 幅
    $bw.Write([byte]$dim)            # 高さ
    $bw.Write([byte]0)               # パレット数（PNG なので 0）
    $bw.Write([byte]0)               # 予約
    $bw.Write([uint16]1)             # プレーン数
    $bw.Write([uint16]32)            # ビット深度
    $bw.Write([uint32]$blob.Bytes.Length)
    $bw.Write([uint32]$offset)

    $offset += $blob.Bytes.Length
}

foreach ($blob in $blobs) { $bw.Write($blob.Bytes) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host ''
Write-Host '完了しました。' -ForegroundColor Green
Write-Host ("  {0}  ({1:N1} KB)" -f $icoPath, ((Get-Item $icoPath).Length / 1KB))
Write-Host ("  {0}" -f $storePath)
