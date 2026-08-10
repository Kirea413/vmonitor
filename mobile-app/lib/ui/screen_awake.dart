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
    await _invoke('keepAwake', keep);
  }

  /// 画面の明るさを固定する。null を渡すと端末の設定に戻す。
  ///
  /// 画面を消させない設定だけでは暗くなるのを防げない。触らない時間が
  /// 続くと、端末が自動調整や減光で勝手に暗くしてしまう。
  /// モニターとして置いて眺めているだけのときに、ちょうどこれが起きる。
  ///
  /// 効くのはこのアプリが前面にある間だけで、端末全体の設定は変えない。
  static Future<void> setBrightness(double? level) async {
    await _invoke('setBrightness', level);
  }

  static Future<void> _invoke(String method, Object? arguments) async {
    try {
      await _channel.invokeMethod<void>(method, arguments);
    } on PlatformException {
      // 効かなくても映像そのものには影響しない
    } on MissingPluginException {
      // Android 以外ではまだ用意していない
    }
  }
}
