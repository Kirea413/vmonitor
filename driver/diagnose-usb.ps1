<#
.SYNOPSIS
    AOA (USB 直結) が使えない原因を調べる。

.DESCRIPTION
    Windows は、ドライバの当たっていない USB デバイスをユーザーモードの
    アプリから開かせない。AOA の切り替え指示 (ベンダーリクエスト 51/52/53)
    は、まだ通常モードの端末へ送る必要があるため、そこで開けないと
    何も始まらない。

    「同じアプリなのに端末によって動いたり動かなかったりする」の正体は、
    たいていこの割り当ての違いにある。何がどう割り当てられているかは
    デバイスマネージャーからでも見られるが、必要な項目が散らばっていて
    読み取りにくい。まとめて出す。

    管理者権限は不要。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File driver\diagnose-usb.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

$ReportPath = Join-Path $env:TEMP 'vmonitor-usb-diagnose.txt'
Start-Transcript -Path $ReportPath -Force | Out-Null

Write-Host ''
Write-Host '=== vmonitor USB 診断 ===' -ForegroundColor Cyan
Write-Host ''

# アクセサリーモードの PID。AoaDevice.AccessoryPids と揃えること。
$AccessoryPids = @('2D00', '2D01', '2D02', '2D03', '2D04', '2D05')

function Get-Prop {
    param($Device, [string]$Key)

    try {
        (Get-PnpDeviceProperty -InstanceId $Device.InstanceId -KeyName $Key -ErrorAction Stop).Data
    } catch {
        $null
    }
}

$devices = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -like 'USB\VID_*' }

if (-not $devices) {
    Write-Host 'USB デバイスが 1 つも見つかりませんでした。' -ForegroundColor Yellow
    Stop-Transcript | Out-Null
    return
}

$rows = foreach ($d in $devices) {
    $id = $d.InstanceId

    # $PID は PowerShell が持っている読み取り専用の変数（プロセス ID）。
    # そのまま使うと代入できずに落ちる。
    $vidHex = if ($id -match 'VID_([0-9A-Fa-f]{4})') { $Matches[1].ToUpper() } else { '????' }
    $pidHex = if ($id -match 'PID_([0-9A-Fa-f]{4})') { $Matches[1].ToUpper() } else { '????' }

    [pscustomobject]@{
        VID      = $vidHex
        PID      = $pidHex
        状態     = $d.Status
        ドライバ = (Get-Prop $d 'DEVPKEY_Device_Service')
        問題     = (Get-Prop $d 'DEVPKEY_Device_ProblemCode')
        名前     = $d.FriendlyName
        Id       = $id
    }
}

Write-Host '--- 繋がっている USB デバイス ---' -ForegroundColor Cyan
$rows | Sort-Object VID, PID |
    Format-Table VID, PID, 状態, ドライバ, 名前 -AutoSize | Out-String -Width 200 | Write-Host

# ── アクセサリーモードの端末 ────────────────────────────────
$accessory = $rows | Where-Object { $_.VID -eq '18D1' -and $AccessoryPids -contains $_.PID }

Write-Host '--- 判定 ---' -ForegroundColor Cyan
Write-Host ''

if ($accessory) {
    foreach ($a in $accessory) {
        Write-Host ("アクセサリーモードの端末: PID=0x{0}" -f $a.PID) -ForegroundColor Green

        if ($a.ドライバ -eq 'WinUSB') {
            Write-Host '  WinUSB が当たっています。ここは問題ありません。' -ForegroundColor Green
        } else {
            Write-Host ("  WinUSB ではなく '{0}' が当たっています。" -f $a.ドライバ) -ForegroundColor Red
            Write-Host '  VMonitorAOA ドライバが入っていないか、この PID に対応していません。'
        }
    }
    Write-Host ''
}

# ── 切り替え前とおぼしき端末 ────────────────────────────────
#
# Android かどうかは VID だけでは決められない。開けるかどうかが
# 本質なので、ドライバの割り当てで振り分ける。
$unbound = $rows | Where-Object {
    $_.状態 -ne 'OK' -or [string]::IsNullOrWhiteSpace($_.ドライバ)
}

if ($unbound) {
    Write-Host 'ドライバが当たっていないデバイスがあります:' -ForegroundColor Yellow
    $unbound | Format-Table VID, PID, 状態, 名前 -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host 'この状態のデバイスは、アプリから開けません。'
    Write-Host ''
}

# ── adb ─────────────────────────────────────────────────────
$adb = Get-Process -Name 'adb' -ErrorAction SilentlyContinue

if ($adb) {
    Write-Host 'adb サーバーが動いています。' -ForegroundColor Yellow
    Write-Host 'インターフェースを掴んでいると、こちらから開けません。'
    Write-Host '`adb kill-server` を実行してから、もう一度お試しください。'
} else {
    Write-Host 'adb サーバーは動いていません。'
}

Write-Host ''
Write-Host ('この内容は次の場所にも残しました: {0}' -f $ReportPath) -ForegroundColor Cyan
Write-Host '動かない端末を挿した状態で実行し、この内容を添えて報告してください。'
Write-Host ''

Stop-Transcript | Out-Null
