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

Write-Host ''
Write-Host '完了しました。' -ForegroundColor Green
Write-Host ("  {0}  ({1:N1} MB)" -f $output.FullName, ($output.Length / 1MB))
