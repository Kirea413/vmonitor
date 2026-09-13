import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';

/// Flutter本体と利用パッケージが登録したライセンスを一覧表示する。
///
/// LicenseRegistryを直接複製せず標準画面を使うことで、依存パッケージを
/// 追加したときも、そのパッケージが提供する著作権表示が自動的に増える。
void showVMonitorLicenses(BuildContext context) {
  final t = L.of(context);

  showLicensePage(
    context: context,
    applicationName: 'vmonitor',
    applicationVersion: '1.2.4-beta',
    applicationLegalese: t.licensesThanks,
  );
}
