import 'package:flutter/services.dart';

/// 映像を出している間、画面を消させないための入口。
///
/// 2 枚目のモニターとして使っている最中に、スマホ側の都合で画面が
/// 消えるとモニターとして成立しない。触っていなくても消えないようにする。
///
/// 端末のウィンドウフラグで行うので、電池を食い続ける類のものではない。
/// アプリが背面に回れば自動で効かなくなる。
class ScreenAwake {
  static const MethodChannel _channel = MethodChannel('vmonitor/screen');

  /// 画面を点けたままにするかどうかを切り替える。
  static Future<void> set(bool keep) async {
    try {
      await _channel.invokeMethod<void>('keepAwake', keep);
    } on PlatformException {
      // 効かなくても映像そのものには影響しない
    } on MissingPluginException {
      // Android 以外ではまだ用意していない
    }
  }
}
