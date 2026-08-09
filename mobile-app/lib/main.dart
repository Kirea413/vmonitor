import 'package:flutter/material.dart';

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
    return MaterialApp(
      title: 'vmonitor',
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: Colors.blue),
        useMaterial3: true,
      ),
      home: const AppShell(),
    );
  }
}
