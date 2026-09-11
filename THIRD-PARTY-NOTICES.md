# 同梱している第三者のソフトウェア

vmonitor のインストーラーには、次のものが含まれています。

## PC アプリのライブラリ

| ソフトウェア | 用途 | ライセンス | 配布元 |
|---|---|---|---|
| LibUsbDotNet | USB通信 | LGPL-3.0-or-later | https://github.com/LibUsbDotNet/LibUsbDotNet |
| Makaretu.Dns / Multicast | mDNS探索・通知 | MIT | https://github.com/richardschneider/net-mdns |
| Common.Logging | Makaretu.Dnsのログ抽象化 | Apache-2.0 | https://github.com/net-commons/common-logging |
| IPNetwork2 | Makaretu.Dnsのネットワーク計算 | BSD-2-Clause | https://github.com/lduchosal/ipnetwork |
| Vortice.Windows | Direct3D・Media Foundation連携 | MIT | https://github.com/amerkoleci/Vortice.Windows |
| SharpGen.Runtime | Vortice.Windowsのネイティブ連携 | MIT | https://github.com/SharpGenTools/SharpGenTools |
| SimpleBase | Vortice.Windowsの内部データ変換 | Apache-2.0 | https://github.com/ssg/SimpleBase |
| Tmds.LibC | Makaretu.DnsのUnix互換処理 | MIT | https://github.com/tmds/Tmds.LibC |
| Microsoft .NET Runtime / System.Management | アプリ実行・Windows管理情報 | MIT | https://github.com/dotnet/runtime |

各ライブラリのライセンス本文は、上記配布元およびNuGetパッケージに収録されて
います。PCアプリの「ライセンス」タブからも、この文書を確認できます。

## UsbDk

- 配布元: https://github.com/daynix/UsbDk
- 版: 1.0.22 (v1.00-22)
- ライセンス: Apache License 2.0
- 著作権: Copyright (c) 2013-2020 Red Hat, Inc. and/or its affiliates

USB 直結 (AOA) を使えるようにするために同梱しています。

通常モードの Android は MTP ドライバの持ち物になっていることが多く、
その状態では Windows がユーザーモードのアプリに触らせません。AOA の
切り替え指示を送れないため、USB 直結が丸ごと使えなくなります。UsbDk は
既存のドライバを外さずに USB へ到達できるので、MTP を壊さずに済みます。

インストール時の「USB 直結を使えるようにする」を外すと導入されません。
Wi-Fi 接続だけを使う場合は必要ありません。

vmonitor をアンインストールしても UsbDk は残ります。他のソフトが
使っている場合があるためです。不要なら「アプリと機能」から
「UsbDk Runtime Library」を削除してください。

Apache License 2.0 の全文:
https://www.apache.org/licenses/LICENSE-2.0

## libusbmuxd / iproxy

- 配布元: https://github.com/libimobiledevice/libusbmuxd
- Windowsビルド: https://github.com/jrjr/libimobiledevice-windows
- 版: iproxy / libusbmuxd 2.1.1-2-g93eb168
- ライセンス: iproxy は GPL-2.0、libusbmuxd は LGPL-2.1

iPhone / iPad上で待ち受けるvmonitorへ、USB経由でTCP接続を転送するために
使用します。依存する `libimobiledevice-glue` と `libplist` も LGPL-2.1です。
ライセンス本文を含む完全な対応ソースは、インストール先の
`tools\ios-usb\source` に同梱しています。詳細は同フォルダーのREADMEを
参照してください。
