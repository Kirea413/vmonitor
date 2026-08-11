; vmonitor Inno Setup スクリプト
; Inno Setup 6.x 対応
;
; ビルド方法:
;   powershell -ExecutionPolicy Bypass -File installer\build.ps1
;
; ISCC を直接叩かないこと。payload はビルドスクリプトが作る。
; 手で集めていた頃は中身がソースより古くなり、
; 参照先のファイルが消えてコンパイルすら通らなくなっていた。

#define AppName "vmonitor"
; pc-client\VMonitor.UI\VMonitor.UI.csproj の <Version> と必ず揃えること。
; アプリはこの値と GitHub Releases のタグを比べて更新を判断する。
#define AppVersion "1.1.0"
; 画面やインストーラーに出す表記。GitHub のタグは v1.0.0-beta。
#define AppVersionLabel "1.1.0-beta"
#define AppPublisher "vmonitor Project"
#define AppURL "https://github.com/Kirea413/vmonitor"
#define AppExeName "VMonitor.UI.exe"
#define AppGUID "{F41484CA-0A01-4733-A9A7-C5A730D3A5CE}"

; パス定義（このスクリプトファイルからの相対パス）
; build.ps1 が payload を組み立てる
#define AppSrcDir "payload\app"
#define DriverSrcDir "payload\driver"

[Setup]
AppId={{F41484CA-0A01-4733-A9A7-C5A730D3A5CE}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}

; インストール先（デフォルト: C:\Program Files\vmonitor）
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; 出力設定
OutputDir=output
OutputBaseFilename=vmonitor-{#AppVersionLabel}-setup
Compression=lzma2/ultra64
SolidCompression=yes

; 管理者権限を要求する（ドライバインストールに必要）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=

; Windows 10 以降を要求する
MinVersion=10.0.19041

; アーキテクチャ
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; アンインストーラー設定
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; ウィザードの見た目
WizardStyle=modern
WizardSmallImageFile=
; tools\generate-icons.ps1 が作る。インストーラー本体と
; 「プログラムの追加と削除」に出るアイコンになる。
SetupIconFile=..\tools\icons\vmonitor.ico

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加タスク:"; Flags: unchecked

[Files]
; ── アプリケーション本体 ────────────────────────────────────────────
Source: "{#AppSrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ── 署名済みドライバファイル ─────────────────────────────────────────
Source: "{#DriverSrcDir}\VMonitorVDD.dll";  DestDir: "{app}\driver"; Flags: ignoreversion
Source: "{#DriverSrcDir}\VMonitorVDD.inf";  DestDir: "{app}\driver"; Flags: ignoreversion
Source: "{#DriverSrcDir}\vmonitorvdd.cat";  DestDir: "{app}\driver"; Flags: ignoreversion
Source: "{#DriverSrcDir}\MyTestCert.cer";   DestDir: "{app}\driver"; Flags: ignoreversion
; USB 直結 (AOA) 用。Android のアクセサリーインターフェースに WinUSB を割り当てる。
Source: "{#DriverSrcDir}\VMonitorAOA.inf";  DestDir: "{app}\driver"; Flags: ignoreversion
Source: "{#DriverSrcDir}\vmonitoraoa.cat";  DestDir: "{app}\driver"; Flags: ignoreversion

; ドライバの削除は VMonitorSetup.exe /uninstall が行う。
; 以前ここにあった uninstall_driver.ps1 は、デバイスノードも証明書も
; 消せておらず、二重管理になっていたため使うのをやめた。

[Icons]
; スタートメニュー
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} をアンインストール"; Filename: "{uninstallexe}"

; デスクトップ（タスク選択時のみ）
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; ── インストール後に実行 ─────────────────────────────────────────────

; ドライバの導入は VMonitorSetup.exe に任せる。
;
; 以前はここで certutil と pnputil を直接並べていたが、それでは
; 仮想ディスプレイは現れない。対応する物理ハードウェアが無いため、
; ルート列挙デバイス (Root\VMonitorVDD) を明示的に作る必要があり、
; pnputil /add-driver /install はそこまでやってくれない。
;
; VMonitorSetup.exe はそれに加えて、古いドライバパッケージの掃除、
; USB 直結 (AOA) 用 INF の導入、失敗時の切り分け情報の出力も行う。
;
; テスト署名モード (bcdedit /set testsigning on) は必要ない。
; VMonitorVDD は UMDF（ユーザーモード）ドライバで WUDFHost.exe 上で動き、
; カーネルの署名強制の対象外になる。必要なのは署名者を信頼させることだけ。
; 起動構成を書き換えずに済むなら、書き換えない方がよい。
; 失敗を握りつぶさない。
;
; 以前はここを [Run] の runhidden で呼びっぱなしにしていた。
; 途中で落ちても画面には何も出ず、終了コードも見ていなかったため、
; 「インストールは成功したのに仮想ディスプレイが出ない」という
; 原因の分からない状態になっていた。
; いまは [Code] の CurStepChanged から呼び、失敗したらその場で伝える。

; インストール完了後にアプリを起動する（任意）
Filename: "{app}\{#AppExeName}"; \
  Description: "vmonitor を起動する"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; ── アンインストール時に実行 ─────────────────────────────────────────

; 1. vmonitor プロセスを終了する
;
;    USB を掴んだまま落とすと、カーネル側の I/O が終わらずに
;    抜け殻のプロセスが残り、端末を握ったままになる。
;    まず行儀よく閉じさせ、それでも残るときだけ強制終了する。
; RunOnceId を付けないと、アンインストールのやり直しのたびに実行される。
Filename: "taskkill.exe"; RunOnceId: "StopApp"; \
  Parameters: "/im VMonitor.UI.exe"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

Filename: "taskkill.exe"; RunOnceId: "StopAppForce"; \
  Parameters: "/f /im VMonitor.UI.exe"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

; 2. ドライバとデバイスノード、取り込んだ証明書を取り除く。
;
;    入れたものは戻す。信頼されたルートに証明書を残すと、
;    その鍵を持つ相手の署名をこの PC が信じ続けることになる。
Filename: "{app}\VMonitorSetup.exe"; RunOnceId: "RemoveDriver"; \
  Parameters: "/uninstall /silent"; \
  Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
// ── ドライバの導入 ───────────────────────────────────────────────────
//
// ファイルのコピーが終わったところで実行し、結果を確かめる。
// ここが失敗したまま完了扱いにすると、拡張ディスプレイも USB 直結も
// 使えないのに「インストールできた」ように見えてしまう。
procedure InstallDrivers();
var
  resultCode: Integer;
  setupExe:   String;
begin
  setupExe := ExpandConstant('{app}\VMonitorSetup.exe');

  if not FileExists(setupExe) then
  begin
    MsgBox('ドライバ導入用のプログラムが見つかりません:' + #13#10 + setupExe,
           mbError, MB_OK);
    exit;
  end;

  if not Exec(setupExe, '/driver-only', '', SW_HIDE,
              ewWaitUntilTerminated, resultCode) then
  begin
    MsgBox('ドライバの導入を開始できませんでした。' + #13#10 +
           'インストール後に次を管理者として実行してください:' + #13#10 +
           '"' + setupExe + '" /driver-only',
           mbError, MB_OK);
    exit;
  end;

  if resultCode <> 0 then
  begin
    MsgBox('ドライバの導入に失敗しました（終了コード ' + IntToStr(resultCode) + '）。' + #13#10 + #13#10 +
           'アプリ自体は使えますが、スマホを 2 枚目のディスプレイにする' + #13#10 +
           '拡張表示と、USB 直結は利用できません。' + #13#10 + #13#10 +
           '詳しい理由は次に記録されています:' + #13#10 +
           ExpandConstant('{commonappdata}\vmonitor\setup-error.log') + #13#10 + #13#10 +
           '入れ直すには、管理者として次を実行してください:' + #13#10 +
           '"' + setupExe + '" /driver-only',
           mbError, MB_OK);
  end;
end;

procedure CurStepChanged(currentStep: TSetupStep);
begin
  if currentStep = ssPostInstall then
    InstallDrivers();
end;

// インストール前チェック: Windows 10 以降かどうか確認
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('vmonitor は 64 ビット版 Windows 10 以降が必要です。', mbError, MB_OK);
    Result := False;
  end;
end;

// アンインストール確認ダイアログ
function InitializeUninstall(): Boolean;
begin
  Result := MsgBox(
    'vmonitor をアンインストールしますか？' + #13#10 +
    '仮想ディスプレイドライバも自動的に削除されます。',
    mbConfirmation, MB_YESNO) = IDYES;
end;
