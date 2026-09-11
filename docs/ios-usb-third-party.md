# iOS USB 転送に同梱するソフトウェア

このフォルダーの実行ファイルとDLLは、iPhone / iPadへのUSBポート転送に使用します。
vmonitor本体とは別プロセスで動きます。

| ファイル | プロジェクト | 版 | ライセンス |
|---|---|---|---|
| `iproxy.exe` | libusbmuxd | 2.1.1-2-g93eb168 | GPL-2.0 |
| `libusbmuxd-2.0.dll` | libusbmuxd | 2.1.1-2-g93eb168 | LGPL-2.1 |
| `libimobiledevice-glue-1.0.dll` | libimobiledevice-glue | 1.3.2-5-gda770a7 | LGPL-2.1 |
| `libplist-2.0.dll` | libplist | 2.7.0-66-g32428ab | LGPL-2.1 |

Windowsビルドの取得元:
https://github.com/jrjr/libimobiledevice-windows/releases/tag/v20260906-74585f8

各プロジェクトの完全な対応ソースとライセンス本文は、このフォルダーの
`source` 以下に `.tar.gz` のまま収録しています。改変は加えていません。

- https://github.com/libimobiledevice/libusbmuxd/tree/93eb168
- https://github.com/libimobiledevice/libimobiledevice-glue/tree/da770a7
- https://github.com/libimobiledevice/libplist/tree/32428ab

これらのプロジェクトはvmonitorプロジェクトとは独立しており、Apple Inc.により
承認または提供されたものではありません。
