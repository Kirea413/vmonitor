// 戻る操作で映像画面が閉じないことを確かめる。
//
// iOS は左端から右へ払うと前の画面に戻る。Android も戻る操作で同じ。
// 画面全体が PC への入力面なので、端をなぞるだけで意図せず切れ、
// 「セッションを確立しています」に戻ってしまう。

import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:vmonitor/transport/transport.dart';
import 'package:vmonitor/transport/wifi_transport.dart' show MdnsServiceRecord;
import 'package:vmonitor/ui/video_display_screen.dart';

/// 何もしないトランスポート。画面を立ち上げるためだけに使う。
class IdleTransport implements Transport {
  final _received = StreamController<({ChannelId channel, Uint8List data})>();

  @override
  TransportType get type => TransportType.wifi;

  @override
  int get estimatedBandwidthBps => 10 * 1000 * 1000;

  @override
  Future<void> connect(String host, int port) async {}

  @override
  Future<void> disconnect() async {}

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() => _received.stream;

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {}
}

void main() {
  testWidgets('戻る操作では映像画面が閉じない', (tester) async {
    final transport = IdleTransport();

    const device = MdnsServiceRecord(
      serviceName: 'test',
      hostName: 'test.local',
      port: 7979,
      ipAddress: '127.0.0.1',
    );

    late BuildContext shellContext;

    await tester.pumpWidget(MaterialApp(
      home: Builder(builder: (context) {
        shellContext = context;
        return const Scaffold(body: Text('ホーム'));
      }),
    ));

    unawaited(Navigator.of(shellContext).push(
      MaterialPageRoute<void>(
        builder: (_) =>
            VideoDisplayScreen(device: device, transport: transport),
      ),
    ));

    // 遷移が終わるまで進める
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(find.byType(VideoDisplayScreen), findsOneWidget);

    // 端をなぞる操作も、Android の戻るも、ここを通る。
    //
    // 返り値は「要求を受け止めたか」なので、閉じなくても true になる。
    // 見るべきは画面が残っているかどうか。
    await Navigator.of(shellContext).maybePop();

    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));

    expect(find.byType(VideoDisplayScreen), findsOneWidget);
  });
}
