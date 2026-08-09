import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'transport.dart';

/// USB トランスポートの実装（Android: ADB TCP フォワード経由）
///
/// Android デバイスでは ADB が PC 側の `adb forward tcp:7979 tcp:7979` によって
/// ループバックポート 7979 をトンネルしている。
/// Flutter アプリは `localhost:7979` への通常 TCP 接続で通信する。
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

    _socket = await Socket.connect(
      connectHost,
      connectPort,
      timeout: const Duration(seconds: 10),
    );
    _socket!.setOption(SocketOption.tcpNoDelay, true);
    _connectTimeMs = DateTime.now().millisecondsSinceEpoch;

    _receiveController =
        StreamController<({ChannelId channel, Uint8List data})>.broadcast();
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
    await _receiveController?.close();
    _receiveController = null;
    await _socket?.close();
    _socket = null;
    _receiveBuffer.clear();
  }

  // ─── 送受信 ──────────────────────────────────────────────────────────

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
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

  static Uint8List _encodeFrame(Uint8List payload, ChannelId channel) {
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

      final totalSize = _frameHeaderSize + payloadLength;
      if (_receiveBuffer.length < totalSize) break;

      final channelId = ChannelId.values[channelByte.clamp(0, ChannelId.values.length - 1)];
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
      throw StateError(
          'USB 接続が確立されていません。connect() を先に呼び出してください。');
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
