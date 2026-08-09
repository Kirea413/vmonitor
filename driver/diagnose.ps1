<#
.SYNOPSIS
    仮想ディスプレイドライバが読み込まれない原因を調べる。

.DESCRIPTION
    UMDF (ユーザーモードドライバフレームワーク) の運用ログは既定で無効になっており、
    ドライバの読み込みに失敗しても理由がどこにも残らない。
    このスクリプトはログを有効にしてデバイスを再起動し、
    UMDF が報告した実際の失敗理由を取り出す。

    管理者権限で実行すること。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File driver\diagnose.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$HardwareId  = 'Root\VMonitorVDD'
$UmdfLog     = 'Microsoft-Windows-DriverFrameworks-UserMode/Operational'
$DriverLog   = 'C:\ProgramData\vmonitor-driver.log'
$ReportPath  = 'C:\ProgramData\vmonitor-diagnose.txt'

# 昇格して別ウィンドウで動かした場合、画面の出力は閉じると消えてしまう。
# 後から読み返せるようにファイルへも記録する。
Start-Transcript -Path $ReportPath -Force | Out-Null

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# ── 管理者権限の確認 ───────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host 'このスクリプトは管理者権限で実行してください。' -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}

# ── 1. UMDF ログを有効にする ───────────────────────────────────────────
Write-Step 'UMDF の運用ログを有効にしています...'

& wevtutil.exe sl $UmdfLog /e:true /q:true 2>&1 | Out-Null
& wevtutil.exe cl $UmdfLog 2>&1 | Out-Null

Write-Host '   有効化しました（記録を消去）。'

# ── 2. 既存のドライバログを消す ────────────────────────────────────────
Remove-Item $DriverLog -ErrorAction SilentlyContinue

# ── 3. デバイスを再起動して読み込みをやり直させる ──────────────────────
Write-Step 'デバイスを再起動しています...'

$device = Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'ROOT\DISPLAY*' -and $_.FriendlyName -like '*vmonitor*' } |
    Select-Object -First 1

if (-not $device) {
    Write-Host '   vmonitor のデバイスが見つかりません。先にインストールしてください。' -ForegroundColor Red
    exit 1
}

Write-Host "   対象: $($device.InstanceId)  状態=$($device.Status) 問題=$($device.Problem)"

& pnputil.exe /restart-device "$($device.InstanceId)" 2>&1 | ForEach-Object { "   $_" }

Start-Sleep -Seconds 5

# ── 4. 結果を集める ────────────────────────────────────────────────────
Write-Step 'ドライバ側のログ'

if (Test-Path $DriverLog) {
    Get-Content $DriverLog | ForEach-Object { "   $_" }
} else {
    Write-Host '   （出力なし = DriverEntry に到達していない＝DLL が読み込まれていない）' -ForegroundColor Yellow
}

Write-Step 'UMDF が報告したイベント'

$events = Get-WinEvent -LogName $UmdfLog -MaxEvents 60 -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -in @('エラー','Error','警告','Warning') -or $_.Message -match 'VMonitor' }

if ($events) {
    $events | Select-Object -First 12 | ForEach-Object {
        Write-Host ("   [{0}] Id={1} {2}" -f $_.LevelDisplayName, $_.Id, $_.TimeCreated) -ForegroundColor Yellow
        ($_.Message -split "`n" | Select-Object -First 4) | ForEach-Object { "        $($_.Trim())" }
    }
} else {
    Write-Host '   （該当イベントなし）'
}

Write-Step 'デバイスの現在の状態'

Get-PnpDevice -InstanceId $device.InstanceId -ErrorAction SilentlyContinue |
    Select-Object Status, Problem, ProblemDescription |
    Format-List | Out-String | ForEach-Object { $_.TrimEnd() }

Write-Host ''
Write-Host '調査が終わったらログを無効に戻せます:' -ForegroundColor DarkGray
Write-Host "  wevtutil sl $UmdfLog /e:false" -ForegroundColor DarkGray

Stop-Transcript | Out-Null

Write-Host ''
Write-Host "結果を $ReportPath に保存しました。" -ForegroundColor Green
Write-Host 'このウィンドウを閉じて構いません。'
Start-Sleep -Seconds 3
