import 'dart:async';
import 'dart:typed_data';

import 'transport.dart';

/// 受信の購読を、取りこぼさずに引き継げるようにする包み。
///
/// ## なぜ要るか
///
/// 接続の承認は待機画面で行い、そのあと同じ通信路のまま映像画面へ渡す。
/// つまり `receive()` を 2 回、別々の相手が呼ぶ。
///
/// ところが Wi-Fi の受信は普通の [StreamController] で作られていて、
/// 一度 listen したら二度目は
///
///   Bad state: Stream has already been listened to.
///
/// になる。購読を cancel しても同じで、張り直しはできない。
/// USB (AOA) は broadcast で作られていたため、ここだけ表に出なかった。
///
/// ## broadcast にしない理由
///
/// 受信側を broadcast に変えるのが手っ取り早いが、それをやると
/// 聞き手が居ない間に届いたものが捨てられる。承認から映像画面が
/// 出来上がるまでには数百ミリ秒あり、その間に来た映像の先頭
/// （SPS/PPS を含むキーフレーム）を落とすと、次のキーフレームまで
/// 何も映らない。真っ暗な画面の作り方としては十分すぎる。
///
/// そこで、聞き手が居ない間は貯めておき、次の聞き手が付いた時点で
/// まとめて流す。
class RelayTransport implements Transport {
  RelayTransport(this._inner);

  final Transport _inner;

  /// 元の受信への購読。1 本だけ張る。
  StreamSubscription<({ChannelId channel, Uint8List data})>? _subscription;

  /// いま配っている先。聞き手が居なければ null 扱い。
  StreamController<({ChannelId channel, Uint8List data})>? _current;

  /// 聞き手が居ない間の預かり。
  final List<({ChannelId channel, Uint8List data})> _pending = [];

  /// 預かりの上限。
  ///
  /// 誰も聞かないまま流れ続けると、際限なく積もって落ちる。
  /// 溢れたら古い映像から捨てる。制御は数も少なく、落とすと
  /// 接続そのものが成立しないので残す。
  static const int _maxPending = 600;

  bool _closed = false;

  @override
  TransportType get type => _inner.type;

  @override
  int get estimatedBandwidthBps => _inner.estimatedBandwidthBps;

  @override
  Future<void> connect(String host, int port) => _inner.connect(host, port);

  @override
  Future<void> send(Uint8List data, ChannelId channel) =>
      _inner.send(data, channel);

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() {
    _subscription ??= _inner.receive().listen(
          _onEvent,
          onError: _onError,
          onDone: _onDone,
        );

    // 前の聞き手は自分で cancel 済みのはず。
    // ここで close はしない。まだ購読が生きていた場合に onDone が飛び、
    // 受け取った側が「相手が切れた」と誤解する。
    final controller =
        StreamController<({ChannelId channel, Uint8List data})>();

    controller.onListen = () {
      for (final event in _pending) {
        controller.add(event);
      }
      _pending.clear();
    };

    _current = controller;

    return controller.stream;
  }

  @override
  Future<void> disconnect() async {
    _closed = true;

    await _subscription?.cancel();
    _subscription = null;

    _pending.clear();

    final controller = _current;
    _current = null;
    if (controller != null && !controller.isClosed) {
      unawaited(controller.close());
    }

    await _inner.disconnect();
  }

  // ── 内部 ─────────────────────────────────────────────────────────────

  void _onEvent(({ChannelId channel, Uint8List data}) event) {
    if (_closed) return;

    final controller = _current;

    if (controller != null && !controller.isClosed && controller.hasListener) {
      controller.add(event);
      return;
    }

    _pending.add(event);

    if (_pending.length <= _maxPending) return;

    // 溢れた。まず映像から捨てる。
    final index = _pending.indexWhere((e) => e.channel == ChannelId.video);
    _pending.removeAt(index >= 0 ? index : 0);
  }

  void _onError(Object error, StackTrace stackTrace) {
    final controller = _current;
    if (controller != null && !controller.isClosed) {
      controller.addError(error, stackTrace);
    }
  }

  void _onDone() {
    final controller = _current;
    if (controller != null && !controller.isClosed) {
      unawaited(controller.close());
    }
  }
}
