import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'transport.dart';

/// PC からの接続を待ち受ける Wi-Fi トランスポート。
///
/// [WifiTransport] とは向きが逆で、こちらが待ち、PC が繋いでくる。
///
/// 繋がってしまえば通信の中身は同じ（同じフレーム形式、同じチャンネル）なので、
/// [VideoDisplayScreen] から見た扱いは変わらない。
///
/// フレーム構造（送受信共通）:
/// ```
/// ┌─────────────────────────────────────────────┐
/// │ ChannelId (1 byte)                          │
/// │ PayloadLength (4 bytes, big-endian uint32)  │
/// │ Payload (PayloadLength bytes)               │
/// └─────────────────────────────────────────────┘
/// ```
class WifiListenTransport implements Transport {
  /// 待ち受けに使う既定のポート。
  ///
  /// PC 側の 7979 とは別にする。同じ端末で両方向を同時に使うことは無いが、
  /// 番号が同じだと、ログを見たときにどちら向きの接続なのか分からなくなる。
  static const int defaultPort = 7980;

  static const int _frameHeaderSize = 5;
  static const int _defaultBandwidthBps = 10 * 1000 * 1000;

  ServerSocket? _server;
  Socket? _socket;

  int _totalBytesSent = 0;
  int? _sendStartMs;
  int _estimatedBandwidthBps = _defaultBandwidthBps;

  StreamController<({ChannelId channel, Uint8List data})>? _receiveController;
  StreamSubscription<Uint8List>? _socketSubscription;
  StreamSubscription<Socket>? _serverSubscription;
  final List<int> _receiveBuffer = [];

  /// 最初の接続を受け取るための待ち合わせ。
  ///
  /// 相手が来ないまま畳んだ場合は null で完了させる。
  /// 例外にすると、待っている人がいないときに未処理のエラーとして表に出てしまう。
  Completer<String?>? _accepted;

  @override
  TransportType get type => TransportType.wifi;

  @override
  int get estimatedBandwidthBps => _estimatedBandwidthBps;

  /// 待ち受けているポート番号。待ち受けていなければ null。
  int? get listeningPort => _server?.port;

  /// 相手が繋がっているか。
  bool get isConnected => _socket != null;

  // ─────────────────────────────────────────────
  // 待ち受け
  // ─────────────────────────────────────────────

  /// 待ち受けを始める。相手が来るのは待たない。
  ///
  /// 繋がる前から自分の IP アドレスとポートを画面に出したいので、
  /// 受け入れとは分けてある。
  Future<void> startListening({int port = defaultPort}) async {
    if (_server != null) return;

    _server = await ServerSocket.bind(InternetAddress.anyIPv4, port);
    _accepted = Completer<String?>();

    _serverSubscription = _server!.listen(
      _onClient,
      // 待ち受けが壊れたら、もう相手は来ない。待っている人を起こす。
      onError: (Object _) => _finishAccept(null),
    );
  }

  /// 最初に繋いできた相手を受け入れる。相手の IP アドレスを返す。
  ///
  /// 相手が来ないまま待ち受けを畳んだ場合は null。
  /// [startListening] を呼んでいなければ、ここで始める。
  Future<String?> acceptOne({int port = defaultPort}) async {
    await startListening(port: port);
    return _accepted!.future;
  }

  /// 受け入れ待ちを終わらせる。既に終わっていれば何もしない。
  void _finishAccept(String? remote) {
    final accepted = _accepted;
    if (accepted == null || accepted.isCompleted) return;

    accepted.complete(remote);
  }

  /// 待ち受けだけをやめる。既に繋がっている相手はそのまま。
  Future<void> stopListening() async {
    await _serverSubscription?.cancel();
    _serverSubscription = null;

    await _server?.close();
    _server = null;

    // 待っている人がいれば、来ないことを伝えて起こす
    _finishAccept(null);
    _accepted = null;
  }

  void _onClient(Socket socket) {
    // 2 台目以降は受け付けない。
    // 1 台の PC を 1 画面に映すための仕組みなので、
    // 途中で別の PC に乗っ取られると何が映っているのか分からなくなる。
    if (_socket != null) {
      socket.destroy();
      return;
    }

    socket.setOption(SocketOption.tcpNoDelay, true);
    _socket = socket;
    _sendStartMs = DateTime.now().millisecondsSinceEpoch;

    _receiveController =
        StreamController<({ChannelId channel, Uint8List data})>();
    _socketSubscription = socket.cast<Uint8List>().listen(
          _onData,
          onDone: _onDone,
          onError: _onError,
          cancelOnError: false,
        );

    // 相手が決まったら、もう待ち受ける必要はない。
    // 開けたままにすると、別の PC が繋いできたときに黙って切ることになる。
    unawaited(stopListeningKeepingClient());

    _finishAccept(socket.remoteAddress.address);
  }

  /// 受け入れた相手はそのままに、待ち受けだけ閉じる。
  Future<void> stopListeningKeepingClient() async {
    await _serverSubscription?.cancel();
    _serverSubscription = null;

    await _server?.close();
    _server = null;
  }

  // ─────────────────────────────────────────────
  // Transport
  // ─────────────────────────────────────────────

  /// 待ち受けて、最初に繋いできた相手を受け入れる。
  ///
  /// [host] は使わない（誰から繋がれるか分からないため）。
  /// [Transport] の形に合わせるためだけに受け取っている。
  @override
  Future<void> connect(String host, int port) async {
    final remote = await acceptOne(port: port == 0 ? defaultPort : port);

    if (remote == null) {
      throw StateError('待ち受けを終了したため接続できませんでした。');
    }
  }

  @override
  Future<void> disconnect() async {
    await stopListening();

    await _socketSubscription?.cancel();
    _socketSubscription = null;

    // close() を待ってはいけない。
    //
    // 購読されていない StreamController の close() が返す Future は、
    // 誰かが listen して流し切るまで完了しない。映像画面に渡す前に切った場合
    // （待ち受けだけして繋がらなかった、渡す前に画面を離れた）は
    // 購読者がいないので、待つと永久に返ってこない。
    final controller = _receiveController;
    _receiveController = null;
    if (controller != null && !controller.isClosed) unawaited(controller.close());

    _socket?.destroy();
    _socket = null;

    _receiveBuffer.clear();
  }

  /// 書き込みの順番待ち。
  ///
  /// 送信は待たずに呼ばれる。指を滑らせている間は 120Hz で連なるので、
  /// 前の flush が終わらないうちに次の add が来る。IOSink は flush の
  /// 最中に add されると StateError を投げ、その 1 通は送られない。
  ///
  /// 実機では「ドラッグしたときだけ、離したのが PC に届かない」という
  /// 形で出た。タップは書き込みが重ならないので通る。落ちるのは
  /// ストローク最後の 1 通で、PC 側は時間切れまで押されたままになり、
  /// 長押しになってしまう。
  Future<void> _writeQueue = Future<void>.value();

  @override
  Future<void> send(Uint8List data, ChannelId channel) {
    // 前の書き込みが終わってから自分の番にする。
    // 失敗しても列は続ける。1 通の失敗で以降が全部詰まってしまう。
    final result = _writeQueue.then((_) => _sendNow(data, channel));

    _writeQueue = result.catchError((Object _) {});

    return result;
  }

  Future<void> _sendNow(Uint8List data, ChannelId channel) async {
    final socket = _socket;
    if (socket == null) {
      throw StateError('まだ PC が接続していません。');
    }

    final frame = _encodeFrame(data, channel);
    socket.add(frame);
    await socket.flush();

    _updateBandwidthEstimate(frame.length);
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() {
    final controller = _receiveController;
    if (controller == null) {
      throw StateError('まだ PC が接続していません。');
    }
    return controller.stream;
  }

  // ─────────────────────────────────────────────
  // 自機のアドレス
  // ─────────────────────────────────────────────

  /// この端末が持っている IPv4 アドレスを返す。
  ///
  /// PC 側で入力してもらう値なので、画面に出す必要がある。
  /// ループバックは相手から見えないので外す。
  static Future<List<String>> localAddresses() async {
    try {
      final interfaces = await NetworkInterface.list(
        type: InternetAddressType.IPv4,
        includeLoopback: false,
        includeLinkLocal: false,
      );

      return [
        for (final interface in interfaces)
          for (final address in interface.addresses) address.address,
      ];
    } catch (_) {
      // 権限やプラットフォームの都合で列挙できないことがある。
      // 手動で調べてもらうしかないので、空で返す。
      return const [];
    }
  }

  // ─────────────────────────────────────────────
  // 内部実装
  // ─────────────────────────────────────────────

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

      final totalFrameSize = _frameHeaderSize + payloadLength;
      if (_receiveBuffer.length < totalFrameSize) break; // データが足りない

      // 知らないチャンネル番号で落とさない。
      // 相手の版が新しければ、こちらが知らない種類が混ざりうる。
      if (channelByte >= ChannelId.values.length) {
        _receiveBuffer.removeRange(0, totalFrameSize);
        continue;
      }

      final channelId = ChannelId.values[channelByte];
      final payload = Uint8List.fromList(
          _receiveBuffer.sublist(_frameHeaderSize, totalFrameSize));

      _receiveBuffer.removeRange(0, totalFrameSize);
      _receiveController?.add((channel: channelId, data: payload));
    }
  }

  void _onDone() {
    _receiveController?.close();
  }

  void _onError(Object error) {
    _receiveController?.addError(error);
  }

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
