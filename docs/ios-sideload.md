# iPhone / iPad に入れる（無署名 IPA の導入手順）

vmonitor の iOS 版は **無署名の IPA** として配布しています。iOS は署名の
無いアプリを受け付けないため、**受け取った側で自分の証明書を付ける**
必要があります。ここではその手順をまとめます。

> **iPhone 17で映像・タッチ・画面回転を確認済みです。**
> iOSの版や機種による差はまだ十分に検証できていません。

---

## 用意するもの

| | |
|---|---|
| iPhone / iPad | iOS 13.0 以上 |
| PC | Windows（この手順は Windows 向け。Mac なら Xcode でも可） |
| Apple ID | 無料のもので構いません |
| ケーブル | 端末と PC を繋ぐもの |
| IPA | [Releases](https://github.com/Kirea413/vmonitor/releases) の `vmonitor-*-unsigned.ipa` |

無料の Apple ID を使う場合、**7 日で失効**します。切れたら同じ手順で
入れ直してください。有料の Apple Developer Program なら 1 年です。

---

## 手順

### 1. Apple のドライバを入れる

**Microsoft Store 版ではだめです。** Sideloadly の要件にこうあります。

> Sideloadly requires the **web version** of iTunes & iCloud on Windows.
> If you have the Microsoft Store versions, uninstall them first.

Store 版の iTunes や「Apple Devices」アプリが入っている場合は、先に
削除してください。入れるのは Apple のサイトで配っているデスクトップ版です。

winget を使うと早いです。

```bash
winget install Apple.iTunes Apple.iCloud
```

インストール後、`Apple Mobile Device Service` が動いているか確かめてください。
**これが端末認識の要です。**

```bash
powershell -c "Get-Service | Where-Object { $_.DisplayName -match 'Apple Mobile' }"
```

> **入っていない場合**（iTunes を入れたのにサービスが無いことがあります）
>
> iTunes のインストーラーは MSI をまとめた自己展開形式なので、
> 中の `AppleMobileDeviceSupport64.msi` を直接入れられます。
>
> ```bash
> iTunes64Setup.exe /extract C:\temp\itunes-msi
> ```
>
> 取り出した MSI を管理者権限で実行してください。

### 2. Sideloadly を入れる

[sideloadly.io](https://sideloadly.io/) から取得するか、winget で。

```bash
winget install iOSGods.Sideloadly
```

ユーザー領域（`%LOCALAPPDATA%\Sideloadly`）に入るので管理者権限は要りません。
なお **実行ファイルにデジタル署名はありません**。気になる場合は公式サイトから
自分で取得してください。

### 3. 端末を繋いで信頼する

ケーブルで繋ぐと、端末側に「このコンピュータを信頼しますか？」と出ます。
**信頼**を選び、パスコードを入力してください。

Sideloadly の上部に端末名が出れば認識できています。出ない場合は
ケーブルを挿し直すか、別のポートを試してください。

### 4. IPA を渡して署名する

1. Sideloadly のウィンドウへ `vmonitor-*-unsigned.ipa` をドラッグする
2. **Apple ID** の欄に自分の Apple ID を入れる
3. `Start` を押す
4. パスワードと、2 要素認証のコードを入力する

パスワードは Sideloadly が Apple のサーバーへ直接送るもので、
vmonitor 側には渡りません。

### 5. 開発者を信頼する

インストールした直後は、まだ起動できません。端末側で信頼が要ります。

**設定 → 一般 → VPN とデバイス管理 → （自分の Apple ID）→ 信頼**

### 6. ローカルネットワークを許可する

初回起動時に「ローカルネットワーク上のデバイスの検索を許可しますか？」
と聞かれます。**許可**してください。

**ここを拒否すると Wi-Fi 上の PC を見つけられません。** USB接続だけを使う
場合は探索許可なしでも待受できますが、Wi-Fiへの切り替えができなくなります。

間違えて拒否した場合は、**設定 → vmonitor → ローカルネットワーク**
から入れ直せます。

---

## 繋ぎかた

通常は **PC と iPhone を同じ Wi-Fi に繋いでください。** 試験対応のUSB接続を
使う場合は、PCへApple Mobile Device Supportを入れます。配布インストーラーには
必要な `iproxy` と依存DLLが含まれています。

1. PC で vmonitor を起動する
2. iPhone で vmonitor を開く
3. iPhoneの一覧に出たPCを選び、「接続」を押す
4. PC側に出る確認を許可する

USBの場合は、iPhoneでvmonitorを開いたままケーブルを接続して「この
コンピューターを信頼」を選びます。PC側のvmonitorがUSB経路を検出すると、
iPhone側の「USBで接続」ボタンが有効になります。ボタンを押し、PC側に出る
確認を許可してください。PC側の「iPhone USB」から開始することもできます。
USB経路はまだ実機検証前です。

複数台を使う場合は、ほかのiPhone/iPadでも2〜4を繰り返します。現在は先着1台が
仮想拡張画面になり、2台目以降にはPCのメイン画面がミラーされます。

iPhone側の一覧にPCが出てこない場合は、[SETUP.md](../SETUP.md) の
ファイアウォールの項を確認してください。PC側の7979番を使用します。

---

## うまくいかないとき

| 症状 | 見るところ |
|---|---|
| Sideloadly が端末を認識しない | Apple のドライバが入っているか。「信頼」を押したか |
| インストールは通るが起動しない | 手順 5 の「デバイス管理」で信頼したか |
| 起動して数日で開けなくなった | 無料 Apple ID の 7 日制限。入れ直す |
| PC が見つからない | 手順 6 のローカルネットワーク許可。PC と同じ Wi-Fi か。PC 側のファイアウォール |
| 起動直後に落ちる | **未検証のため、こちらの不具合の可能性が高いです。** Issue でお知らせください |

## 報告してもらえると助かること

iOS 版は誰も動かしたことがありません。次のどれかだけでも大きな手がかりに
なります。

- 起動したか、しなかったか
- PC の一覧に出てきたか
- 映像が出たか、タッチが効いたか
- iOS のバージョンと端末名

[Issues](https://github.com/Kirea413/vmonitor/issues) へお願いします。
