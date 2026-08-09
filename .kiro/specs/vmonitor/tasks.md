# Implementation Plan: vmonitor

## Overview

vmonitor の実装を、インフラ基盤の構築から始め、コアコンポーネント（仮想ディスプレイドライバ、ストリーマー、レンダラー、タッチ入力、接続層）を段階的に組み上げ、最後に UI・設定・セキュリティを統合する。各フェーズでプロパティテストとユニットテストを並走させ、品質を早期に検証する。

---

## Tasks

- [x] 1. プロジェクト構成とコアインターフェースの定義
  - [x] 1.1 PC クライアントの C# ソリューション・プロジェクト構成を作成する
    - `VMonitor.Core`（共通インターフェース・データモデル）、`VMonitor.Driver`、`VMonitor.Streamer`、`VMonitor.Session`、`VMonitor.UI` の各プロジェクトを作成する
    - xUnit・FsCheck・Moq の NuGet 参照を追加する
    - _Requirements: 1.1, 1.2_
  - [x] 1.2 スマホアプリの Flutter プロジェクト構成を作成する
    - `lib/renderer`、`lib/touch`、`lib/transport`、`lib/ui` のディレクトリを作成する
    - `flutter_test`・`fast_check` の依存関係を `pubspec.yaml` に追加する
    - _Requirements: 1.4_
  - [x] 1.3 コア共通インターフェースとデータモデルを `VMonitor.Core` に定義する
    - `IVirtualDisplayDriver`・`IStreamer`・`ISessionManager`・`IAuthManager`・`ITransport`・`IDisplaySettingsManager`・`IWindowsInkInjector` インターフェースを作成する
    - `Session`・`DeviceInfo`・`TrustedDevice`・`DisplaySpec`・`Resolution`・`StreamingSettings`・`TouchEvent`・`TouchPoint` レコード/クラスを定義する
    - _Requirements: 2.5, 3.1, 4.1, 6.1_

- [x] 2. 仮想ディスプレイドライバ (VDD) とインストーラー
  - [x] 2.1 IddCx UMDF2 ドライバプロジェクトを作成し、仮想モニターとして OS に登録する
    - IddCx を使った UMDF2 ドライバの骨格（`IddCxAdapterInitAsync`・`IddCxMonitorCreate`）を実装する
    - `IVirtualDisplayDriver.CreateDisplayAsync` / `RemoveDisplayAsync` を実装する
    - C++ ドライバプロジェクト (`driver/VMonitorVDD/`) 作成済み・ビルド進行中
    - INF ファイルの InfVerif エラー修正中（`wdf.h` インクルードパス調整）
    - _Requirements: 3.1, 3.2, 3.5_
  - [x] 2.2 解像度・向き更新を `UpdateResolutionAsync` として実装する
    - `IddCxMonitorUpdateModes` を呼び出してディスプレイモードリストを更新する
    - Portrait / Landscape / PortraitFlipped / LandscapeFlipped すべての向きに対応する
    - _Requirements: 5.1, 5.2_
  - [x] 2.3 Property 3: ディスプレイ追加・削除ラウンドトリップのプロパティテストを書く
    - **Property 3: ディスプレイ追加・削除のラウンドトリップ**
    - **Validates: Requirements 3.1, 3.5**
  - [x] 2.4 Property 10: 向き変更による解像度同期のプロパティテストを書く
    - **Property 10: 向き変更による解像度同期**
    - **Validates: Requirements 5.1, 5.2**
  - [x] 2.5 `InstallAsync` / `UninstallAsync` を実装し、`pnputil /add-driver` でドライバを自動インストールする
    - インストーラー（WiX / NSIS）に署名済みドライバをバンドルする
    - インストール失敗時のエラーメッセージ生成ロジックを実装する
    - _Requirements: 1.1, 1.5_
  - [x] 2.6 Property 1: エラーメッセージの表示・非表示のプロパティテストを書く
    - **Property 1: エラーメッセージの表示・非表示の正確さ**
    - **Validates: Requirements 1.5**
  - [x] 2.7 解像度フォールバックロジックを実装する
    - サポート範囲外の解像度入力に対し最近傍サポート解像度へフォールバックする
    - フォールバック発生時にユーザー通知を出す
    - _Requirements: 5.5_
  - [x] 2.8 Property 12: 手動解像度指定優先のプロパティテストを書く
    - **Property 12: 手動解像度指定の優先**
    - **Validates: Requirements 5.4**
  - [x] 2.9 Property 13: 解像度フォールバック最近傍保証のプロパティテストを書く
    - **Property 13: 解像度フォールバックの最近傍保証**
    - **Validates: Requirements 5.5**

- [x] 3. チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

- [x] 4. 接続レイヤー（Wi-Fi / USB）
  - [x] 4.1 `ITransport` の Wi-Fi 実装（mDNS 探索 + TCP/TLS）を作成する
    - PC クライアント側: `_vmonitor._tcp` mDNS サービスを起動時に登録する
    - スマホ側: `_vmonitor._tcp` を検索し、接続候補リストに表示する
    - TLS ハンドシェイクとチャンネル多重化（映像・タッチ・制御）を実装する
    - _Requirements: 2.1, 1.2_
  - [x] 4.2 Property 2: デバイス探索ラウンドトリップのプロパティテストを書く
    - **Property 2: デバイス探索のラウンドトリップ**
    - **Validates: Requirements 2.1**
  - [x] 4.3 `ITransport` の USB 実装（Android: ADB フォワード、iOS: libimobiledevice）を作成する
    - PC クライアントで USB デバイス接続イベントを監視し、トンネルを自動確立する
    - _Requirements: 2.2_
  - [x] 4.4 Wi-Fi・USB 接続のユニットテストを書く
    - USB 接続イベントでセッション確立が試みられることを検証する
    - _Requirements: 2.2_

- [x] 5. セッション管理と認証
  - [x] 5.1 `ISessionManager` を実装する（セッション確立・終了・再接続）
    - 10 秒以内のセッション確立タイムアウト処理を実装する
    - 指数バックオフ（初回 1s → 最大 5s、30 秒間）の再接続ロジックを実装する
    - `SessionState` 遷移（Connecting → Active → Reconnecting → Terminated）を実装する
    - _Requirements: 2.3, 2.4, 2.6, 9.1, 9.2_
  - [x] 5.2 Property 22: 再接続試行継続性のプロパティテストを書く
    - **Property 22: 再接続試行の継続性**
    - **Validates: Requirements 9.1**
  - [x] 5.3 `IAuthManager` を実装する（デバイス認証・信頼済みデバイス管理）
    - 初回接続時の許可確認ダイアログ表示ロジックを実装する
    - デバイス識別子の信頼済みリストへの追加・削除・照会を実装する
    - _Requirements: 8.1, 8.2, 8.3, 8.5_
  - [x] 5.4 Property 20: デバイス信頼管理ライフサイクルのプロパティテストを書く
    - **Property 20: デバイス信頼管理のライフサイクル**
    - **Validates: Requirements 8.2, 8.3, 8.5**
  - [x] 5.5 セッション確立フローを VDD と接続し、仮想ディスプレイの自動作成・削除を実装する
    - セッション確立時に `CreateDisplayAsync` を呼び出す
    - セッション終了・タイムアウト時に `RemoveDisplayAsync` を呼び出す
    - _Requirements: 2.5, 3.1, 3.5_
  - [x] 5.6 セッション・認証のユニットテストを書く
    - タイムアウト後に通知と再試行 UI が表示されること（2.4）を検証する
    - 30 秒タイムアウト後にセッションが Terminated 状態になること（9.2）を検証する
    - 未知デバイスからの接続で許可ダイアログが表示されること（8.1）を検証する
    - _Requirements: 2.4, 8.1, 9.2_

- [x] 6. チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

- [x] 7. 映像ストリーマー（PC クライアント側）
  - [x] 7.1 `IStreamer` を Windows Media Foundation MFT (GPU ハードウェアエンコード) で実装する
    - H.264 (baseline) エンコードをデフォルトとし、端末対応時は H.265/HEVC へ切り替える
    - `StartAsync` / `StopAsync` とフレームキャプチャループを実装する
    - _Requirements: 4.1, 4.3, 4.4_
  - [x] 7.2 Property 6: エンコード処理時間上限のプロパティテストを書く
    - **Property 6: エンコード処理時間の上限**
    - **Validates: Requirements 4.3**
  - [x] 7.3 Property 7: フレームレート下限保証のプロパティテストを書く
    - **Property 7: フレームレートの下限保証**
    - **Validates: Requirements 4.4**
  - [x] 7.4 帯域適応ビットレート制御 (`OnBandwidthEstimate`) を実装する
    - RTCP / RTT 計測で帯域を推定し、ビットレート・解像度を段階的に下げる
    - 最低品質でも 30fps を維持する
    - _Requirements: 4.5_
  - [x] 7.5 Property 8: 帯域低下時ビットレート適応のプロパティテストを書く
    - **Property 8: 帯域低下時のビットレート適応**
    - **Validates: Requirements 4.5**
  - [x] 7.6 ビットレート設定変更の即時反映を実装する (`Config` setter)
    - _Requirements: 4.6_
  - [x] 7.7 Property 9: ビットレート設定変更即時反映のプロパティテストを書く
    - **Property 9: ビットレート設定変更の即時反映**
    - **Validates: Requirements 4.6**

- [x] 8. 映像レンダラー（スマホアプリ側）
  - [x] 8.1 Flutter + プラットフォームチャンネルで `Renderer` を実装する
    - iOS: VideoToolbox ハードウェアデコーダー
    - Android: MediaCodec ハードウェアデコーダー
    - デコード済みフレームを `Texture` ウィジェットで全画面 GPU 表示する
    - _Requirements: 4.2, 5.3_
  - [x] 8.2 Property 5: 映像エンコード・デコードラウンドトリップのプロパティテストを書く
    - **Property 5: 映像エンコード・デコードのラウンドトリップ**
    - **Validates: Requirements 4.1, 4.2**
  - [x] 8.3 Property 11: レンダラー全画面表示（レターボックスなし）のプロパティテストを書く
    - **Property 11: レンダラーの全画面表示（レターボックスなし）**
    - **Validates: Requirements 5.3**

- [x] 9. チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

- [x] 10. タッチ入力プロキシとWindows Inkインジェクター
  - [x] 10.1 Flutter 側の `TouchInputProxy` を実装する
    - `GestureDetector` / `Listener` でシングル・マルチタッチイベントを捕捉する
    - タッチポイントを正規化座標 [0.0, 1.0] にシリアライズして `ITransport` で送信する
    - _Requirements: 6.1, 6.4_
  - [x] 10.2 Property 14: タッチイベント完全転送のプロパティテストを書く
    - **Property 14: タッチイベントの完全転送**
    - **Validates: Requirements 6.1**
  - [x] 10.3 Property 16: マルチタッチ同時転送のプロパティテストを書く
    - **Property 16: マルチタッチの同時転送**
    - **Validates: Requirements 6.4**
  - [x] 10.4 PC クライアント側の `IWindowsInkInjector` を実装する
    - Windows Ink API (`InjectTouchInput`) でタッチポイントを注入する
    - `UpdateTransform` で向き変更時の座標変換行列を更新する
    - _Requirements: 6.2, 6.3, 6.6_
  - [x] 10.5 Property 15: タッチイベント Windows Ink 注入のプロパティテストを書く
    - **Property 15: タッチイベントの Windows Ink 注入**
    - **Validates: Requirements 6.2**
  - [x] 10.6 Property 18: 向き変更後タッチ座標変換正確さのプロパティテストを書く
    - **Property 18: 向き変更後のタッチ座標変換の正確さ**
    - **Validates: Requirements 6.6**
  - [x] 10.7 タッチ入力プロキシとインジェクターをセッション・VDD と結線する
    - 向き変更イベントを受けて `UpdateTransform` を呼び出すイベントハンドラーを登録する
    - _Requirements: 6.6_
  - [x] 10.8 Property 17: タッチ入力処理時間上限のプロパティテストを書く
    - **Property 17: タッチ入力処理時間の上限**
    - **Validates: Requirements 6.5**

- [x] 11. ディスプレイ設定マネージャー
  - [x] 11.1 `IDisplaySettingsManager` を `SetDisplayConfig` / `ChangeDisplaySettingsEx` ラッパーで実装する
    - Clone / Extend / SecondaryOnly の各 `DisplayMode` を実装する
    - 設定変更後に `QueryDisplayConfig` でポーリングし 3 秒以内の適用完了を保証する
    - _Requirements: 3.3, 3.4, 7.3_
  - [x] 11.2 Property 4: DisplayMode 設定即時反映のプロパティテストを書く
    - **Property 4: DisplayMode 設定の即時反映**
    - **Validates: Requirements 3.3, 3.4, 7.3**
  - [x] 11.3 ディスプレイ設定マネージャーをセッション管理・VDD と結線する
    - セッション確立時にデフォルト DisplayMode を適用する
    - _Requirements: 3.3, 3.4_

- [x] 12. チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

- [x] 13. 設定永続化とエラーログ
  - [x] 13.1 `%APPDATA%\vmonitor\settings.json` への設定読み書きを実装する
    - `StreamingSettings`・`DisplaySettings`・`trustedDevices` の保存・読み込みを実装する
    - 設定ファイル破損時のデフォルト値フォールバックを実装する
    - _Requirements: 7.5_
  - [x] 13.2 Property 19: 設定永続化ラウンドトリップのプロパティテストを書く
    - **Property 19: 設定永続化のラウンドトリップ**
    - **Validates: Requirements 7.5**
  - [x] 13.3 構造化 JSON エラーロガーを実装する
    - `%APPDATA%\vmonitor\logs\vmonitor.log` への DEBUG / INFO / WARN / ERROR ログ記録を実装する
    - 10MB 超での自動ローテーション（最大 5 世代）を実装する
    - _Requirements: 9.4_
  - [x] 13.4 Property 23: エラーログ記録ラウンドトリップのプロパティテストを書く
    - **Property 23: エラーログの記録**
    - **Validates: Requirements 9.4**

- [x] 14. エラー処理と回復
  - [x] 14.1 ドライバ障害回復ロジックを実装する
    - WMI イベント監視でドライバ停止を検出する
    - `pnputil /restart-device` で最大 3 回（5 秒間隔）再起動を試みる
    - 再起動失敗時にユーザー通知を表示する
    - _Requirements: 9.3_
  - [x] 14.2 ドライバ障害回復のユニットテストを書く
    - ドライバ停止イベントで再起動試行が行われることを検証する
    - _Requirements: 9.3_
  - [x] 14.3 暗号化トランスポートを実装する（TLS + ペイロード暗号化）
    - 映像ストリームとタッチ入力データを暗号化して送受信する
    - _Requirements: 8.4_
  - [x] 14.4 Property 21: ペイロード暗号化のプロパティテストを書く
    - **Property 21: ペイロードの暗号化**
    - **Validates: Requirements 8.4**

- [x] 15. チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

- [x] 16. PC クライアント UI (WPF/WinUI)
  - [x] 16.1 メインウィンドウと接続管理画面を作成する
    - 接続候補リスト・接続状態の表示
    - 切断・再接続通知 UI を実装する
    - _Requirements: 2.4, 2.6_
  - [x] 16.2 ディスプレイ設定画面を作成する
    - 複製・拡張・セカンダリのみの DisplayMode 切り替え UI
    - 解像度プリセット一覧と手動入力フォームを実装する
    - _Requirements: 7.1, 7.2_
  - [x] 16.3 信頼済みデバイス管理画面を作成する
    - デバイス一覧表示と削除操作 UI を実装する
    - 初回接続時の許可確認ダイアログを実装する
    - _Requirements: 8.1, 8.5_
  - [x] 16.4 エラーログ確認・設定画面を作成する
    - ログファイルパスの表示とリンク、ビットレート設定の保存 UI を実装する
    - _Requirements: 9.5, 7.5_

- [x] 17. スマホアプリ UI (Flutter)
  - [x] 17.1 デバイス探索・接続画面を作成する
    - mDNS で検出した PC 候補リストを表示し、接続ボタンを実装する
    - タイムアウト通知と再試行オプションを実装する
    - _Requirements: 2.1, 2.3, 2.4_
  - [x] 17.2 全画面映像表示画面（レンダラービュー）を作成する
    - `Texture` ウィジェットによる全画面描画を実装する
    - 向き変更に応じた表示切り替えを実装する
    - _Requirements: 4.2, 5.3_
  - [x] 17.3 スマホアプリのフレームレート・ビットレート設定画面を作成する
    - 設定変更を PC クライアントへ即時反映する
    - _Requirements: 7.4_

- [x] 18. インストーラースモークテストの統合
  - [x] 18.1 インストーラー完了後に自動実行されるスモークテストスクリプトを作成する
    - 仮想ディスプレイドライバが DriverStore に存在することを確認（1.1）
    - 必要なネットワークサービスが Running 状態であることを確認（1.2）
    - `pnputil /enum-drivers` でドライバ一覧を検証する
    - _Requirements: 1.1, 1.2, 1.3_

- [x] 19. 最終チェックポイント
  - 全テストが通ることを確認し、疑問点があればユーザーに質問してください。

---

## Notes

- `*` 付きのサブタスクはオプションであり、MVP を優先する場合はスキップ可能
- 各タスクは特定の要件番号を参照しトレーサビリティを確保している
- プロパティテストは FsCheck (C#) / fast_check (Dart) を使用し、最低 100 回のランダム入力で検証する
- ユニットテストは xUnit (C#) / flutter_test (Dart) を使用する
- チェックポイントで段階的な品質検証を行う

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2"] },
    { "id": 1, "tasks": ["1.3"] },
    { "id": 2, "tasks": ["2.1", "4.1", "5.1"] },
    { "id": 3, "tasks": ["2.2", "2.5", "4.3", "5.3", "7.1"] },
    { "id": 4, "tasks": ["2.3", "2.6", "2.7", "4.2", "4.4", "5.2", "5.5", "7.2", "7.3", "8.1"] },
    { "id": 5, "tasks": ["2.8", "2.9", "5.4", "5.6", "7.4", "7.6", "8.2", "8.3", "10.1", "11.1"] },
    { "id": 6, "tasks": ["7.5", "7.7", "10.2", "10.3", "10.4", "11.2", "13.1"] },
    { "id": 7, "tasks": ["10.5", "10.6", "10.7", "11.3", "13.2", "13.3", "14.3"] },
    { "id": 8, "tasks": ["10.8", "13.4", "14.1", "14.4"] },
    { "id": 9, "tasks": ["14.2", "16.1", "17.1"] },
    { "id": 10, "tasks": ["16.2", "16.3", "17.2"] },
    { "id": 11, "tasks": ["16.4", "17.3"] },
    { "id": 12, "tasks": ["18.1"] }
  ]
}
```
