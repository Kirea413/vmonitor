# 同梱している第三者のソフトウェア

vmonitor のインストーラーには、次のものが含まれています。

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
