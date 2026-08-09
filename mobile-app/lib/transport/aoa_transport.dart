import 'dart:async';

import 'package:flutter/services.dart';

import 'transport.dart';

/// USB 直結 (AOA) トランスポート。
///
/// PC がこの端末をアクセサリーモードへ切り替えると、端末は USB デバイス側、
/// PC は USB ホスト側になる。adb も Wi-Fi も要らず、開発者オプションも不要。
///
/// Flutter からは `UsbAccessory` を直接触れないため、
/// Kotlin 側の `AoaPlugin` がストリームの面倒を見る。
/// ここはそのプラットフォームチャンネルを [Transport] の形に合わせる薄い層。
///
/// フレームの組み立てとほどきは Kotlin 側で済んでいるので、
/// この層ではチャンネル ID とペイロードだけを扱う。
class AoaTransport implements Transport {
  static const MethodChannel _method = MethodChannel('vmonitor/aoa');
  static const EventChannel _frames = EventChannel('vmonitor/aoa/frames');
  static const EventChannel _states = EventChannel('vmonitor/aoa/state');

  /// USB 2.0 の公称帯域 (480 Mbps)。
  static const int _defaultBandwidthBps = 480 * 1000 * 1000;

  StreamController<({ChannelId channel, Uint8List data})>? _receiveController;
  StreamSubscription<dynamic>? _frameSubscription;

  /// ケーブルが抜かれたことを知るための購読。
  ///
  /// 映像やタッチのチャンネルは、抜かれても終わりを通知してくれない。
  /// 別に用意されている状態通知を見て、こちらから受信ストリームを畳む。
  StreamSubscription<({String state, String? detail})>? _stateSubscription;

  bool _connected = false;

  /// 切断されたときの理由。利用者に見せるために残す。
  String? disconnectReason;

  /// 接続相手（PC）の名乗り。接続後に埋まる。
  String? accessoryDescription;

  @override
  TransportType get type => TransportType.usb;

  @override
  int get estimatedBandwidthBps => _defaultBandwidthBps;

  /// 接続しているかどうか。
  bool get isConnected => _connected;

  // ─── 可用性 ───────────────────────────────────────────────────────────

  /// この端末が USB アクセサリーモードに対応しているか。
  static Future<bool> isSupported() async {
    try {
      return await _method.invokeMethod<bool>('isSupported') ?? false;
    } on PlatformException {
      return false;
    } on MissingPluginException {
      // Android 以外ではプラグインが無い
      return false;
    }
  }

  /// PC が USB で繋がっていて、アクセサリーとして見えているか。
  static Future<bool> isAttached() async {
    try {
      return await _method.invokeMethod<bool>('isAttached') ?? false;
    } on PlatformException {
      return false;
    } on MissingPluginException {
      return false;
    }
  }

  /// この端末の呼び名（例: "Google Pixel 6a"）。
  ///
  /// PC 側の一覧に出すために送る。「Android (USB)」のままだと、
  /// 複数台あるときにどれがどれだか分からない。
  static Future<String?> deviceName() async {
    try {
      return await _method.invokeMethod<String>('deviceName');
    } on PlatformException {
      return null;
    } on MissingPluginException {
      // Android 以外ではまだ用意していない
      return null;
    }
  }

  /// 状態通知の共有ストリーム。
  ///
  /// 一度だけ作って使い回す。
  ///
  /// `receiveBroadcastStream()` は呼ぶたびに別のストリームを作り、
  /// そのたびにネイティブ側へ購読を張り直す。ところが受け口は 1 つしか
  /// 無いので、あとから来たものが前のものを上書きし、
  /// **どれか 1 つが購読をやめた時点で受け口が null になる**。
  ///
  /// 探索画面と [AoaTransport] の両方が購読していたため、映像画面へ
  /// 引き継ぐときに探索画面側が解除された時点で通知が死んでいた。
  /// その結果、ケーブルを抜いても PC 側が落ちても何も起きず、
  /// 端末は固まったように見えていた。
  static Stream<({String state, String? detail})>? _sharedStates;

  /// 接続状態の変化を通知するストリーム。
  ///
  /// `state` は attached / connected / detached / error のいずれか。
  static Stream<({String state, String? detail})> stateChanges() {
    return _sharedStates ??= _states
        .receiveBroadcastStream()
        .map((dynamic event) {
          final map = (event as Map).cast<Object?, Object?>();
          return (
            state: map['state'] as String? ?? 'unknown',
            detail: map['detail'] as String?,
          );
        })
        .asBroadcastStream();
  }

  // ─── 接続 ─────────────────────────────────────────────────────────────

  /// USB で PC に接続する。
  ///
  /// [host] と [port] は使わない（USB は相手が一意に決まるため）。
  /// [Transport] の形を揃えるためだけに受け取る。
  @override
  Future<void> connect(String host, int port) async {
    if (_connected) return;

    final Map<Object?, Object?>? info;

    try {
      info = await _method.invokeMethod<Map<Object?, Object?>>('connect');
    } on PlatformException catch (e) {
      throw StateError('USB 接続に失敗しました: ${e.message ?? e.code}');
    } on MissingPluginException {
      throw StateError('この端末では USB 直結を利用できません。');
    }

    accessoryDescription = info == null
        ? null
        : '${info['manufacturer'] ?? '?'} / ${info['model'] ?? '?'}';

    _receiveController =
        StreamController<({ChannelId channel, Uint8List data})>.broadcast();

    _frameSubscription = _frames.receiveBroadcastStream().listen(
      _onFrame,
      onError: (Object error) => _receiveController?.addError(error),
      onDone: () => _receiveController?.close(),
    );

    // ケーブルが抜かれても、映像やタッチのチャンネルは終わりを通知してこない。
    // 状態通知を見て、こちらから受信ストリームを閉じる。
    // 閉じないと、利用者には「固まった」ようにしか見えない。
    _stateSubscription = stateChanges().listen(
      (event) {
        if (event.state != 'detached' && event.state != 'error') return;

        disconnectReason = event.detail;
        _connected = false;

        _receiveController?.close();
      },
      onError: (Object _) {},
    );

    _connected = true;
  }

  @override
  Future<void> disconnect() async {
    _connected = false;

    await _frameSubscription?.cancel();
    _frameSubscription = null;

    await _stateSubscription?.cancel();
    _stateSubscription = null;

    await _receiveController?.close();
    _receiveController = null;

    try {
      await _method.invokeMethod<void>('disconnect');
    } on PlatformException {
      // 既に抜かれている場合など。切断の失敗は無視してよい
    } on MissingPluginException {
      // 同上
    }
  }

  // ─── 送受信 ──────────────────────────────────────────────────────────

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    if (!_connected) {
      throw StateError('USB 接続が確立されていません。connect() を先に呼び出してください。');
    }

    try {
      await _method.invokeMethod<void>('send', <String, Object>{
        'channel': channel.index,
        'data': data,
      });
    } on PlatformException catch (e) {
      throw StateError('USB 送信に失敗しました: ${e.message ?? e.code}');
    }
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() {
    final controller = _receiveController;

    if (controller == null) {
      throw StateError('USB 接続が確立されていません。connect() を先に呼び出してください。');
    }

    return controller.stream;
  }

  // ─── 内部処理 ────────────────────────────────────────────────────────

  void _onFrame(dynamic event) {
    final map = (event as Map).cast<Object?, Object?>();

    final channelIndex = (map['channel'] as num?)?.toInt() ?? 0;
    final data = map['data'] as Uint8List?;

    if (data == null) return;

    final channel = ChannelId
        .values[channelIndex.clamp(0, ChannelId.values.length - 1)];

    _receiveController?.add((channel: channel, data: data));
  }
}
