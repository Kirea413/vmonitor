import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:vmonitor/transport/relay_transport.dart';
import 'package:vmonitor/transport/transport.dart';

/// 普通の（broadcast でない）StreamController で受信を作るトランスポート。
/// Wi-Fi 側と同じ性質を持たせてある。
class _FakeTransport implements Transport {
  final _controller = StreamController<({ChannelId channel, Uint8List data})>();

  final List<({Uint8List data, ChannelId channel})> sent = [];
  bool disconnected = false;

  void emit(ChannelId channel, List<int> bytes) {
    _controller.add((channel: channel, data: Uint8List.fromList(bytes)));
  }

  @override
  TransportType get type => TransportType.wifi;

  @override
  int get estimatedBandwidthBps => 1234;

  @override
  Future<void> connect(String host, int port) async {}

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    sent.add((data: data, channel: channel));
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() => _controller.stream;

  @override
  Future<void> disconnect() async {
    disconnected = true;

    // close() は待たない。
    //
    // 購読されていない非 broadcast の StreamController を close すると、
    // その Future は誰かが listen するまで完了しない。待つと切断処理ごと
    // 止まる。実物のトランスポートも同じ理由で待っていない。
    if (!_controller.isClosed) unawaited(_controller.close());
  }
}

void main() {
  group('RelayTransport', () {
    test('包まないと二度目の listen は失敗する（前提の確認）', () {
      final inner = _FakeTransport();

      inner.receive().listen((_) {});

      expect(() => inner.receive().listen((_) {}), throwsStateError);
    });

    test('購読を引き継いでも二度目の listen ができる', () async {
      final relay = RelayTransport(_FakeTransport());

      final first = relay.receive().listen((_) {});
      await first.cancel();

      // ここが本来 Bad state: Stream has already been listened to だった
      expect(() => relay.receive().listen((_) {}), returnsNormally);
    });

    test('引き継ぎの間に届いたものを落とさない', () async {
      final inner = _FakeTransport();
      final relay = RelayTransport(inner);

      final received = <List<int>>[];

      final first = relay.receive().listen((e) => received.add(e.data));
      inner.emit(ChannelId.control, [1]);
      await pumpEventQueue();

      // 待機画面が手を離す
      await first.cancel();

      // 映像画面が出来上がるまでの間に届く。ここを捨てると
      // 先頭のキーフレームが失われ、何も映らなくなる。
      inner.emit(ChannelId.video, [2]);
      inner.emit(ChannelId.video, [3]);
      await pumpEventQueue();

      relay.receive().listen((e) => received.add(e.data));
      await pumpEventQueue();

      expect(received, [
        [1],
        [2],
        [3],
      ]);
    });

    test('聞き手が付かないまま溢れたら、映像から捨てて制御は残す', () async {
      final inner = _FakeTransport();
      final relay = RelayTransport(inner);

      // 一度購読してから離し、預かりが積もる状態にする
      final first = relay.receive().listen((_) {});
      await first.cancel();

      inner.emit(ChannelId.control, [0xC0]);
      for (int i = 0; i < 800; i++) {
        inner.emit(ChannelId.video, [i & 0xFF]);
      }
      await pumpEventQueue();

      final received = <({ChannelId channel, Uint8List data})>[];
      relay.receive().listen(received.add);
      await pumpEventQueue();

      expect(received.length, lessThanOrEqualTo(601));

      // 制御は生き残っている
      expect(
        received.any((e) => e.channel == ChannelId.control),
        isTrue,
        reason: '制御を捨てると接続そのものが成立しなくなる',
      );
    });

    test('送信と切断は元のトランスポートへ通す', () async {
      final inner = _FakeTransport();
      final relay = RelayTransport(inner);

      await relay.send(Uint8List.fromList([9]), ChannelId.touch);
      expect(inner.sent.single.channel, ChannelId.touch);

      expect(relay.type, TransportType.wifi);
      expect(relay.estimatedBandwidthBps, 1234);

      await relay.disconnect();
      expect(inner.disconnected, isTrue);
    });
  });
}
