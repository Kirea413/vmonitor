import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/gestures.dart'
    show PointerDeviceKind, PointerHoverEvent;
import 'package:flutter/widgets.dart';

import '../transport/transport.dart';
// 切断に使う操作の選択肢。設定として保存するので ui 側に置いてある。
import '../ui/display_preferences.dart' show DisconnectGesture;

/// タッチ入力プロキシ: スマホのタッチイベントを収集して PC へ転送する
abstract class TouchInputProxy {
  /// タッチイベントをキャプチャするストリーム
  Stream<TouchEvent> captureEvents();

  /// タッチイベントをシリアライズしてトランスポート経由で送信する
  Future<void> send(TouchEvent event);

  /// 向き変更時に座標変換マトリクスを更新する
  void updateTransform(Resolution displayResolution, Orientation orientation);

  /// 正規化座標 (normX, normY) をピクセル座標に変換する
  PixelPoint transform(double normX, double normY);
}

// ---------------------------------------------------------------------------
// タッチイベント関連データモデル
// ---------------------------------------------------------------------------

/// タッチイベント (マルチタッチ対応)
class TouchEvent {
  final List<TouchPoint> points;
  final DateTime timestamp;
  final Orientation currentOrientation;

  const TouchEvent({
    required this.points,
    required this.timestamp,
    required this.currentOrientation,
  });
}

/// タッチポイント
class TouchPoint {
  /// タッチポイント識別子
  final int id;

  /// 正規化座標 [0.0, 1.0]
  final double x;
  final double y;

  /// 圧力 [0.0, 1.0]
  final double pressure;

  final TouchPhase phase;

  /// ペン（スタイラス）か。false なら指。
  ///
  /// Windows ではタッチとペンが別の入力として扱われる。ペンとして
  /// 送れば筆圧や傾きを見るアプリが本来の動きをするし、
  /// 手のひらが当たっても線にならない。
  final bool isPen;

  const TouchPoint({
    required this.id,
    required this.x,
    required this.y,
    required this.pressure,
    required this.phase,
    this.isPen = false,
  });
}

/// タッチフェーズ
enum TouchPhase {
  began,
  moved,
  ended,
  cancelled,

  /// 触れずに近づいている（ペンのホバー）。
  ///
  /// Windows はペンが画面に近づくと、触れる前から位置を示す丸を出す。
  /// これがあると狙った場所に下ろせる。触れてからでないと分からないと、
  /// 一筆目が必ずずれる。
  ///
  /// 末尾に足すこと。並びがそのまま通信の値になっているので、
  /// 途中に入れると古い版と食い違う。
  hovered,
}

/// 画面向き
enum Orientation { portrait, landscape, portraitFlipped, landscapeFlipped }

/// ディスプレイ解像度
class Resolution {
  final int width;
  final int height;

  const Resolution({required this.width, required this.height});
}

/// ピクセル座標
class PixelPoint {
  final double x;
  final double y;

  const PixelPoint({required this.x, required this.y});
}

// ---------------------------------------------------------------------------
// FlutterTouchInputProxy — 具体実装
// ---------------------------------------------------------------------------

/// Flutter の [Listener] ウィジェットを通じてタッチイベントを捕捉し、
/// 正規化座標にシリアライズして [Transport] 経由で PC へ送信する実装。
///
/// Requirements 6.1, 6.4:
/// - セッションがアクティブな間、座標・圧力を含む全タッチイベントを収集して送信する。
/// - マルチタッチ時は全ポイントを同一メッセージで同時に送信する（部分送信禁止）。
class FlutterTouchInputProxy implements TouchInputProxy {
  /// ウィジェットの論理サイズ（タッチ座標正規化に使用）。
  /// [TouchInputView] が `LayoutBuilder` 経由でセットする。
  Size _widgetSize = Size.zero;

  /// 現在アクティブなタッチポイント: pointer ID → フレームオフセット座標
  final Map<int, Offset> _activePointers = {};

  /// 触れ続けていることを知らせ続けるためのタイマー。
  ///
  /// 端末は指が動いたときにしか知らせを出さない。長押しのように
  /// 止めていると何も送られず、PC 側からは生きているのか離れたのか
  /// 区別が付かない。
  ///
  /// PC 側は届かない接触を一定時間で解放する。動かなくてもこちらから
  /// 送り続けることで、押している間は解放されないようにする。
  /// 逆に「離した」の知らせを取りこぼしても、送信が止まるので
  /// PC 側が自分で気付いて離せる。
  Timer? _holdTimer;

  static const Duration _holdInterval = Duration(milliseconds: 200);

  /// タッチイベントを配信するコントローラ。
  final StreamController<TouchEvent> _controller =
      StreamController<TouchEvent>.broadcast();

  /// 送信先トランスポート。[attach] で設定する。
  Transport? _transport;

  /// 現在の向き。
  Orientation _orientation = Orientation.portrait;

  /// 現在の表示解像度（座標変換用）。
  Resolution _displayResolution = const Resolution(width: 1080, height: 1920);

  // ─── Public API ──────────────────────────────────────────────────────────

  /// トランスポートをアタッチする。セッション確立後に呼び出す。
  void attach(Transport transport) {
    _transport = transport;
  }

  /// トランスポートをデタッチする。セッション終了時に呼び出す。
  void detach() {
    _transport = null;
  }

  /// ウィジェットサイズを更新する。[TouchInputView] が LayoutBuilder から呼び出す。
  void updateSize(Size size) {
    _widgetSize = size;
  }

  @override
  Stream<TouchEvent> captureEvents() => _controller.stream;

  @override
  Future<void> send(TouchEvent event) async {
    final transport = _transport;
    if (transport == null) return;

    final payload = _serializeEvent(event);
    await transport.send(payload, ChannelId.touch);
  }

  @override
  void updateTransform(Resolution displayResolution, Orientation orientation) {
    _displayResolution = displayResolution;
    _orientation = orientation;
  }

  /// 端末の向きだけを更新する。
  ///
  /// 表示解像度は PC 側から通知されるまで変える必要がないため、
  /// 向きの変化だけを反映したい場面ではこちらを使う。
  void updateOrientation(Orientation orientation) {
    _orientation = orientation;
  }

  @override
  PixelPoint transform(double normX, double normY) {
    return PixelPoint(
      x: normX * _displayResolution.width,
      y: normY * _displayResolution.height,
    );
  }

  // ─── Flutter pointer event handlers ──────────────────────────────────────

  /// [PointerDownEvent] を処理してポインターを登録し、TouchEvent を発行する。
  void onPointerDown(PointerDownEvent event) {
    downCount++;
    lastKind = event.kind.name;
    _activePointers[event.pointer] = event.localPosition;
    _emitEvent(event.pointer, event.localPosition, event.pressure,
        TouchPhase.began, _isPen(event.kind));
  }

  /// [PointerMoveEvent] を処理してポインター位置を更新し、TouchEvent を発行する。
  void onPointerMove(PointerMoveEvent event) {
    moveCount++;
    _activePointers[event.pointer] = event.localPosition;
    _emitEvent(event.pointer, event.localPosition, event.pressure,
        TouchPhase.moved, _isPen(event.kind));
  }

  /// [PointerUpEvent] を処理してポインターを削除し、TouchEvent を発行する。
  void onPointerUp(PointerUpEvent event) {
    upCount++;
    _activePointers.remove(event.pointer);
    _emitEvent(event.pointer, event.localPosition, event.pressure,
        TouchPhase.ended, _isPen(event.kind));

    _resendRelease(event.pointer, event.localPosition, _isPen(event.kind),
        TouchPhase.ended);
  }

  /// 「離した」をもう一度送る。
  ///
  /// 押した・動いたは、取りこぼしても次が来る。「離した」だけは
  /// ストロークの最後の 1 通で、これを落とすと PC 側は時間切れまで
  /// 押されたままになる。利用者からは長押しに見える。
  ///
  /// 一度離した指の「離した」は何度届いても構わない。PC 側は
  /// 覚えの無い指の知らせを捨てるので、二重に離れることはない。
  void _resendRelease(
    int pointer,
    Offset position,
    bool isPen,
    TouchPhase phase,
  ) {
    for (final delay in _releaseRetryDelays) {
      Timer(delay, () {
        // 同じ番号で押し直されていたら、送ってはいけない。
        // 始まったばかりのストロークを消してしまう。
        if (_activePointers.containsKey(pointer)) return;

        _emitEvent(pointer, position, 0.0, phase, isPen);
      });
    }
  }

  static const List<Duration> _releaseRetryDelays = [
    Duration(milliseconds: 60),
    Duration(milliseconds: 200),
  ];

  /// [PointerCancelEvent] を処理してポインターをキャンセルし、TouchEvent を発行する。
  /// 触れずに近づいているペンの位置を送る。
  ///
  /// 指では起きない。ホバーを持つのはペンとマウスだけ。
  void onPointerHover(PointerHoverEvent event) {
    if (!_isPen(event.kind)) return;

    _emitEvent(event.pointer, event.localPosition, 0.0,
        TouchPhase.hovered, true);
  }

  void onPointerCancel(PointerCancelEvent event) {
    cancelCount++;
    _activePointers.remove(event.pointer);
    _emitEvent(event.pointer, event.localPosition, event.pressure,
        TouchPhase.cancelled, _isPen(event.kind));

    _resendRelease(event.pointer, event.localPosition, _isPen(event.kind),
        TouchPhase.cancelled);
  }

  /// いま触れていることになっている指を、すべて離したことにする。
  ///
  /// 端末側のジェスチャー操作に切り替えるときに使う。転送をやめるだけだと、
  /// PC 側は指が置かれたままだと思い続け、押しっぱなしの状態が残る。
  void releaseAllPointers() {
    for (final entry in _activePointers.entries.toList()) {
      _emitEvent(entry.key, entry.value, 0.0, TouchPhase.cancelled, false);
    }

    _activePointers.clear();
  }

  // ─── Internal ─────────────────────────────────────────────────────────────

  /// 指定ポインターのイベントを正規化し、全アクティブポインターと合わせて
  /// [TouchEvent] を生成してストリームに流し、トランスポートで送信する。
  ///
  /// Requirements 6.4: 全タッチポイントを同一メッセージで同時に送信する。
  /// Flutter のポインター種別を「ペンかどうか」に落とす。
  ///
  /// Apple Pencil や Android のスタイラスは stylus として届く。
  /// 反転させて消しゴム側を使うと invertedStylus になるが、
  /// Windows 側でそこまで扱っていないので、どちらもペンとする。
  static bool _isPen(PointerDeviceKind kind) =>
      kind == PointerDeviceKind.stylus ||
      kind == PointerDeviceKind.invertedStylus;

  /// 触れている指があるあいだだけ、知らせ続ける。
  void _updateHoldTimer() {
    if (_activePointers.isEmpty) {
      _holdTimer?.cancel();
      _holdTimer = null;
      return;
    }

    _holdTimer ??= Timer.periodic(_holdInterval, (_) => _emitHold());
  }

  /// 触れ続けている指を、同じ位置のまま送り直す。
  void _emitHold() {
    if (_activePointers.isEmpty) {
      _updateHoldTimer();
      return;
    }

    final first = _activePointers.entries.first;

    // 主ポイント以外は _emitEvent が moved として一緒に載せてくれる
    _emitEvent(first.key, first.value, _lastPressure[first.key] ?? 0.5,
        TouchPhase.moved, _lastIsPen[first.key] ?? false);
  }

  /// Flutter から何を受け取ったかの数え上げ（切り分け用）。
  ///
  /// PC 側の記録では「離した」が 1 件も届いていなかった。遅れている
  /// のではなく来ていない。ただ、それが
  ///   ・Flutter がそもそも up を出していない
  ///   ・出ているがこちらが送れていない
  /// のどちらなのかは、PC 側からは区別が付かない。端末の画面に出す。
  static int downCount = 0;
  static int moveCount = 0;
  static int upCount = 0;
  static int cancelCount = 0;
  static String lastKind = '-';

  /// 送り出せた「離した」の数と、送れなかった数。
  ///
  /// upCount は onPointerUp の入口で増える。心拍タイマーが止まるのも
  /// _emitEvent の冒頭。つまりどちらも「送信まで辿り着いた」証拠には
  /// ならない。手前で例外が出ていれば、up が増えて心拍も止まるのに
  /// PC には何も届かない。実際そう見えている。
  ///
  /// 送信の直前と、その結果を別々に数える。
  static int sentRelease = 0;
  static int sendFailed = 0;
  static String lastError = '-';

  /// 送り直すときに使う、直近の筆圧と種別。
  final Map<int, double> _lastPressure = {};
  final Map<int, bool>   _lastIsPen    = {};

  void _emitEvent(
    int pointer,
    Offset localPosition,
    double pressure,
    TouchPhase phase,
    bool isPen,
  ) {
    if (phase == TouchPhase.ended || phase == TouchPhase.cancelled) {
      _lastPressure.remove(pointer);
      _lastIsPen.remove(pointer);
    } else {
      _lastPressure[pointer] = pressure;
      _lastIsPen[pointer] = isPen;
    }

    _updateHoldTimer();
    final widgetSize = _widgetSize;
    final points = <TouchPoint>[];

    // 現在のポインターを主ポイントとして追加
    final normX = widgetSize.width > 0
        ? (localPosition.dx / widgetSize.width).clamp(0.0, 1.0)
        : 0.0;
    final normY = widgetSize.height > 0
        ? (localPosition.dy / widgetSize.height).clamp(0.0, 1.0)
        : 0.0;

    points.add(TouchPoint(
      id: pointer,
      x: normX,
      y: normY,
      pressure: pressure.clamp(0.0, 1.0),
      isPen: isPen,
      phase: phase,
    ));

    // 他のアクティブポインター（マルチタッチ）を moved フェーズとして追加
    // Requirements 6.4: 全ポイントを同一メッセージに含める
    for (final entry in _activePointers.entries) {
      if (entry.key == pointer) continue; // 主ポインターは既に追加済み

      final otherOffset = entry.value;
      final otherNormX = widgetSize.width > 0
          ? (otherOffset.dx / widgetSize.width).clamp(0.0, 1.0)
          : 0.0;
      final otherNormY = widgetSize.height > 0
          ? (otherOffset.dy / widgetSize.height).clamp(0.0, 1.0)
          : 0.0;

      points.add(TouchPoint(
        id: entry.key,
        x: otherNormX,
        y: otherNormY,
        pressure: 0.0,
        phase: TouchPhase.moved,
      ));
    }

    final touchEvent = TouchEvent(
      points: points,
      timestamp: DateTime.now(),
      currentOrientation: _orientation,
    );

    final isRelease =
        phase == TouchPhase.ended || phase == TouchPhase.cancelled;

    // ストリームに流す。
    //
    // ここが投げると、この下の送信に辿り着かない。「離した」だけが
    // 消える形になり、PC 側は時間切れまで押されたままになる。
    // 流せなくても送信は続ける。
    try {
      _controller.add(touchEvent);
    } catch (e) {
      lastError = 'add: $e';
    }

    if (isRelease) sentRelease++;

    // トランスポートで送信（非同期エラーは握り潰してストリームを止めない）
    send(touchEvent).catchError((Object e) {
      sendFailed++;
      lastError = 'send: $e';
    });
  }

  /// [TouchEvent] をバイナリ形式にシリアライズする。
  ///
  /// フォーマット（リトルエンディアン）:
  /// ```
  /// ┌─────────────────────────────────────────────────────────────┐
  /// │ timestamp_us (8 bytes, int64)                               │
  /// │ orientation  (1 byte)                                       │
  /// │ point_count  (1 byte)                                       │
  /// │ per point:                                                  │
  /// │   id         (4 bytes, int32)                               │
  /// │   x          (4 bytes, float32)                             │
  /// │   y          (4 bytes, float32)                             │
  /// │   pressure   (4 bytes, float32)                             │
  /// │   phase      (1 byte)                                       │
  /// └─────────────────────────────────────────────────────────────┘
  /// ```
  static Uint8List _serializeEvent(TouchEvent event) {
    // 1 イベントあたりのヘッダー: 8(timestamp) + 1(orientation) + 1(count) = 10 バイト
    // 1 ポイントあたり:
    //   4(id) + 4(x) + 4(y) + 4(pressure) + 1(phase) + 1(kind) = 18 バイト
    //
    // kind を足したぶん 17 から増えている。PC 側は長さを見て
    // 古い 17 バイトの並びも読めるようにしてある。
    const headerSize = 10;
    const pointSize = 18;

    final buffer = ByteData(headerSize + event.points.length * pointSize);
    int offset = 0;

    // タイムスタンプ (Unix マイクロ秒, int64)
    final timestampUs = event.timestamp.microsecondsSinceEpoch;
    buffer.setInt64(offset, timestampUs, Endian.little);
    offset += 8;

    // 向き (1 byte)
    buffer.setUint8(offset, event.currentOrientation.index);
    offset += 1;

    // タッチポイント数 (1 byte)
    buffer.setUint8(offset, event.points.length);
    offset += 1;

    // タッチポイント列
    for (final point in event.points) {
      buffer.setInt32(offset, point.id, Endian.little);
      offset += 4;
      buffer.setFloat32(offset, point.x, Endian.little);
      offset += 4;
      buffer.setFloat32(offset, point.y, Endian.little);
      offset += 4;
      buffer.setFloat32(offset, point.pressure, Endian.little);
      offset += 4;
      buffer.setUint8(offset, point.phase.index);
      offset += 1;
      buffer.setUint8(offset, point.isPen ? 1 : 0);
      offset += 1;
    }

    return buffer.buffer.asUint8List();
  }

  /// リソースを解放する。
  void dispose() {
    // 送り直しを先に止める。残すと解放後に動く。
    _holdTimer?.cancel();
    _holdTimer = null;

    _controller.close();
    _activePointers.clear();
    _lastPressure.clear();
    _lastIsPen.clear();
    _transport = null;
  }
}

// ---------------------------------------------------------------------------
// TouchInputView — Listener ベースのウィジェット
// ---------------------------------------------------------------------------

/// [FlutterTouchInputProxy] を使ってタッチイベントを捕捉するウィジェット。
///
/// `Listener` でシングル・マルチタッチのポインターイベントをすべて捕捉し、
/// [FlutterTouchInputProxy] へ委譲する。
///
/// ```dart
/// TouchInputView(
///   proxy: proxy,
///   child: RendererView(encodedFrames: videoStream),
/// )
/// ```
class TouchInputView extends StatefulWidget {
  final FlutterTouchInputProxy proxy;
  final Widget child;

  /// 切断の操作が行われたときに呼ぶ。
  ///
  /// 画面いっぱいが PC の入力面になっているので、切断のためのボタンを
  /// 置くと必ず映像の邪魔になる。場所を取らない操作で抜けられるようにする。
  final VoidCallback? onDisconnectGesture;

  /// どの操作を切断とみなすか。
  final DisconnectGesture gesture;

  const TouchInputView({
    super.key,
    required this.proxy,
    required this.child,
    this.onDisconnectGesture,
    this.gesture = DisconnectGesture.threeFingerSwipeDown,
  });

  @override
  State<TouchInputView> createState() => _TouchInputViewState();
}

class _TouchInputViewState extends State<TouchInputView> {
  /// 切断とみなす移動量（論理ピクセル）。
  ///
  /// 短すぎると、指を置いただけで切れてしまう。
  static const double _disconnectDistance = 140;

  /// 長押しとみなす時間。
  static const Duration _longPressDuration = Duration(milliseconds: 700);

  /// 左上の長押しを受け付ける範囲（画面比）。
  static const double _cornerRatio = 0.25;

  /// この本数以上が同時に触れたら、PC への転送をやめて端末側の操作とみなす。
  ///
  /// 1 本はタップとドラッグ、2 本は拡大縮小として PC へ送りたいので、
  /// 指の本数で見分ける操作は 3 本以上にしてある。
  int get _gesturePointerCount => widget.gesture.pointerCount;

  /// 指の本数で見分ける操作か（左上の長押しだけは 1 本で扱いが違う）。
  bool get _usesPointerCount =>
      widget.gesture != DisconnectGesture.longPressTopLeft &&
      widget.gesture != DisconnectGesture.none;

  Timer? _longPressTimer;

  /// いま触れている指の位置。
  final Map<int, Offset> _pointers = {};

  /// 端末側のジェスチャーとして扱っている最中か。
  bool _gestureMode = false;

  /// ジェスチャーに入った時点の指の重心。
  Offset? _gestureOrigin;

  /// この一連の操作で既に切断を出したか（何度も出さない）。
  bool _fired = false;

  Offset _centroid() {
    if (_pointers.isEmpty) return Offset.zero;

    var sum = Offset.zero;
    for (final p in _pointers.values) {
      sum += p;
    }

    return sum / _pointers.length.toDouble();
  }

  /// 左上の長押しを見張る。
  ///
  /// こちらは 1 本指なので、動き出したら普通の操作として PC へ送りたい。
  /// 指が止まったまま時間が経ったときだけ切断とみなす。
  void _armLongPress(Offset position, Size size) {
    _longPressTimer?.cancel();

    final inCorner = position.dx < size.width  * _cornerRatio &&
                     position.dy < size.height * _cornerRatio;

    if (!inCorner) return;

    _longPressTimer = Timer(_longPressDuration, () {
      if (_fired || _pointers.length != 1) return;

      _fired = true;

      // 押しっぱなしが PC に残らないよう離しておく
      widget.proxy.releaseAllPointers();
      widget.onDisconnectGesture?.call();
    });
  }

  void _onDown(PointerDownEvent event) {
    _pointers[event.pointer] = event.localPosition;

    if (widget.gesture == DisconnectGesture.longPressTopLeft &&
        _pointers.length == 1) {
      _armLongPress(event.localPosition, _size);
    }

    if (_usesPointerCount &&
        !_gestureMode &&
        _pointers.length >= _gesturePointerCount) {
      // ここから先は端末の操作。PC には送らない。
      //
      // 既に送ってしまった指は離したことにする。そうしないと
      // PC 側に押されたままの接触が残る。
      _gestureMode  = true;
      _gestureOrigin = _centroid();
      _fired = false;

      widget.proxy.releaseAllPointers();

      // 4 本指で触れる操作は、払う必要がない。揃った時点で成立。
      if (widget.gesture == DisconnectGesture.fourFingerTap) {
        _fired = true;
        widget.onDisconnectGesture?.call();
      }

      return;
    }

    if (_gestureMode) return;

    widget.proxy.onPointerDown(event);
  }

  void _onMove(PointerMoveEvent event) {
    _pointers[event.pointer] = event.localPosition;

    if (!_gestureMode) {
      // 指が動いたなら長押しではない
      _longPressTimer?.cancel();

      widget.proxy.onPointerMove(event);
      return;
    }

    if (_fired) return;

    final origin = _gestureOrigin;
    if (origin == null) return;

    final moved = _centroid() - origin;

    // 縦にしっかり払われたか。
    // 横に流れているものは別の操作なので拾わない。
    if (moved.dy.abs() < moved.dx.abs()) return;

    final travelled = switch (widget.gesture) {
      DisconnectGesture.threeFingerSwipeDown =>  moved.dy,
      DisconnectGesture.threeFingerSwipeUp   => -moved.dy,
      _                                      =>  0.0,
    };

    if (travelled < _disconnectDistance) return;

    _fired = true;
    widget.onDisconnectGesture?.call();
  }

  void _onUp(PointerUpEvent event) {
    _pointers.remove(event.pointer);

    if (!_gestureMode) {
      widget.proxy.onPointerUp(event);
    }

    _endGestureIfIdle();
  }

  /// 触れずに近づいているペン。ジェスチャー中は送らない。
  void _onHover(PointerHoverEvent event) {
    if (_gestureMode) return;

    widget.proxy.onPointerHover(event);
  }

  void _onCancel(PointerCancelEvent event) {
    _pointers.remove(event.pointer);

    if (!_gestureMode) {
      widget.proxy.onPointerCancel(event);
    }

    _endGestureIfIdle();
  }

  /// 全部離れたら、ふつうの転送に戻す。
  void _endGestureIfIdle() {
    if (_pointers.isNotEmpty) return;

    _longPressTimer?.cancel();
    _longPressTimer = null;

    _gestureMode   = false;
    _gestureOrigin = null;
    _fired         = false;
  }

  /// 直近のウィジェットの大きさ（隅の判定に使う）。
  Size _size = Size.zero;

  @override
  void dispose() {
    _longPressTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        _size = Size(constraints.maxWidth, constraints.maxHeight);

        // ウィジェットサイズをプロキシに通知する（正規化計算に使用）
        widget.proxy.updateSize(_size);

        return Listener(
          // HitTestBehavior.opaque: 子ウィジェットがヒット不要でも全面でイベントを受け取る
          behavior: HitTestBehavior.opaque,
          onPointerHover: _onHover,
          onPointerDown: _onDown,
          onPointerMove: _onMove,
          onPointerUp: _onUp,
          onPointerCancel: _onCancel,
          child: widget.child,
        );
      },
    );
  }
}
