// 「離した」が確実に、そして遅れずに送り出されることを確かめる。
//
// 実機で二段階の不具合が出た。
//
// 一段目。送信は完了を待たずに呼ばれるため、指を滑らせている間は
// 前の flush の最中に次の add が来て、Dart の IOSink が
// 「StreamSink is bound to a stream」を投げていた。握り潰していたので
// その 1 通が黙って消える。落ちるのはストローク最後の「離した」で、
// PC 側は時間切れまで押されたままになり、長押しになってしまう。
//
// 二段目。書き込みを直列化したら消えなくなった代わりに、順番待ちが
// 生まれた。動いた知らせが積まれていると「離した」はその後ろに並ぶ。
// 溜まり具合で待ち時間が変わり、「タイミングによって離れるのが遅い」
// という形で出る。

import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/gestures.dart';
import 'package:flutter/widgets.dart' hide Orientation;
import 'package:flutter_test/flutter_test.dart';

import 'package:vmonitor/touch/touch_input_proxy.dart';
import 'package:vmonitor/transport/transport.dart';

/// 送るのに時間がかかるトランスポート。
///
/// 実機では 1 通ごとにネットワークの往復が挟まる。すぐ返ってくる
/// 作りにすると順番待ちが起きず、遅れを再現できない。
class SlowTransport implements Transport {
  SlowTransport(this.delay);

  final Duration delay;
  final List<Uint8List> sent = [];

  @override
  TransportType get type => TransportType.wifi;

  @override
  int get estimatedBandwidthBps => 10 * 1000 * 1000;

  @override
  Future<void> connect(String host, int port) async {}

  @override
  Future<void> disconnect() async {}

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() =>
      const Stream.empty();

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    await Future<void>.delayed(delay);
    sent.add(data);
  }
}

/// ペイロードから、含まれているフェーズを読む。
///
/// 形式: timestamp 8 + orientation 1 + count 1、以降 1 点 18 バイトで
/// 16 バイト目がフェーズ。
List<int> phasesOf(Uint8List payload) {
  final count = payload[9];

  return [
    for (var i = 0; i < count; i++) payload[10 + i * 18 + 16],
  ];
}

const int phaseBegan = 0;
const int phaseMoved = 1;
const int phaseEnded = 2;

void main() {
  late FlutterTouchInputProxy proxy;
  late SlowTransport transport;

  setUp(() {
    proxy = FlutterTouchInputProxy();
    transport = SlowTransport(const Duration(milliseconds: 5));
    proxy.attach(transport);
    proxy.updateSize(const Size(1000, 1000));
  });

  PointerDownEvent down(int id, Offset at) =>
      PointerDownEvent(pointer: id, position: at);

  PointerMoveEvent move(int id, Offset at) =>
      PointerMoveEvent(pointer: id, position: at);

  PointerUpEvent up(int id, Offset at) =>
      PointerUpEvent(pointer: id, position: at);

  test('滑らせてから離すと、最後に届くのは「離した」', () async {
    proxy.onPointerDown(down(1, const Offset(10, 10)));

    for (var i = 0; i < 60; i++) {
      proxy.onPointerMove(move(1, Offset(10.0 + i, 10)));
    }

    proxy.onPointerUp(up(1, const Offset(70, 10)));

    // 列が捌けるまで待つ
    await Future<void>.delayed(const Duration(milliseconds: 600));

    expect(transport.sent, isNotEmpty);
    expect(phasesOf(transport.sent.last), contains(phaseEnded));
  });

  test('動いた知らせは間引かれるが、押した・離したは残る', () async {
    proxy.onPointerDown(down(1, const Offset(10, 10)));

    for (var i = 0; i < 60; i++) {
      proxy.onPointerMove(move(1, Offset(10.0 + i, 10)));
    }

    proxy.onPointerUp(up(1, const Offset(70, 10)));

    await Future<void>.delayed(const Duration(milliseconds: 600));

    final phases = transport.sent.expand(phasesOf).toList();

    // 押したと離したは必ず通っていること
    expect(phases, contains(phaseBegan));
    expect(phases, contains(phaseEnded));

    // 動いた知らせは、送れるぶんだけでよい。60 件すべてを順番に
    // 送っていると、その後ろに並ぶ「離した」がそのぶん遅れる。
    final moved = phases.where((p) => p == phaseMoved).length;
    expect(moved, lessThan(60));
  });

  test('離したあとに押し直しても、送り直しで消されない', () async {
    proxy.onPointerDown(down(1, const Offset(10, 10)));
    proxy.onPointerUp(up(1, const Offset(10, 10)));

    // 送り直しは 60ms と 200ms 後。その間に同じ番号で押し直す。
    await Future<void>.delayed(const Duration(milliseconds: 20));
    proxy.onPointerDown(down(1, const Offset(500, 500)));

    await Future<void>.delayed(const Duration(milliseconds: 400));

    // 最後が「離した」だと、始めたばかりの線が消える
    expect(phasesOf(transport.sent.last), isNot(contains(phaseEnded)));
  });
}
