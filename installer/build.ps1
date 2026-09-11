<#
.SYNOPSIS
    vmonitor のインストーラーを一発で作る。

.DESCRIPTION
    これまで配布物は手で集めていた。そのため中身がソースより 1 か月古く、
    .iss が参照するファイルが存在しないという状態になっていた
    （インストーラーのコンパイル自体が通らなかった）。

    集める手順をここに一本化して、いつ実行しても今のソースから作れるようにする。

    行うこと:
      1. ネイティブ H.264 エンコーダー (C++) のビルド
         ※ dotnet build では作られない。忘れると映像が出ない。
      2. PC アプリの発行（自己完結。.NET ランタイム不要）
      3. セットアップ本体 (VMonitorSetup.exe) の発行
      4. ドライバ一式の確認
      5. payload への集約
      6. Inno Setup でのコンパイル

.PARAMETER SkipDriverBuild
    ドライバのビルドと署名を飛ばし、既存の driver\dist をそのまま使う。
    WDK が無い環境や、ドライバを変えていないときに使う。

.PARAMETER SkipInstaller
    payload を作るところまでで止め、Inno Setup のコンパイルを行わない。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build.ps1
#>

[CmdletBinding()]
param(
    [switch]$SkipDriverBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$InstallerDir = $PSScriptRoot
$RootDir      = Split-Path $InstallerDir -Parent
$PcClientDir  = Join-Path $RootDir 'pc-client'
$DriverDir    = Join-Path $RootDir 'driver'
$PayloadDir   = Join-Path $InstallerDir 'payload'
$AppStageDir  = Join-Path $PayloadDir 'app'
$DrvStageDir  = Join-Path $PayloadDir 'driver'

function Write-Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

$ProgramFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')

function Find-MSBuild {
    $vswhere = Join-Path $ProgramFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
        if ($path) { return $path }
    }
    throw 'MSBuild が見つかりません。Visual Studio または Build Tools をインストールしてください。'
}

function Find-ISCC {
    $candidates = @(
        (Join-Path $ProgramFilesX86 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) {
        throw 'ISCC.exe が見つかりません。Inno Setup 6 をインストールしてください。'
    }
    return $found
}

# ── 1. ネイティブエンコーダー ───────────────────────────────────────────
#
# これは dotnet build では作られない。忘れると VMonitor.Encoder.dll が
# 古いまま配布され、直したはずのエンコード周りが直っていないことになる。
Write-Step 'ネイティブ H.264 エンコーダーをビルドしています...'

$msbuild     = Find-MSBuild
$encoderProj = Join-Path $PcClientDir 'VMonitor.Encoder\VMonitor.Encoder.vcxproj'

& $msbuild $encoderProj `
    /p:Configuration=Release /p:Platform=x64 `
    /p:SolutionDir="$PcClientDir\" /v:minimal /nologo

if ($LASTEXITCODE -ne 0) { throw "ネイティブエンコーダーのビルドに失敗しました (終了コード $LASTEXITCODE)。" }

$encoderDll = Join-Path $PcClientDir 'bin\x64\Release\VMonitor.Encoder.dll'
if (-not (Test-Path $encoderDll)) {
    throw "エンコーダーの出力が見つかりません: $encoderDll"
}
Write-Host "   $encoderDll"

# ── 2. ドライバ ─────────────────────────────────────────────────────────
Write-Step 'ドライバを用意しています...'

$driverDist = Join-Path $DriverDir 'dist'

if (-not $SkipDriverBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $DriverDir 'build-and-sign.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'ドライバのビルド・署名に失敗しました。' }
} else {
    Write-Host '   ビルドを飛ばし、既存の dist を使います。'
}

# 揃っていないまま配ると、入れた先で「拡張ディスプレイが出ない」
# あるいは「USB 直結が繋がらない」という形でしか分からない。ここで止める。
$requiredDriverFiles = @(
    'VMonitorVDD.dll', 'VMonitorVDD.inf', 'vmonitorvdd.cat',
    'VMonitorAOA.inf', 'vmonitoraoa.cat',
    'MyTestCert.cer'
)

$missing = $requiredDriverFiles | Where-Object { -not (Test-Path (Join-Path $driverDist $_)) }

if ($missing) {
    throw ("ドライバファイルが足りません: {0}`n" -f ($missing -join ', ')) +
          "driver\build-and-sign.ps1 を実行してください（WDK が必要です）。"
}

Write-Host "   $driverDist"

# ── 3. PC アプリの発行 ──────────────────────────────────────────────────
#
# 自己完結で発行する。フレームワーク依存にすると、入れた先に
# .NET 8 デスクトップランタイムが無い場合、起動すらせずに終わる。
Write-Step 'PC アプリを発行しています（自己完結）...'

if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $AppStageDir -Force | Out-Null

# SatelliteResourceLanguages: .NET が持っている 13 か国語ぶんの
# 翻訳リソースを削る。中身は例外メッセージなどで、日本語の UI しか
# 出さないこのアプリでは使われない。それだけで 15 MB ある。
& dotnet publish (Join-Path $PcClientDir 'VMonitor.UI\VMonitor.UI.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:SatelliteResourceLanguages=ja `
    -o $AppStageDir --nologo -v q

if ($LASTEXITCODE -ne 0) { throw 'PC アプリの発行に失敗しました。' }

# 発行結果にエンコーダーが入っているか確かめる。
# ここが抜けたまま配ると、映像が 1 フレームも出ないインストーラーが出来上がる。
if (-not (Test-Path (Join-Path $AppStageDir 'VMonitor.Encoder.dll'))) {
    Write-Host '   発行物にエンコーダーが無いため、直接コピーします。' -ForegroundColor Yellow
    Copy-Item $encoderDll $AppStageDir
}

# ── 4. セットアップ本体の発行 ───────────────────────────────────────────
#
# ドライバの導入はこれに任せる。証明書の取り込み、古いパッケージの掃除、
# そして「ルート列挙デバイスの作成」まで面倒を見る。
#
# 単一ファイルで発行し、exe だけを持ってくる。
#
# 中にランタイムを丸ごと抱えるため 64 MB ほどあり、アプリのぶんと
# 合わせて同じものが 2 組になる。無駄は承知のうえ。
#
# 一度これをやめて同じフォルダへ並べたが、両方とも自己完結なので
# 共通のアセンブリが重なり、こちらが持つ .NET 8 同梱の版が、
# アプリが NuGet で持ち込んだ新しい版を上書きした。結果、
#
#   Could not load file or assembly 'System.Text.Json, Version=9.0.0.0'
#
# で全セッションが即死した。発行の順番を入れ替えても、別の
# アセンブリ（System.Text.Encodings.Web）で同じことが起きる。
# 容量より確実さを取る。
Write-Step 'セットアップ本体を発行しています（単一ファイル）...'

$setupStage = Join-Path $InstallerDir 'obj\setup'
if (Test-Path $setupStage) { Remove-Item $setupStage -Recurse -Force }

& dotnet publish (Join-Path $PcClientDir 'VMonitor.Installer\VMonitor.Installer.csproj') `
    -c Release -o $setupStage --nologo -v q

if ($LASTEXITCODE -ne 0) { throw 'セットアップ本体の発行に失敗しました。' }

$setupExe = Join-Path $setupStage 'VMonitorSetup.exe'
if (-not (Test-Path $setupExe)) { throw "VMonitorSetup.exe が見つかりません: $setupStage" }

# exe だけを持ってくる。他のファイルは持ち込まない
# （持ち込むとアプリ側のアセンブリと重なる）。
Copy-Item $setupExe $AppStageDir
Write-Host ("   {0}  ({1:N1} MB)" -f $setupExe, ((Get-Item $setupExe).Length / 1MB))

# ── 版の食い違いを出荷前に見つける ──────────────────────────────────────
#
# 2 つを同じフォルダへ発行している以上、片方の古い版がもう片方の
# 新しい版を上書きしうる。実際それで全セッションが即死した。
# 順番で回避してあるが、順番だけに頼らず、ここでも確かめる。
Write-Step '同居させたアセンブリの版を確認しています...'

$uiDeps = Get-Content (Join-Path $AppStageDir 'VMonitor.UI.deps.json') -Raw | ConvertFrom-Json
$checked = 0

foreach ($target in $uiDeps.targets.PSObject.Properties) {
    foreach ($entry in $target.Value.PSObject.Properties) {
        $runtimeFiles = $entry.Value.runtime
        if (-not $runtimeFiles) { continue }

        foreach ($file in $runtimeFiles.PSObject.Properties) {
            $wanted = $file.Value.assemblyVersion
            if (-not $wanted) { continue }

            $name = Split-Path $file.Name -Leaf
            $onDisk = Join-Path $AppStageDir $name
            if (-not (Test-Path $onDisk)) { continue }

            $actual = [System.Reflection.AssemblyName]::GetAssemblyName($onDisk).Version.ToString()
            $checked++

            if ($actual -ne $wanted) {
                throw ("{0} の版が違います。要求 {1} / 実物 {2}。 " -f $name, $wanted, $actual) +
                      "同じフォルダへ発行している別のプロジェクトが上書きした可能性があります。"
            }
        }
    }
}

Write-Host ("   {0} 個を照合し、食い違いはありませんでした。" -f $checked)

# ── iPhone / iPad の USB 転送 ─────────────────────────────────────────
#
# iOS は Android AOA を使えないため、Apple Mobile Device Support の
# usbmuxd と話す iproxy で、PC のローカルTCPポートを端末へ転送する。
# バイナリ配布物は版とハッシュを固定し、必要な4ファイルだけを取り出す。
#
# iproxy は GPL-2.0、リンク先の3ライブラリは LGPL-2.1。対応する正確な
# ソースアーカイブも一緒にインストールして、バイナリだけを配らない。
Write-Step 'iOS USB 転送ツールを用意しています...'

$VendorDir       = Join-Path $InstallerDir 'vendor'
$IosUsbVendorDir = Join-Path $VendorDir 'ios-usb'
$IosUsbStageDir  = Join-Path $AppStageDir 'tools\ios-usb'
$IosUsbSourceDir = Join-Path $IosUsbStageDir 'source'
$IosUsbZip       = Join-Path $IosUsbVendorDir 'libimobile-suite-v20260906-74585f8-w64.zip'
$IosUsbUrl       = 'https://github.com/jrjr/libimobiledevice-windows/releases/download/v20260906-74585f8/libimobile-suite-latest_w64.zip'
$IosUsbZipSha    = '86E7F9970AD5D668C8235697C9F8E02D2E3DD4EB8FE52FEC5CE6AACBC118714B'

New-Item -ItemType Directory -Path $IosUsbVendorDir -Force | Out-Null
New-Item -ItemType Directory -Path $IosUsbStageDir -Force | Out-Null
New-Item -ItemType Directory -Path $IosUsbSourceDir -Force | Out-Null

if (-not (Test-Path $IosUsbZip)) {
    Write-Host '   iproxy のWindowsビルドを取得しています...'
    Invoke-WebRequest -Uri $IosUsbUrl -OutFile $IosUsbZip -UseBasicParsing
}

$actualIosUsbZipSha = (Get-FileHash $IosUsbZip -Algorithm SHA256).Hash
if ($actualIosUsbZipSha -ne $IosUsbZipSha) {
    Remove-Item -LiteralPath $IosUsbZip -Force
    throw "iOS USB ツールの中身が想定と違います。`n  期待: $IosUsbZipSha`n  実際: $actualIosUsbZipSha"
}

$IosUsbExtractDir = Join-Path $InstallerDir 'obj\ios-usb-extract'
if (Test-Path $IosUsbExtractDir) {
    # InstallerDir 配下の固定された作業フォルダーだけを削除する。
    Remove-Item -LiteralPath $IosUsbExtractDir -Recurse -Force
}
Expand-Archive -LiteralPath $IosUsbZip -DestinationPath $IosUsbExtractDir

$iosUsbFiles = @{
    'iproxy.exe'                     = '28DDC8CFC6D1DBC6711B71408AE06C7D624DD59C35FEE1E682B4CEB41407989A'
    'libusbmuxd-2.0.dll'             = '6A91C7B7873FCB5CC9BD506E9E013E9121A34D8D5C0F590D13D4E9322B386425'
    'libimobiledevice-glue-1.0.dll'  = 'EFD6E7EE7A76EA5277584F4701720156B788FFEAAE78E3221C5AEA7CF3AE6CF1'
    'libplist-2.0.dll'               = '0D573BEB60856F5E58754F24B90C2B685FE40DED31093C7C755909616C8B8512'
}

foreach ($entry in $iosUsbFiles.GetEnumerator()) {
    $source = Join-Path $IosUsbExtractDir $entry.Key
    if (-not (Test-Path $source)) { throw "iOS USB ツールが足りません: $($entry.Key)" }

    $fileHash = (Get-FileHash $source -Algorithm SHA256).Hash
    if ($fileHash -ne $entry.Value) {
        throw "$($entry.Key) の中身が想定と違います。期待 $($entry.Value) / 実際 $fileHash"
    }

    Copy-Item -LiteralPath $source -Destination $IosUsbStageDir -Force
}

$iosUsbSources = @(
    @{
        Name = 'libusbmuxd'; Commit = '93eb168';
        Sha256 = '810E26DD849083E192176150519D372E800BD3EDBA341DF75E2A6FEF6DCEC364'
    },
    @{
        Name = 'libimobiledevice-glue'; Commit = 'da770a7';
        Sha256 = 'C3E36A0F99D419E2DFD1B7E8C43813342E2BF597EC22C60DF290635EEF6A56C6'
    },
    @{
        Name = 'libplist'; Commit = '32428ab';
        Sha256 = '6F7ADE2A3299662BC148836A105872A51039AA4CF9430EB4D96A23395EA1E214'
    }
)

foreach ($sourceInfo in $iosUsbSources) {
    $archiveName = "$($sourceInfo.Name)-$($sourceInfo.Commit).tar.gz"
    $archive = Join-Path $IosUsbVendorDir $archiveName
    $url = "https://codeload.github.com/libimobiledevice/$($sourceInfo.Name)/tar.gz/$($sourceInfo.Commit)"

    if (-not (Test-Path $archive)) {
        Write-Host "   対応ソースを取得しています: $archiveName"
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    }

    $sourceHash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($sourceHash -ne $sourceInfo.Sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "$archiveName の中身が想定と違います。期待 $($sourceInfo.Sha256) / 実際 $sourceHash"
    }

    Copy-Item -LiteralPath $archive -Destination $IosUsbSourceDir -Force
}

Copy-Item -LiteralPath (Join-Path $RootDir 'docs\ios-usb-third-party.md') `
    -Destination (Join-Path $IosUsbStageDir 'README.md') -Force

Write-Host ("   iproxy と依存DLL {0} 本、対応ソース {1} 件を収録しました。" -f `
    ($iosUsbFiles.Count - 1), $iosUsbSources.Count)

# ── 5. ドライバを payload へ ────────────────────────────────────────────
Write-Step 'payload にまとめています...'

New-Item -ItemType Directory -Path $DrvStageDir -Force | Out-Null
Copy-Item (Join-Path $driverDist '*') $DrvStageDir -Recurse -Force

# pdb は配らない。動作に要らず、容量だけ増える。
Get-ChildItem $AppStageDir -Filter '*.pdb' -Recurse | Remove-Item -Force

$appFiles = @(Get-ChildItem $AppStageDir -Recurse -File)
$appSize  = ($appFiles | Measure-Object -Property Length -Sum).Sum

Write-Host ("   アプリ : {0} ファイル / {1:N1} MB" -f $appFiles.Count, ($appSize / 1MB))
Write-Host ("   ドライバ: {0} ファイル" -f @(Get-ChildItem $DrvStageDir -File).Count)

# ── UsbDk を用意する ────────────────────────────────────────────────────
#
# 通常モードの Android は MTP ドライバの持ち物になっていることが多い。
# その状態では libusb から開けず、AOA の切り替え指示すら送れない。
# 実機では Pixel 9a が WUDFWpdMtp で握られていた。
#
# UsbDk は既存のドライバを外さずに USB へ到達できる。別途入れてもらう
# 案内では手間が増えるだけなので、こちらで同梱する。
#
# 中のカーネルドライバは二重署名になっている（Symantec のクロス署名と、
# Microsoft Windows Third Party Component CA 2014 による証明署名）。
# 後者があるおかげで、セキュアブートを有効にしたままでも読み込まれる。
Write-Step 'UsbDk を用意しています...'

$VendorDir  = Join-Path $InstallerDir 'vendor'
$UsbDkMsi   = Join-Path $VendorDir 'UsbDk_1.0.22_x64.msi'
$UsbDkUrl   = 'https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi'
$UsbDkSha   = '91F6F695E1E13C656024E6D3B55620BF08D8835EF05EE0496935BA6BB62466A5'

New-Item -ItemType Directory -Path $VendorDir -Force | Out-Null

if (-not (Test-Path $UsbDkMsi)) {
    Write-Host '   取得しています...'
    Invoke-WebRequest -Uri $UsbDkUrl -OutFile $UsbDkMsi -UseBasicParsing
}

# 取り違えや差し替えに気付けるよう、中身を照合してから配る。
$actual = (Get-FileHash $UsbDkMsi -Algorithm SHA256).Hash

if ($actual -ne $UsbDkSha) {
    Remove-Item $UsbDkMsi -Force
    throw "UsbDk の中身が想定と違います。`n  期待: $UsbDkSha`n  実際: $actual"
}

# 署名が生きていることも見る。カーネルドライバを配る以上、
# ここを黙って通すわけにはいかない。
$sig = Get-AuthenticodeSignature $UsbDkMsi

if ($sig.Status -ne 'Valid') {
    throw "UsbDk の署名が有効ではありません: $($sig.Status)"
}

Copy-Item $UsbDkMsi $PayloadDir -Force
Write-Host ("   {0}  ({1:N1} MB)  署名: {2}" -f `
    (Split-Path $UsbDkMsi -Leaf), ((Get-Item $UsbDkMsi).Length / 1MB), $sig.SignerCertificate.Subject.Split(',')[0])

# ── 6. インストーラーのコンパイル ───────────────────────────────────────
if ($SkipInstaller) {
    Write-Host ''
    Write-Host "payload まで作成しました: $PayloadDir" -ForegroundColor Green
    return
}

Write-Step 'Inno Setup でコンパイルしています...'

$iscc = Find-ISCC
& $iscc (Join-Path $InstallerDir 'vmonitor_setup.iss')

if ($LASTEXITCODE -ne 0) { throw "Inno Setup のコンパイルに失敗しました (終了コード $LASTEXITCODE)。" }

$output = Get-ChildItem (Join-Path $InstallerDir 'output') -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

# 自動更新はこのサイドカーと照合してからインストーラーを起動する。
# setup.exe と必ず一緒に GitHub Release へ添付すること。
$outputHash = (Get-FileHash $output.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$($output.FullName).sha256"
Set-Content -LiteralPath $checksumPath -Value "$outputHash  $($output.Name)" -Encoding ascii

Write-Host ''
Write-Host '完了しました。' -ForegroundColor Green
Write-Host ("  {0}  ({1:N1} MB)" -f $output.FullName, ($output.Length / 1MB))
Write-Host ("  {0}" -f $checksumPath)
