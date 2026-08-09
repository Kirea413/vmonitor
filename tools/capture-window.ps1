<#
.SYNOPSIS
    vmonitor のウィンドウを画像に撮る。

.DESCRIPTION
    画面全体の共有を使わず、対象のウィンドウだけを直接描き出す。
    PrintWindow はウィンドウ自身に「自分を描け」と頼む仕組みなので、
    最前面にある必要も、画面共有の許可も要らない。

.PARAMETER Out
    保存先。
#>

[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot 'shots\vmonitor.png'),
    [string]$ProcessName = 'VMonitor.UI'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Win32Capture {
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // 画面の拡大率をこのプロセスにも認識させる。
    //
    // 非対応のままだと GetWindowRect が論理サイズを返すのに対し、
    // PrintWindow は実ピクセルで描くため、小さいビットマップに
    // 大きい絵を描くことになり右下が切れる。
    // 「UI が見切れている」ように見えるが、実際は撮り方の問題。
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    public static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

    public static void MakeDpiAware() {
        try {
            if (SetProcessDpiAwarenessContext(PerMonitorV2)) return;
        } catch { }

        // 古い Windows 向けの控え
        try { SetProcessDPIAware(); } catch { }
    }

    public const int SW_RESTORE = 9;

    // PW_RENDERFULLCONTENT。これを付けないと、
    // ハードウェア描画している部分が黒く抜ける。
    public const uint PW_RENDERFULLCONTENT = 2;
}
'@

# 拡大率を認識させてから測る。ウィンドウを探すより先に行うこと。
[Win32Capture]::MakeDpiAware()

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

if (-not $proc) {
    Write-Host "$ProcessName のウィンドウが見つかりません。" -ForegroundColor Red
    exit 1
}

$handle = $proc.MainWindowHandle

# しまわれていると中身が描けないので、出しておく
if ([Win32Capture]::IsIconic($handle)) {
    [Win32Capture]::ShowWindow($handle, [Win32Capture]::SW_RESTORE) | Out-Null
    Start-Sleep -Milliseconds 700
}

$rect = New-Object Win32Capture+RECT
[Win32Capture]::GetWindowRect($handle, [ref]$rect) | Out-Null

$width  = $rect.Right  - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
    Write-Host "ウィンドウの大きさを取得できませんでした。" -ForegroundColor Red
    exit 1
}

$bmp = New-Object System.Drawing.Bitmap($width, $height,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

$g   = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()

$ok = [Win32Capture]::PrintWindow($handle, $hdc, [Win32Capture]::PW_RENDERFULLCONTENT)

$g.ReleaseHdc($hdc)
$g.Dispose()

if (-not $ok) {
    Write-Host "ウィンドウを描き出せませんでした。" -ForegroundColor Red
    $bmp.Dispose()
    exit 1
}

$dir = Split-Path $Out -Parent
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host ("撮影しました: {0}  ({1}x{2})" -f $Out, $width, $height) -ForegroundColor Green
