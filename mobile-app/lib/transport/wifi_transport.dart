import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:multicast_dns/multicast_dns.dart';

import 'transport.dart';

/// Wi-Fi (TCP) トランスポートの実装。
/// 開発段階では TLS なし素 TCP を使用する。
///
/// フレーム構造（送受信共通）:
/// ```
/// ┌─────────────────────────────────────────────┐
/// │ ChannelId (1 byte)                          │
/// │ PayloadLength (4 bytes, big-endian uint32)  │
/// │ Payload (PayloadLength bytes)               │
/// └─────────────────────────────────────────────┘
/// ```
class WifiTransport implements Transport {
  static const int _frameHeaderSize = 5;
  static const int _defaultBandwidthBps = 10 * 1000 * 1000;

  Socket? _socket;

  int _totalBytesSent = 0;
  int? _sendStartMs;
  int _estimatedBandwidthBps = _defaultBandwidthBps;

  StreamController<({ChannelId channel, Uint8List data})>? _receiveController;
  StreamSubscription<Uint8List>? _socketSubscription;
  final List<int> _receiveBuffer = [];

  @override
  TransportType get type => TransportType.wifi;

  @override
  int get estimatedBandwidthBps => _estimatedBandwidthBps;

  // ─────────────────────────────────────────────
  // 接続・切断
  // ─────────────────────────────────────────────

  /// 指定ホスト・ポートへ素の TCP で接続する（開発用）。
  @override
  Future<void> connect(String host, int port) async {
    _socket = await Socket.connect(
      host,
      port,
      timeout: const Duration(seconds: 10),
    );
    _socket!.setOption(SocketOption.tcpNoDelay, true);

    _sendStartMs = DateTime.now().millisecondsSinceEpoch;

    // 受信ループの開始
    // broadcast() ではなく通常の StreamController を使用する（データロストを防ぐ）
    _receiveController =
        StreamController<({ChannelId channel, Uint8List data})>();
    _socketSubscription = _socket!.cast<Uint8List>().listen(
      _onData,
      onDone: _onDone,
      onError: _onError,
      cancelOnError: false, // エラー時も受信を継続する
    );
  }

  @override
  Future<void> disconnect() async {
    await _socketSubscription?.cancel();
    _socketSubscription = null;

    // close() は待たない。
    //
    // 購読されていない StreamController の close() が返す Future は、
    // 誰かが listen するまで完了しない。繋いだ直後に切った場合は
    // まだ誰も受信していないので、待つと切断処理ごと止まる。
    final controller = _receiveController;
    _receiveController = null;
    if (controller != null && !controller.isClosed) unawaited(controller.close());

    await _socket?.close();
    _socket = null;

    _receiveBuffer.clear();
  }

  // ─────────────────────────────────────────────
  // 送受信
  // ─────────────────────────────────────────────

  /// フレームをエンコードして TLS ソケットに書き込む。
  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    _ensureConnected();

    final frame = _encodeFrame(data, channel);
    _socket!.add(frame);
    await _socket!.flush();

    _updateBandwidthEstimate(frame.length);
  }

  /// 受信データを (ChannelId, Uint8List) ペアのストリームとして返す。
  @override
  Stream<({ChannelId channel, Uint8List data})> receive() {
    _ensureConnected();
    return _receiveController!.stream;
  }

  // ─────────────────────────────────────────────
  // mDNS 探索
  // ─────────────────────────────────────────────

  /// _vmonitor._tcp mDNS サービスを探索して接続候補リストを返す。
  /// multicast_dns パッケージを使って実際のネットワーク探索を行う。
  static Future<List<MdnsServiceRecord>> discoverServices({
    Duration timeout = const Duration(seconds: 5),
  }) async {
    final results = <MdnsServiceRecord>[];
    const serviceType = '_vmonitor._tcp';

    MDnsClient? client;
    try {
      client = MDnsClient();
      await client.start();

      // PTR レコードでサービスインスタンスを列挙する
      await for (final PtrResourceRecord ptr in client
          .lookup<PtrResourceRecord>(
            ResourceRecordQuery.serverPointer(serviceType),
          )
          .timeout(timeout, onTimeout: (sink) => sink.close())) {
        final String instanceName = ptr.domainName;

        // SRV レコードでホスト名とポートを取得する
        await for (final SrvResourceRecord srv in client
            .lookup<SrvResourceRecord>(
              ResourceRecordQuery.service(instanceName),
            )
            .timeout(const Duration(seconds: 2), onTimeout: (sink) => sink.close())) {

          // A レコードで IP アドレスを取得する
          String? ipAddress;
          await for (final IPAddressResourceRecord ip in client
              .lookup<IPAddressResourceRecord>(
                ResourceRecordQuery.addressIPv4(srv.target),
              )
              .timeout(const Duration(seconds: 2), onTimeout: (sink) => sink.close())) {
            ipAddress = ip.address.address;
            break;
          }

          ipAddress ??= srv.target;

          results.add(MdnsServiceRecord(
            serviceName: instanceName,
            hostName: srv.target,
            port: srv.port,
            ipAddress: ipAddress,
          ));
          break;
        }
      }
    } catch (e) {
      // mDNS 探索失敗（ネットワーク到達不能など）は無視して空リストを返す
      // エラー例: SocketException (errno 101: Network is unreachable)
    } finally {
      try {
        client?.stop();
      } catch (_) {
        // stop() 失敗も無視
      }
    }

    return results;
  }

  // ─────────────────────────────────────────────
  // 内部実装
  // ─────────────────────────────────────────────

  /// フレームエンコード: ChannelId(1) + Length(4 BE) + Payload。
  static Uint8List _encodeFrame(Uint8List payload, ChannelId channel) {
    final frame = Uint8List(_frameHeaderSize + payload.length);
    frame[0] = channel.index;

    final bd = ByteData.sublistView(frame, 1, 5);
    bd.setUint32(0, payload.length, Endian.big);

    frame.setRange(_frameHeaderSize, frame.length, payload);
    return frame;
  }

  /// 受信データをバッファに積んでフレーム単位でデコードする。
  void _onData(Uint8List chunk) {
    // ignore: avoid_print
    print('[WifiTransport] onData: ${chunk.length} bytes');
    _receiveBuffer.addAll(chunk);
    _drainFrames();
  }

  /// バッファから完全なフレームを取り出し、受信コントローラに流す。
  void _drainFrames() {
    while (_receiveBuffer.length >= _frameHeaderSize) {
      final channelByte = _receiveBuffer[0];
      final payloadLength = (_receiveBuffer[1] << 24) |
          (_receiveBuffer[2] << 16) |
          (_receiveBuffer[3] << 8) |
          _receiveBuffer[4];

      final totalFrameSize = _frameHeaderSize + payloadLength;
      if (_receiveBuffer.length < totalFrameSize) break; // データが足りない

      final channelId = ChannelId.values[channelByte];
      final payload =
          Uint8List.fromList(_receiveBuffer.sublist(_frameHeaderSize, totalFrameSize));

      _receiveBuffer.removeRange(0, totalFrameSize);
      _receiveController?.add((channel: channelId, data: payload));
    }
  }

  void _onDone() {
    _receiveController?.close();
  }

  void _onError(Object error) {
    // エラーをデバッグ出力する
    // ignore: avoid_print
    print('[WifiTransport] Socket error: $error');
    _receiveController?.addError(error);
  }

  void _ensureConnected() {
    if (_socket == null) {
      throw StateError('接続が確立されていません。connect() を先に呼び出してください。');
    }
  }

  /// 送受信バイト数をもとに帯域を推定する。
  void _updateBandwidthEstimate(int bytesSent) {
    _totalBytesSent += bytesSent;
    final startMs = _sendStartMs;
    if (startMs != null) {
      final elapsedMs = DateTime.now().millisecondsSinceEpoch - startMs;
      if (elapsedMs > 0) {
        _estimatedBandwidthBps = _totalBytesSent * 8 * 1000 ~/ elapsedMs;
      }
    }
  }
}

/// mDNS で発見されたサービスレコード。
class MdnsServiceRecord {
  /// サービスのインスタンス名。
  final String serviceName;

  /// ホスト名（例: "MyPC.local"）。
  final String hostName;

  /// ポート番号。
  final int port;

  /// 解決済み IP アドレス文字列。
  final String ipAddress;

  const MdnsServiceRecord({
    required this.serviceName,
    required this.hostName,
    required this.port,
    required this.ipAddress,
  });

  @override
  String toString() =>
      'MdnsServiceRecord(name: $serviceName, host: $hostName, port: $port, ip: $ipAddress)';
}
