<#
.SYNOPSIS
    VMonitorVDD (仮想ディスプレイドライバ) をビルドし、テスト署名する。

.DESCRIPTION
    Windows は署名されていないドライバを読み込まない。配布用の WHQL 署名には
    EV 証明書と Microsoft への提出が必要なため、個人利用・開発用には
    「自己署名証明書 ＋ テスト署名モード」を使う。

    このスクリプトが行うこと:
      1. ドライバ DLL のビルド
      2. 自己署名コード署名証明書の作成（初回のみ、CurrentUser\My に格納）
      3. 証明書を .cer として書き出し（インストーラーが配布先へ取り込む）
      4. Inf2Cat でカタログ (.cat) を生成
      5. カタログとドライバ DLL に署名
      6. 成果物を dist フォルダにまとめる

    生成物を実際に読み込ませるには、対象 PC で管理者権限で以下が必要:
      - 証明書を Root と TrustedPublisher に取り込む
      - bcdedit /set testsigning on を実行して再起動
    これらはインストーラー (VMonitorSetup.exe) が自動で行う。

.PARAMETER SkipBuild
    ビルドを飛ばして署名だけ行う。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File driver\build-and-sign.ps1
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$DriverDir  = $PSScriptRoot
$ProjectDir = Join-Path $DriverDir 'VMonitorVDD'
$OutDir     = Join-Path $DriverDir 'bin\x64\Release'
$DistDir    = Join-Path $DriverDir 'dist'

$CertSubject   = 'CN=vmonitor Test Certificate'
$CertFriendly  = 'vmonitor Test Signing'
$CerPath       = Join-Path $DistDir 'MyTestCert.cer'

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

# ${env:ProgramFiles(x86)} は括弧のせいで文字列内に展開できないため、
# 環境変数として明示的に取得する。
$ProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')

function Find-LatestTool([string]$relativePath) {
    $kit = Join-Path $ProgramFilesX86 'Windows Kits\10\bin'
    # 結果が 1 件だと配列にならず文字列が返るため、
    # 添字で取ると先頭の 1 文字になってしまう。Select-Object で取り出す。
    $found = Get-ChildItem -Path $kit -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName $relativePath } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if (-not $found) {
        throw "$relativePath が Windows Kits に見つかりません。WDK をインストールしてください。"
    }

    return $found
}

function Find-MSBuild {
    $vswhere = Join-Path $ProgramFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
        if ($path) { return $path }
    }
    throw 'MSBuild が見つかりません。Visual Studio または Build Tools をインストールしてください。'
}

# ── 1. ビルド ──────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Step 'ドライバをビルドしています...'

    $msbuild = Find-MSBuild
    & $msbuild (Join-Path $ProjectDir 'VMonitorVDD.vcxproj') `
        /p:Configuration=Release /p:Platform=x64 `
        /p:SolutionDir="$DriverDir\" /v:minimal /nologo

    if ($LASTEXITCODE -ne 0) { throw "ドライバのビルドに失敗しました (終了コード $LASTEXITCODE)。" }
}

if (-not (Test-Path (Join-Path $OutDir 'VMonitorVDD.dll'))) {
    throw "ビルド成果物が見つかりません: $OutDir\VMonitorVDD.dll"
}

# ── 2. 配布フォルダの用意 ───────────────────────────────────────────────
Write-Step '配布フォルダを準備しています...'

if (Test-Path $DistDir) { Remove-Item $DistDir -Recurse -Force }
New-Item -ItemType Directory -Path $DistDir | Out-Null

Copy-Item (Join-Path $OutDir 'VMonitorVDD.dll') $DistDir

# INF はビルド出力にあるもの（DriverVer がビルド時刻で打ち直されたもの）を配布する。
# ソースの INF をそのまま配ると DriverVer が更新されず、
# 修正版を入れても「既にシステムに存在します」と判定されて古いものが使われ続ける。
$stampedInf = Join-Path $OutDir 'VMonitorVDD.inf'
$sourceInf  = Join-Path $ProjectDir 'VMonitorVDD.inf'

if (Test-Path $stampedInf) { Copy-Item $stampedInf $DistDir }
else                       { Copy-Item $sourceInf  $DistDir }

# AOA 用の WinUSB INF も同じ配布フォルダに入れる。
#
# こちらは自前のバイナリを持たず、Windows 内蔵の winusb.sys を
# Android のアクセサリーインターフェースに割り当てるだけの INF。
# Inf2Cat は フォルダ内の INF それぞれについてカタログを作るので、
# 同居させておけば署名まで一度に済む。
$aoaInf = Join-Path $DriverDir 'VMonitorAOA\VMonitorAOA.inf'

if (Test-Path $aoaInf) {
    Copy-Item $aoaInf $DistDir

    # ここでは打ち直さない。両方まとめて下で行う。
}

# ── DriverVer を打ち直す ───────────────────────────────────────────────
#
# 据え置きにすると、修正版を配っても「既に同じかそれより新しいものが
# 入っています」と判定されて古い INF が使われ続ける。
#
# 日付は UTC で入れること。stampinf の '*' は現地時刻を書くが、
# Inf2Cat は UTC で「未来の日付か」を判定する。日本のように UTC より
# 進んでいる地域では、深夜 0 時から朝 9 時のあいだだけ現地の日付が
# UTC の日付を追い越し、
#
#   22.9.7: DriverVer set to a date in the future
#
# で署名が通らなくなる。実際それで止まった。
Write-Step 'DriverVer を打ち直しています...'

$stampinf  = Find-LatestTool 'x86\stampinf.exe'
$stampDate = [DateTime]::UtcNow.ToString('MM/dd/yyyy')

Write-Host "   日付 (UTC): $stampDate"

foreach ($name in @('VMonitorVDD.inf', 'VMonitorAOA.inf')) {
    $target = Join-Path $DistDir $name
    if (-not (Test-Path $target)) { continue }

    & $stampinf -f $target -d $stampDate -v '*'

    if ($LASTEXITCODE -ne 0) {
        Write-Host "   stampinf に失敗しました: $name" -ForegroundColor Yellow
    }
}

# ── 3. 自己署名証明書 ──────────────────────────────────────────────────
Write-Step 'コード署名証明書を用意しています...'

$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host '   新しい自己署名証明書を作成します。'
    $cert = New-SelfSignedCertificate `
        -Subject $CertSubject `
        -FriendlyName $CertFriendly `
        -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5)
} else {
    Write-Host "   既存の証明書を使用します (期限: $($cert.NotAfter.ToString('yyyy-MM-dd')))。"
}

Export-Certificate -Cert $cert -FilePath $CerPath -Force | Out-Null
Write-Host "   証明書を書き出しました: $CerPath"

# ── 4. カタログ生成 ────────────────────────────────────────────────────
Write-Step 'カタログファイルを生成しています...'

$inf2cat = Find-LatestTool 'x86\Inf2Cat.exe'

# 10_x64 は Windows 10 以降の x64 を対象とする指定
& $inf2cat /driver:"$DistDir" /os:10_x64 /verbose

if ($LASTEXITCODE -ne 0) {
    throw "Inf2Cat に失敗しました (終了コード $LASTEXITCODE)。INF の記述を確認してください。"
}

$catFiles = @(Get-ChildItem -Path $DistDir -Filter '*.cat')
if ($catFiles.Count -eq 0) { throw 'カタログファイルが生成されませんでした。' }

$catFiles | ForEach-Object { Write-Host "   生成: $($_.Name)" }

# ── 5. 署名 ────────────────────────────────────────────────────────────
Write-Step 'ドライバとカタログに署名しています...'

$signtool = Find-LatestTool 'x64\signtool.exe'
$thumbprint = $cert.Thumbprint

$signTargets = @($catFiles | ForEach-Object { $_.FullName }) + @(Join-Path $DistDir 'VMonitorVDD.dll')

foreach ($target in $signTargets) {
    & $signtool sign /fd SHA256 /sha1 $thumbprint /t http://timestamp.digicert.com "$target"

    if ($LASTEXITCODE -ne 0) {
        # タイムスタンプサーバーに繋がらない環境でも署名自体は行えるようにする
        Write-Host '   タイムスタンプ付与に失敗したため、タイムスタンプなしで署名します。' -ForegroundColor Yellow
        & $signtool sign /fd SHA256 /sha1 $thumbprint "$target"
        if ($LASTEXITCODE -ne 0) { throw "署名に失敗しました: $target" }
    }

    Write-Host "   署名: $(Split-Path $target -Leaf)"
}

# ── 6. 検証 ────────────────────────────────────────────────────────────
Write-Step '署名を確認しています...'

# 署名が付いていること自体を確認する。
#
# チェーンの検証はここでは通らないのが正常。自己署名証明書は
# まだ信頼されたルートに入っていないため、signtool verify は
# 「certificate chain ... terminated in a root certificate which is not trusted」
# を返す。証明書を Root / TrustedPublisher に取り込んだ時点で解消する。
$dllPath = Join-Path $DistDir 'VMonitorVDD.dll'
$signature = Get-AuthenticodeSignature -FilePath $dllPath

Write-Host "   署名者 : $($signature.SignerCertificate.Subject)"
Write-Host "   状態   : $($signature.Status)  (UnknownError = 未信頼のルート。取り込み前は正常)"

if (-not $signature.SignerCertificate) {
    throw "署名が付いていません: $dllPath"
}

Write-Host ''
Write-Host '完了しました。' -ForegroundColor Green
Write-Host "配布物: $DistDir"
Get-ChildItem $DistDir | ForEach-Object { Write-Host "  - $($_.Name)" }
Write-Host ''
Write-Host '対象 PC でドライバを読み込ませるには、管理者権限で次が必要です:' -ForegroundColor Yellow
Write-Host '  1. certutil -addstore -f Root MyTestCert.cer'
Write-Host '  2. certutil -addstore -f TrustedPublisher MyTestCert.cer'
Write-Host '  3. pnputil /add-driver VMonitorVDD.inf /install'
Write-Host '  4. Root\VMonitorVDD のデバイスノードを作成'
Write-Host ''
Write-Host 'これらは VMonitorSetup.exe が自動で実行します。'
Write-Host ''
Write-Host 'VMonitorVDD は UMDF (ユーザーモード) ドライバのため、'
Write-Host 'テスト署名モードもセキュアブートの無効化も必要ありません。'
