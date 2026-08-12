import 'package:flutter/material.dart';

import 'l10n/app_localizations.dart';
import 'ui/app_shell.dart';
import 'ui/display_preferences.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();

  // 保存済みの表示設定を先に読み込む。
  // 読み込みを待たずに起動して、あとから反映させる。
  // 設定が無くても既定値で動くので、ここで待つ意味がない。
  displayPreferences.load();

  runApp(const VMonitorApp());
}

class VMonitorApp extends StatelessWidget {
  const VMonitorApp({super.key});

  @override
  Widget build(BuildContext context) {
    // 言語の選択を変えたら、その場で描き直す。
    return ListenableBuilder(
      listenable: displayPreferences,
      builder: (context, _) {
        return MaterialApp(
          onGenerateTitle: (context) => L.of(context).appTitle,

          // 端末の設定に従う（locale が null）。設定で選ばれていれば
          // そちらを優先する。対応していない言語のときは英語に落ちる。
          locale: displayPreferences.locale,
          localizationsDelegates: L.localizationsDelegates,
          supportedLocales: L.supportedLocales,

          theme: ThemeData(
            colorScheme: ColorScheme.fromSeed(seedColor: Colors.blue),
            useMaterial3: true,
          ),
          home: const AppShell(),
        );
      },
    );
  }
}
