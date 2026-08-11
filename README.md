# vmonitor

**スマートフォンを Windows PC の 2 枚目のモニターにする。**

余っているスマホやタブレットを、ケーブル 1 本、あるいは Wi-Fi だけで
拡張ディスプレイとして使えるようにします。ミラーリングではなく、
Windows から見て本当にディスプレイが 1 枚増えます。
画面に触れば、その操作が PC 側へ Windows Ink として届きます。

[![release](https://img.shields.io/github/v/release/Kirea413/vmonitor?include_prereleases&label=release)](https://github.com/Kirea413/vmonitor/releases)

> **beta です。** 動作を確認した環境は下の「検証状況」のとおりで、
> それほど広くありません。

---

## できること

- **本物の拡張ディスプレイ** — IddCx 仮想ディスプレイドライバで、Windows に
  ディスプレイをもう 1 枚認識させます。ウィンドウを持っていけますし、
  「ディスプレイ設定」にも並びます
- **USB 直結** — Android Open Accessory で、ケーブル 1 本で繋がります。
  Wi-Fi の混雑や電波状況に左右されません
- **Wi-Fi 接続** — 同じネットワークにいれば無線でも。mDNS で自動的に見つけます
- **タッチが効く** — スマホ画面へのタッチが Windows Ink として PC に注入されます。
  ペンにも対応しています
- **拡大率の変更** — Windows の表示スケールとして反映します。
  解像度を下げてぼかす方式ではありません
- **切断ジェスチャー** — 画面いっぱいが入力面なのでボタンは置けません。
  3 本指で払う、4 本指で触れる、など 5 通りから選べます
- **スリープ・減光の抑止** — モニターとして置いている間、消えたり暗くなったりしません

## 必要なもの

| | 要件 |
|---|---|
| PC | Windows 10 バージョン 1809 (build 17763) 以上、または Windows 11 |
| スマホ | Android 7.0 (API 24) 以上 / iOS 13.0 以上 |
| 接続 | USB ケーブル（Android のみ）、または PC と同じ Wi-Fi |

下限の根拠と、iOS の機能対応状況は [SETUP.md](SETUP.md) にあります。

## 入れかた

[Releases](https://github.com/Kirea413/vmonitor/releases) から取得してください。

1. **PC** … `vmonitor-*-setup.exe` を**管理者として実行**
2. **Android** … `vmonitor-*.apk` を端末で開く
3. **iPhone / iPad** … `vmonitor-*-unsigned.ipa` は無署名です。
   自分の証明書を付ける必要があります → [手順](docs/ios-sideload.md)

> **⚠ ドライバの証明書について**
>
> 仮想ディスプレイドライバは自己署名の証明書で署名しており、インストーラーは
> それを「信頼されたルート証明機関」に取り込みます。**その証明書で署名された
> 任意のソフトウェアを、その PC が信頼するようになります。** 納得できない場合は
> インストールしないでください。アンインストール時に取り除かれます。

## 使いかた

1. PC で vmonitor を起動する
2. スマホを USB で繋ぐ（または、スマホ側で Wi-Fi 待ち受けを開始する）
3. PC の一覧から端末を選び、右側の「接続する」を押す
4. スマホに確認が出るので許可する

切断はスマホのジェスチャー（既定は 3 本指で下に払う）か、PC 側の切断ボタンから。

## 検証状況

作れることと動くことは別なので、確かめた範囲を書いておきます。

| | 状態 |
|---|---|
| Windows 11 (build 26200) | ✅ 動作確認済み |
| Windows 10 | ❌ 未確認（API の要件上は 1809 から動くはず） |
| Android 実機 | ✅ **2 台**で動作確認済み（作者の端末と、友人の端末） |
| iOS 実機 | ✅ **iPhone 17 で動作確認済み**（映像・タッチ・画面回転） |
| 自動テスト | .NET 172 件、Flutter 83 件 |

## 仕組み

```
┌─ Windows ─────────────────────────────┐        ┌─ スマホ ────────┐
│                                       │        │                 │
│  IddCx 仮想ディスプレイドライバ        │        │                 │
│         │ 描画内容                    │        │                 │
│         ▼                             │        │                 │
│  画面キャプチャ (DXGI)                │        │                 │
│         │                             │        │                 │
│         ▼                             │  USB   │                 │
│  H.264 エンコード (Media Foundation)  │ ─────► │  ハードウェア   │
│                                       │  または│  デコードして   │
│                                       │  Wi-Fi │  全画面に表示   │
│                                       │        │                 │
│  Windows Ink 注入  ◄───────────────── │ ◄───── │  タッチ         │
└───────────────────────────────────────┘        └─────────────────┘
```

| フォルダ | 中身 |
|---|---|
| `driver/` | IddCx 仮想ディスプレイドライバ (C++ / UMDF2) |
| `pc-client/` | PC アプリ (C# / WPF / .NET 8) とネイティブエンコーダー (C++) |
| `mobile-app/` | スマホアプリ (Flutter / Kotlin / Swift) |
| `installer/` | Inno Setup のインストーラー |

ドライバはユーザーモード (UMDF) で動くため、**テスト署名モードやセキュアブートの
無効化は要りません**。

## ビルド

手順は [SETUP.md](SETUP.md) にまとめてあります。まとめて作るなら:

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1
```

ネイティブエンコーダー、ドライバの署名、PC アプリの発行、インストーラーの
コンパイルまで一度に行います。Visual Studio 2022、WDK、Inno Setup 6 が要ります。

iOS の無署名 IPA は Mac が無くても作れます。GitHub Actions の macOS ランナーを
使う手動ワークフローを用意してあります
（[.github/workflows/ios-unsigned-ipa.yml](.github/workflows/ios-unsigned-ipa.yml)）。

## 分かっている制限

- 検証した環境が上の表のとおり限られています
- iOS で確認できているのは iPhone 17 の 1 台だけです。
  USB 直結は iOS では原理的にできません（Wi-Fi のみ）
- 拡大率は端末によっては指定どおりにならず、Windows が近い段階へ丸めます

## 不具合の報告

[Issues](https://github.com/Kirea413/vmonitor/issues) へお願いします。
次を書いてもらえると助かります。

- Windows のビルド番号（`winver`）と、スマホの OS バージョン
- USB か Wi-Fi か
- `C:\ProgramData\vmonitor-driver.log`（ドライバ側のログ）
