<#
.SYNOPSIS
    残ってしまった vmonitor の古い登録と残骸を取り除く。

.DESCRIPTION
    「プログラムの追加と削除」に vmonitor の登録が 2 つ並んでしまうことがある。

      - "vmonitor 1.0.0"  … VMonitorSetup.exe が自分で作る登録
      - "vmonitor"        … Inno Setup が作る登録

    どちらも同じ C:\Program Files\vmonitor を指すため、片方をアンインストール
    すると、もう片方が使っているファイルごと消える。実際にそれが起きて、
    アプリ本体・ドライバ・署名証明書がまとめて失われた。

    このスクリプトは、その残骸を安全に片付ける。ドライバや証明書は
    既に無い前提だが、残っていれば併せて取り除く。

    片付けたあとに installer\output\vmonitor-*-setup.exe を実行して
    入れ直すこと。

.NOTES
    管理者権限で実行すること。
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    # 消さずに、何を消すつもりかだけ表示する
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:ProgramFiles 'vmonitor'

$UninstallKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\vmonitor',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F41484CA-0A01-4733-A9A7-C5A730D3A5CE}_is1'
)

$Shortcuts = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\vmonitor'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'vmonitor.lnk')
)

function Test-Administrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host '管理者権限が必要です。PowerShell を「管理者として実行」してください。' -ForegroundColor Red
    exit 1
}

function Remove-Thing($description, $action) {
    if ($DryRun) {
        Write-Host "  [確認のみ] $description" -ForegroundColor Yellow
        return
    }

    try {
        & $action
        Write-Host "  削除: $description" -ForegroundColor Green
    } catch {
        Write-Host "  失敗: $description — $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
Write-Host '=== vmonitor の古い登録と残骸を片付けます ===' -ForegroundColor Cyan
if ($DryRun) { Write-Host '（確認のみ。実際には消しません）' -ForegroundColor Yellow }

# ── 1. 動いていれば止める ───────────────────────────────────────────────
Write-Host ''
Write-Host '[1/5] 起動中の vmonitor を終了します'

foreach ($proc in @(Get-Process VMonitor.UI -ErrorAction SilentlyContinue)) {
    Remove-Thing "プロセス VMonitor.UI ($($proc.Id))" {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { $proc.Kill() }
    }
}

# ── 2. 「追加と削除」の登録 ─────────────────────────────────────────────
#
# ここが本題。同じフォルダを指す登録が 2 つあると、
# 片方を消したときにもう片方の中身まで巻き添えになる。
Write-Host ''
Write-Host '[2/5] 「プログラムの追加と削除」の登録を取り除きます'

foreach ($key in $UninstallKeys) {
    if (-not (Test-Path $key)) { continue }

    $name = (Get-ItemProperty $key -ErrorAction SilentlyContinue).DisplayName
    Remove-Thing "登録 '$name' ($key)" { Remove-Item $key -Recurse -Force }
}

# ── 3. ショートカット ───────────────────────────────────────────────────
Write-Host ''
Write-Host '[3/5] ショートカットを取り除きます'

foreach ($path in $Shortcuts) {
    if (-not (Test-Path $path)) { continue }
    Remove-Thing $path { Remove-Item $path -Recurse -Force }
}

# ── 4. ドライバと証明書 ─────────────────────────────────────────────────
#
# 既に消えている見込みだが、中途半端に残っていると
# 入れ直したときに古い方が使われ続けることがある。
Write-Host ''
Write-Host '[4/5] 残っているドライバと証明書を確認します'

$oemInfs = @()
$enum = & pnputil /enum-drivers 2>&1
$current = $null

foreach ($line in $enum) {
    if ($line -match '(oem\d+\.inf)') { $current = $matches[1]; continue }
    if ($current -and $line -match 'vmonitor') { $oemInfs += $current; $current = $null }
}

if ($oemInfs.Count -eq 0) {
    Write-Host '  DriverStore に vmonitor のドライバはありません。'
} else {
    foreach ($inf in ($oemInfs | Select-Object -Unique)) {
        Remove-Thing "ドライバパッケージ $inf" {
            & pnputil /delete-driver $inf /uninstall /force | Out-Null
        }
    }
}

$certs = @(Get-ChildItem Cert:\LocalMachine\Root, Cert:\LocalMachine\TrustedPublisher -ErrorAction SilentlyContinue |
           Where-Object { $_.Subject -like '*vmonitor*' })

if ($certs.Count -eq 0) {
    Write-Host '  信頼ストアに vmonitor の証明書はありません。'
} else {
    foreach ($cert in $certs) {
        Remove-Thing "証明書 $($cert.Subject) ($($cert.PSParentPath.Split('\')[-1]))" {
            Remove-Item $cert.PSPath -Force
        }
    }
}

# ── 5. インストール先フォルダ ───────────────────────────────────────────
Write-Host ''
Write-Host '[5/5] インストール先を取り除きます'

if (Test-Path $InstallDir) {
    Remove-Thing $InstallDir { Remove-Item $InstallDir -Recurse -Force }
} else {
    Write-Host "  $InstallDir はありません。"
}

Write-Host ''
Write-Host '片付けが終わりました。' -ForegroundColor Green
Write-Host ''
Write-Host '次に、インストーラーを実行して入れ直してください:'
Write-Host '  installer\output\vmonitor-1.1.0-setup.exe'
Write-Host ''
