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
  /// 向きごとの余白を保存する鍵の頭。
  static String _insetPrefix(Orientation orientation) =>
      orientation == Orientation.portrait
          ? 'display.inset.portrait'
          : 'display.inset.landscape';

  // 向きで分ける前の鍵。読み込みのときだけ見る（引き継ぎ用）。
  static const String _keyInsetTop    = 'display.inset.top';
  static const String _keyInsetBottom = 'display.inset.bottom';
  static const String _keyInsetLeft   = 'display.inset.left';
  static const String _keyInsetRight  = 'display.inset.right';
  static const String _keyShowDebug   = 'display.showDebug';
  static const String _keyShowButton  = 'display.showSettingsButton';
  static const String _keyGesture     = 'display.disconnectGesture';
  static const String _keyConfirm     = 'display.confirmBeforeDisconnect';
  static const String _keyKeepAwake   = 'display.keepScreenAwake';
  static const String _keyKeepBright  = 'display.keepScreenBright';
  static const String _keyBrightness  = 'display.screenBrightness';

  /// 余白の上限。これ以上狭めると映像が小さくなりすぎる。
  static const double maxInset = 80;

  // 余白は画面の向きごとに別々に覚える。
  //
  // 1 組だけだと、縦で「下を空ける」と決めた値が、横にした瞬間
  // 画面の下辺（物理的には横っ腹）に付いてしまう。避けたかった
  // ホームバーや丸い角は別の場所へ移っているので、まったく的外れな
  // ところが削られる。
  //
  // 向きごとに持てば、それぞれの向きで一度合わせるだけで、
  // 以後は回しても正しい場所に付く。
  final Map<Orientation, EdgeInsets> _insets = {
    Orientation.portrait:  EdgeInsets.zero,
    Orientation.landscape: EdgeInsets.zero,
  };

  /// いま画面がどちらを向いているか。
  ///
  /// 余白を読むときの既定として使う。映像画面が向きの変化に合わせて
  /// 更新する。
  Orientation _orientation = Orientation.portrait;

  bool _showDebugOverlay   = false;
  bool _showSettingsButton = true;

  DisconnectGesture _disconnectGesture = DisconnectGesture.threeFingerSwipeDown;
  bool _confirmBeforeDisconnect = true;
  bool _keepScreenAwake  = true;
  bool _keepScreenBright = true;
  double _screenBrightness = 1.0;

  /// 映像とタッチ領域の余白。
  ///
  /// 画面の縁（丸みのある角、ジェスチャー操作に使われる帯）は
  /// 狙って触りにくい。そこを避けて内側だけを使うための設定。
  ///
  /// 映像そのものを内側に寄せるので、PC 画面のどこにでも触れる状態は保たれる。
  /// 触れる範囲だけ狭めると、PC 画面の端に永遠に届かなくなってしまう。
  EdgeInsets get insets => insetsFor(_orientation);

  /// 指定した向きの余白。
  EdgeInsets insetsFor(Orientation orientation) =>
      _insets[orientation] ?? EdgeInsets.zero;

  /// いま基準にしている向き。
  Orientation get orientation => _orientation;

  /// 画面の向きが変わったことを伝える。
  ///
  /// 変わっていなければ何もしない（毎フレーム呼ばれても平気にしておく）。
  void setOrientation(Orientation value) {
    if (_orientation == value) return;

    _orientation = value;
    notifyListeners();
  }

  double get top    => insets.top;
  double get bottom => insets.bottom;
  double get left   => insets.left;
  double get right  => insets.right;

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

  /// 映像を出している間、明るさを固定するか。
  ///
  /// 画面を消させる・消させないとは別の話。消えないようにしても、
  /// 触らない時間が続けば端末が自動調整や減光で勝手に暗くする。
  /// モニターとして置いて眺めているだけのときに、まさにこれが起きる。
  ///
  /// 固定するのはこのアプリが前面にある間だけで、端末全体の
  /// 明るさ設定は書き換えない。
  bool get keepScreenBright => _keepScreenBright;

  Future<void> setKeepScreenBright(bool value) async {
    _keepScreenBright = value;
    notifyListeners();
    await _save();
  }

  /// 固定するときの明るさ（0.05〜1.0）。
  ///
  /// 常に最大にすると電池と発熱がきつい。暗くならなければ十分、
  /// という使い方のために選べるようにしてある。
  double get screenBrightness => _screenBrightness;

  Future<void> setScreenBrightness(double value) async {
    _screenBrightness = value.clamp(minBrightness, 1.0);
    notifyListeners();
    await _save();
  }

  /// これより暗くすると、点いているのか分からなくなる端末がある。
  static const double minBrightness = 0.05;

  /// ネイティブへ渡す明るさ。固定しない設定なら null。
  double? get brightnessOverride => _keepScreenBright ? _screenBrightness : null;

  /// 保存済みの設定を読み込む。読めなければ既定値のまま。
  Future<void> load() async {
    try {
      final prefs = await SharedPreferences.getInstance();

      // 向きごとに分ける前の設定。縦向きのものとして引き継ぐ。
      // 何も無ければ 0。
      final legacy = EdgeInsets.fromLTRB(
        prefs.getDouble(_keyInsetLeft)   ?? 0,
        prefs.getDouble(_keyInsetTop)    ?? 0,
        prefs.getDouble(_keyInsetRight)  ?? 0,
        prefs.getDouble(_keyInsetBottom) ?? 0,
      );

      for (final orientation in Orientation.values) {
        final prefix = _insetPrefix(orientation);

        final stored = EdgeInsets.fromLTRB(
          prefs.getDouble('$prefix.left')   ?? -1,
          prefs.getDouble('$prefix.top')    ?? -1,
          prefs.getDouble('$prefix.right')  ?? -1,
          prefs.getDouble('$prefix.bottom') ?? -1,
        );

        // -1 は「保存されていない」の印。まだ分けて保存していない場合は
        // 古い値をそのまま使う（縦で合わせた値を横にも一旦入れておく。
        // 合わなければ画面を見ながら直せる）。
        _insets[orientation] = stored.left < 0
            ? legacy
            : EdgeInsets.fromLTRB(
                stored.left.clamp(0.0, maxInset),
                stored.top.clamp(0.0, maxInset),
                stored.right.clamp(0.0, maxInset),
                stored.bottom.clamp(0.0, maxInset),
              );
      }

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
      _keepScreenBright        = prefs.getBool(_keyKeepBright) ?? true;

      _screenBrightness =
          (prefs.getDouble(_keyBrightness) ?? 1.0).clamp(minBrightness, 1.0);



      notifyListeners();
    } catch (_) {
      // 読めなくても既定値で動く
    }
  }

  /// いまの向きの余白を変える。
  Future<void> setInsets({
    double? top,
    double? bottom,
    double? left,
    double? right,
    Orientation? orientation,
  }) async {
    final target  = orientation ?? _orientation;
    final current = insetsFor(target);

    _insets[target] = EdgeInsets.fromLTRB(
      (left   ?? current.left).clamp(0.0, maxInset),
      (top    ?? current.top).clamp(0.0, maxInset),
      (right  ?? current.right).clamp(0.0, maxInset),
      (bottom ?? current.bottom).clamp(0.0, maxInset),
    );

    notifyListeners();
    await _save();
  }

  /// いまの向きの余白をすべて 0 に戻す。
  Future<void> clearInsets({Orientation? orientation}) async {
    _insets[orientation ?? _orientation] = EdgeInsets.zero;

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

      for (final orientation in Orientation.values) {
        final prefix = _insetPrefix(orientation);
        final value  = insetsFor(orientation);

        await prefs.setDouble('$prefix.left',   value.left);
        await prefs.setDouble('$prefix.top',    value.top);
        await prefs.setDouble('$prefix.right',  value.right);
        await prefs.setDouble('$prefix.bottom', value.bottom);
      }
      await prefs.setBool(_keyShowDebug,     _showDebugOverlay);
      await prefs.setBool(_keyShowButton,    _showSettingsButton);
      await prefs.setInt(_keyGesture,        _disconnectGesture.index);
      await prefs.setBool(_keyConfirm,       _confirmBeforeDisconnect);
      await prefs.setBool(_keyKeepAwake,     _keepScreenAwake);
      await prefs.setBool(_keyKeepBright,    _keepScreenBright);
      await prefs.setDouble(_keyBrightness,  _screenBrightness);
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
