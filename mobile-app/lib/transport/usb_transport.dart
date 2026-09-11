import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:shared_preferences/shared_preferences.dart';

import 'transport.dart';

/// USB トランスポートの実装（Android: ADB TCP フォワード経由）
///
/// Android デバイスでは ADB が PC 側の `adb reverse tcp:7979 tcp:7979` によって
/// ループバックポート 7979 をトンネルしている。
/// Flutter アプリは `localhost:7979` へのTLS接続で通信する。
///
/// iOS では libimobiledevice が同じポートでトンネルを提供する。
///
/// フレーム構造（WifiTransport と共通）:
/// ```
/// ┌─────────────────────────────────────────────┐
/// │ ChannelId (1 byte)                          │
/// │ PayloadLength (4 bytes, big-endian uint32)  │
/// │ Payload (PayloadLength bytes)               │
/// └─────────────────────────────────────────────┘
/// ```
class UsbTransport implements Transport {
  /// ADB フォワードで使用するポート番号（PC 側と一致させる）
  static const int _adbPort = 7979;

  /// フレームヘッダーサイズ
  static const int _frameHeaderSize = 5;

  static const int _maxPayloadSize = 32 * 1024 * 1024;

  /// USB 2.0 の推定帯域幅 (480 Mbps)
  static const int _defaultBandwidthBps = 480 * 1000 * 1000;

  Socket? _socket;
  StreamController<({ChannelId channel, Uint8List data})>? _receiveController;
  StreamSubscription<Uint8List>? _socketSubscription;
  final List<int> _receiveBuffer = [];

  int _estimatedBandwidth = _defaultBandwidthBps;
  int _totalBytesSent = 0;
  int? _connectTimeMs;

  @override
  TransportType get type => TransportType.usb;

  @override
  int get estimatedBandwidthBps => _estimatedBandwidth;

  // ─── 接続 ─────────────────────────────────────────────────────────────

  /// USB (ADB) 接続を確立する。
  ///
  /// [host] は通常 '127.0.0.1'（ループバック）、
  /// [port] は ADB フォワードで使用するポート（デフォルト 7979）。
  @override
  Future<void> connect(String host, int port) async {
    final connectHost = host.isEmpty ? '127.0.0.1' : host;
    final connectPort = port <= 0 ? _adbPort : port;

    final preferences = await SharedPreferences.getInstance();
    final pinKey = 'vmonitor.tls_pin.v1.usb:$connectPort';
    final trustedFingerprint = preferences.getString(pinKey);

    _socket = await SecureSocket.connect(
      connectHost,
      connectPort,
      timeout: const Duration(seconds: 10),
      onBadCertificate: (certificate) {
        return trustedFingerprint == null ||
            _certificateFingerprint(certificate) == trustedFingerprint;
      },
    );
    _socket!.setOption(SocketOption.tcpNoDelay, true);

    final certificate = (_socket as SecureSocket).peerCertificate;
    if (certificate == null) {
      await _socket?.close();
      _socket = null;
      throw const HandshakeException('PC のTLS証明書を取得できませんでした。');
    }

    final fingerprint = _certificateFingerprint(certificate);
    if (trustedFingerprint != null && fingerprint != trustedFingerprint) {
      await _socket?.close();
      _socket = null;
      throw const HandshakeException('PC のTLS証明書が前回の接続時と異なります。');
    }

    if (trustedFingerprint == null) {
      await preferences.setString(pinKey, fingerprint);
    }

    _connectTimeMs = DateTime.now().millisecondsSinceEpoch;

    _receiveController =
        StreamController<({ChannelId channel, Uint8List data})>();
    _socketSubscription = _socket!.cast<Uint8List>().listen(
          _onData,
          onDone: _onDone,
          onError: _onError,
        );
  }

  @override
  Future<void> disconnect() async {
    await _socketSubscription?.cancel();
    _socketSubscription = null;
    final controller = _receiveController;
    _receiveController = null;
    if (controller != null && !controller.isClosed) {
      unawaited(controller.close());
    }
    await _socket?.close();
    _socket = null;
    _receiveBuffer.clear();
  }

  // ─── 送受信 ──────────────────────────────────────────────────────────

  @override
  Future<void> send(Uint8List data, ChannelId channel) {
    // 前の書き込みが終わってから自分の番にする。
    //
    // 送信は待たずに呼ばれる。指を滑らせている間は連続するので、
    // 前の flush が終わらないうちに次の add が来る。IOSink は flush の
    // 最中に add されると StateError を投げ、その 1 通は送られない。
    //
    // 失敗しても列は続ける。1 通の失敗で以降が全部詰まってしまう。
    final result = _writeQueue.then((_) => _sendNow(data, channel));

    _writeQueue = result.catchError((Object _) {});

    return result;
  }

  Future<void> _writeQueue = Future<void>.value();

  Future<void> _sendNow(Uint8List data, ChannelId channel) async {
    _ensureConnected();

    final frame = _encodeFrame(data, channel);
    _socket!.add(frame);
    await _socket!.flush();

    _totalBytesSent += frame.length;
    _updateBandwidthEstimate();
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() {
    _ensureConnected();
    return _receiveController!.stream;
  }

  // ─── 内部処理 ────────────────────────────────────────────────────────

  static String _certificateFingerprint(X509Certificate certificate) =>
      certificate.sha1
          .map((byte) => byte.toRadixString(16).padLeft(2, '0'))
          .join();

  static Uint8List _encodeFrame(Uint8List payload, ChannelId channel) {
    if (payload.length > _maxPayloadSize) {
      throw ArgumentError.value(
        payload.length,
        'payload.length',
        '$_maxPayloadSize bytes 以下である必要があります',
      );
    }

    final frame = Uint8List(_frameHeaderSize + payload.length);
    frame[0] = channel.index;
    final bd = ByteData.sublistView(frame, 1, 5);
    bd.setUint32(0, payload.length, Endian.big);
    frame.setRange(_frameHeaderSize, frame.length, payload);
    return frame;
  }

  void _onData(Uint8List chunk) {
    _receiveBuffer.addAll(chunk);
    _drainFrames();
  }

  void _drainFrames() {
    while (_receiveBuffer.length >= _frameHeaderSize) {
      final channelByte = _receiveBuffer[0];
      final payloadLength = (_receiveBuffer[1] << 24) |
          (_receiveBuffer[2] << 16) |
          (_receiveBuffer[3] << 8) |
          _receiveBuffer[4];

      if (payloadLength > _maxPayloadSize) {
        _receiveBuffer.clear();
        _receiveController?.addError(
          FormatException('受信ペイロードが上限を超えています: $payloadLength bytes'),
        );
        unawaited(disconnect());
        return;
      }

      final totalSize = _frameHeaderSize + payloadLength;
      if (_receiveBuffer.length < totalSize) break;

      if (channelByte >= ChannelId.values.length) {
        _receiveBuffer.removeRange(0, totalSize);
        continue;
      }

      final channelId = ChannelId.values[channelByte];
      final payload = Uint8List.fromList(
          _receiveBuffer.sublist(_frameHeaderSize, totalSize));
      _receiveBuffer.removeRange(0, totalSize);
      _receiveController?.add((channel: channelId, data: payload));
    }
  }

  void _onDone() => _receiveController?.close();

  void _onError(Object error) => _receiveController?.addError(error);

  void _ensureConnected() {
    if (_socket == null) {
      throw StateError('USB 接続が確立されていません。connect() を先に呼び出してください。');
    }
  }

  void _updateBandwidthEstimate() {
    final startMs = _connectTimeMs;
    if (startMs == null) return;
    final elapsedMs = DateTime.now().millisecondsSinceEpoch - startMs;
    if (elapsedMs > 0) {
      _estimatedBandwidth = _totalBytesSent * 8 * 1000 ~/ elapsedMs;
    }
  }
}
