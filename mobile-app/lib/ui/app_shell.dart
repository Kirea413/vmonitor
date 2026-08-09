import 'package:flutter/material.dart';

import 'device_discovery_screen.dart';
import 'video_display_screen.dart';

class AppShell extends StatelessWidget {
  const AppShell({super.key});

  @override
  Widget build(BuildContext context) {
    return DeviceDiscoveryScreen(
      // 映像画面が閉じるまで待ってから返す。
      // 探索画面はこれを合図に待ち受けへ戻る。返さずに投げっぱなしにすると、
      // 戻ってきたときに「接続しました」の表示のまま固まる。
      onConnected: (transport, device) {
        return Navigator.of(context).push(
          MaterialPageRoute<void>(
            builder: (_) => VideoDisplayScreen(
              device: device,
              transport: transport,
            ),
          ),
        );
      },
    );
  }
}
