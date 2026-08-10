import 'package:flutter/widgets.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// 切断に使う操作。
///
/// 画面いっぱいが PC の入力面なので、ボタンを置くと必ず映像の邪魔になる。
/// 場所を取らない操作で抜けられるようにする。
///
/// どれを選んでも、その操作の間は PC への転送を止める。指の本数で
/// 切り替えるものは、PC 側で同じ本数を使いたい人には邪魔になるので
/// [DisconnectGesture.none] も用意してある。
enum DisconnectGesture {
  /// 3 本指で下に払う（既定）。「引き下ろして終わる」で覚えやすい。
  threeFingerSwipeDown,

  /// 3 本指で上に払う。下方向を PC 側で使いたい場合に。
  threeFingerSwipeUp,

  /// 4 本指で触れる。払う必要がないぶん確実だが、指が要る。
  fourFingerTap,

  /// 画面の左上を長押し。1 本指で済むが、その間 PC には届かない。
  longPressTopLeft,

  /// 使わない。設定画面からの切断だけになる。
  none;

  /// 設定画面に出す名前。
  String get label => switch (this) {
        DisconnectGesture.threeFingerSwipeDown => '3 本指で下に払う',
        DisconnectGesture.threeFingerSwipeUp   => '3 本指で上に払う',
        DisconnectGesture.fourFingerTap        => '4 本指で触れる',
        DisconnectGesture.longPressTopLeft     => '左上を長押し',
        DisconnectGesture.none                 => '使わない',
      };

  /// 何をするとどうなるかの補足。
  String get description => switch (this) {
        DisconnectGesture.threeFingerSwipeDown =>
          '映像の上を 3 本指で下へ払うと確認が出ます。',
        DisconnectGesture.threeFingerSwipeUp =>
          '映像の上を 3 本指で上へ払うと確認が出ます。',
        DisconnectGesture.fourFingerTap =>
          '4 本指で同時に触れると確認が出ます。払う必要はありません。',
        DisconnectGesture.longPressTopLeft =>
          '画面の左上あたりを長押しすると確認が出ます。',
        DisconnectGesture.none =>
          '設定画面の「接続を切ってホームに戻る」から切断します。',
      };

  /// この操作を認識するのに要る指の本数。
  int get pointerCount => switch (this) {
        DisconnectGesture.fourFingerTap    => 4,
        DisconnectGesture.longPressTopLeft => 1,
        DisconnectGesture.none             => 0,
        _                                  => 3,
      };
}

/// 映像画面の見た目に関する設定。
///
/// 端末ごとの都合なので PC 側へは送らず、この端末に保存する。
class DisplayPreferences extends ChangeNotifier {
  static const String _keyInsetTop    = 'display.inset.top';
  static const String _keyInsetBottom = 'display.inset.bottom';
  static const String _keyInsetLeft   = 'display.inset.left';
  static const String _keyInsetRight  = 'display.inset.right';
  static const String _keyShowDebug   = 'display.showDebug';
  static const String _keyShowButton  = 'display.showSettingsButton';
  static const String _keyGesture     = 'display.disconnectGesture';
  static const String _keyConfirm     = 'display.confirmBeforeDisconnect';
  static const String _keyKeepAwake   = 'display.keepScreenAwake';

  /// 余白の上限。これ以上狭めると映像が小さくなりすぎる。
  static const double maxInset = 80;

  double _top    = 0;
  double _bottom = 0;
  double _left   = 0;
  double _right  = 0;

  bool _showDebugOverlay   = false;
  bool _showSettingsButton = true;

  DisconnectGesture _disconnectGesture = DisconnectGesture.threeFingerSwipeDown;
  bool _confirmBeforeDisconnect = true;
  bool _keepScreenAwake = true;

  /// 映像とタッチ領域の余白。
  ///
  /// 画面の縁（丸みのある角、ジェスチャー操作に使われる帯）は
  /// 狙って触りにくい。そこを避けて内側だけを使うための設定。
  ///
  /// 映像そのものを内側に寄せるので、PC 画面のどこにでも触れる状態は保たれる。
  /// 触れる範囲だけ狭めると、PC 画面の端に永遠に届かなくなってしまう。
  EdgeInsets get insets => EdgeInsets.fromLTRB(_left, _top, _right, _bottom);

  double get top    => _top;
  double get bottom => _bottom;
  double get left   => _left;
  double get right  => _right;

  /// 受信状況などの数字を画面に出すか。
  bool get showDebugOverlay => _showDebugOverlay;

  /// 設定ボタンを画面に出すか。
  ///
  /// 隠したあとも、画面の左上を 2 回叩けば戻せる。
  /// 戻す手段が無いと設定画面へ行けなくなるため。
  bool get showSettingsButton => _showSettingsButton;

  /// 切断に使う操作。
  DisconnectGesture get disconnectGesture => _disconnectGesture;

  Future<void> setDisconnectGesture(DisconnectGesture value) async {
    _disconnectGesture = value;
    notifyListeners();
    await _save();
  }

  /// 切断の前に確認を出すか。
  ///
  /// 既定は出す。ジェスチャーは意図せず出ることがあり、作業中に
  /// 黙って画面が消えると理由が分からない。
  ///
  /// ただし慣れて確実に出せるようになると、毎回の確認は手間になる。
  /// 4 本指タップのように誤爆しにくい操作を選んだ場合も同じ。
  bool get confirmBeforeDisconnect => _confirmBeforeDisconnect;

  Future<void> setConfirmBeforeDisconnect(bool value) async {
    _confirmBeforeDisconnect = value;
    notifyListeners();
    await _save();
  }

  /// 映像を出している間、画面を消させないか。
  ///
  /// 既定は消させない。2 枚目のモニターとして使っている最中に
  /// 画面が消えると、モニターとして成立しないため。
  ///
  /// ただし、映しっぱなしで放置する使い方では電池を食う。切れるようにしてある。
  bool get keepScreenAwake => _keepScreenAwake;

  Future<void> setKeepScreenAwake(bool value) async {
    _keepScreenAwake = value;
    notifyListeners();
    await _save();
  }

  /// 保存済みの設定を読み込む。読めなければ既定値のまま。
  Future<void> load() async {
    try {
      final prefs = await SharedPreferences.getInstance();

      _top    = prefs.getDouble(_keyInsetTop)    ?? 0;
      _bottom = prefs.getDouble(_keyInsetBottom) ?? 0;
      _left   = prefs.getDouble(_keyInsetLeft)   ?? 0;
      _right  = prefs.getDouble(_keyInsetRight)  ?? 0;

      _showDebugOverlay   = prefs.getBool(_keyShowDebug)  ?? false;
      _showSettingsButton = prefs.getBool(_keyShowButton) ?? true;

      // 選択肢が減った版から戻した場合に落ちないよう、範囲を見てから使う
      final gestureIndex = prefs.getInt(_keyGesture);
      if (gestureIndex != null &&
          gestureIndex >= 0 &&
          gestureIndex < DisconnectGesture.values.length) {
        _disconnectGesture = DisconnectGesture.values[gestureIndex];
      }

      _confirmBeforeDisconnect = prefs.getBool(_keyConfirm) ?? true;
      _keepScreenAwake         = prefs.getBool(_keyKeepAwake) ?? true;

      

      notifyListeners();
    } catch (_) {
      // 読めなくても既定値で動く
    }
  }

  Future<void> setInsets({
    double? top,
    double? bottom,
    double? left,
    double? right,
  }) async {
    _top    = (top    ?? _top).clamp(0.0, maxInset);
    _bottom = (bottom ?? _bottom).clamp(0.0, maxInset);
    _left   = (left   ?? _left).clamp(0.0, maxInset);
    _right  = (right  ?? _right).clamp(0.0, maxInset);

    notifyListeners();
    await _save();
  }

  Future<void> setShowDebugOverlay(bool value) async {
    _showDebugOverlay = value;
    notifyListeners();
    await _save();
  }

  Future<void> setShowSettingsButton(bool value) async {
    _showSettingsButton = value;
    notifyListeners();
    await _save();
  }

  Future<void> _save() async {
    try {
      final prefs = await SharedPreferences.getInstance();

      await prefs.setDouble(_keyInsetTop,    _top);
      await prefs.setDouble(_keyInsetBottom, _bottom);
      await prefs.setDouble(_keyInsetLeft,   _left);
      await prefs.setDouble(_keyInsetRight,  _right);
      await prefs.setBool(_keyShowDebug,     _showDebugOverlay);
      await prefs.setBool(_keyShowButton,    _showSettingsButton);
      await prefs.setInt(_keyGesture,        _disconnectGesture.index);
      await prefs.setBool(_keyConfirm,       _confirmBeforeDisconnect);
      await prefs.setBool(_keyKeepAwake,     _keepScreenAwake);
    } catch (_) {
      // 保存できなくても、この起動の間は設定が効く
    }
  }
}

/// アプリ全体で 1 つだけ持つ設定。
///
/// 画面をまたいで同じ値を見せる必要があり、数も少ないので、
/// 状態管理の仕組みを持ち込まずここで保持する。
final displayPreferences = DisplayPreferences();
