import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:vmonitor/transport/transport.dart';
import 'package:vmonitor/transport/wifi_listen_transport.dart';

/// PC から繋いでもらう向きのトランスポートを、ループバックで確かめる。
///
/// 実機を出さずに確かめられるのはここまでだが、
/// フレームの組み立て・切り出しと待ち受けの畳み方は実際の通信で決まるので、
/// 手で追うより繋いで確かめた方が確実。
void main() {
  /// ヘッダーを組み立てる（PC 側と同じ形式）。
  Uint8List frame(ChannelId channel, List<int> payload) {
    final bytes = BytesBuilder();
    bytes.addByte(channel.index);
    bytes.add([
      (payload.length >> 24) & 0xFF,
      (payload.length >> 16) & 0xFF,
      (payload.length >> 8) & 0xFF,
      payload.length & 0xFF,
    ]);
    bytes.add(payload);
    return bytes.toBytes();
  }

  group('WifiListenTransport', () {
    late WifiListenTransport transport;

    setUp(() => transport = WifiListenTransport());
    tearDown(() => transport.disconnect());

    test('待ち受けを始めるとポートが決まる', () async {
      await transport.startListening(port: 0);

      expect(transport.listeningPort, isNotNull);
      expect(transport.listeningPort, greaterThan(0));
      expect(transport.isConnected, isFalse);
    });

    test('繋いできた相手を受け入れる', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);

      await expectLater(accepted, completion(isNotNull));
      expect(transport.isConnected, isTrue);

      client.destroy();
    });

    test('相手が来ないまま畳んだら null で終わる', () async {
      await transport.startListening(port: 0);

      final accepted = transport.acceptOne();
      await transport.stopListening();

      // 例外にすると、待っている人がいない場面で未処理エラーになる
      await expectLater(accepted, completion(isNull));
    });

    test('待たずに続けて送っても、全部そのまま届く', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final received = BytesBuilder();
      final done = Completer<void>();

      // 1 通 6 バイト（ヘッダー 5 + 中身 1）
      const count = 60;

      client.listen((chunk) {
        received.add(chunk);
        if (received.length >= count * 6 && !done.isCompleted) done.complete();
      });

      // 指を滑らせている間と同じで、待たずに続けて呼ばれる。
      // IOSink は flush の最中に add されると StateError を投げる。
      // 握り潰していたので、その 1 通が黙って消えていた。
      final sends = [
        for (var i = 0; i < count; i++)
          transport.send(Uint8List.fromList([i]), ChannelId.touch),
      ];

      await expectLater(Future.wait(sends), completes);
      await done.future.timeout(const Duration(seconds: 5));

      final bytes = received.toBytes();
      expect(bytes.length, count * 6);

      // 順番も中身も崩れていないこと
      for (var i = 0; i < count; i++) {
        expect(bytes[i * 6], ChannelId.touch.index);
        expect(bytes[i * 6 + 4], 1);
        expect(bytes[i * 6 + 5], i);
      }

      client.destroy();
    });

    test('受け取ったフレームをチャンネルごとに切り出す', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final received = <({ChannelId channel, Uint8List data})>[];
      final done = Completer<void>();

      transport.receive().listen((e) {
        received.add(e);
        if (received.length == 2) done.complete();
      });

      client.add(frame(ChannelId.video, [1, 2, 3]));
      client.add(frame(ChannelId.control, [9]));
      await client.flush();

      await done.future.timeout(const Duration(seconds: 5));

      expect(received[0].channel, ChannelId.video);
      expect(received[0].data, [1, 2, 3]);
      expect(received[1].channel, ChannelId.control);
      expect(received[1].data, [9]);

      client.destroy();
    });

    test('上限を超える受信長は確保せずプロトコルエラーにする', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final protocolError = Completer<Object>();
      transport.receive().listen(
            (_) {},
            onError: (Object error) => protocolError.complete(error),
          );

      // 32 MiB + 1。ペイロード本体を送らなくてもヘッダー時点で拒否する。
      client.add([ChannelId.video.index, 0x02, 0x00, 0x00, 0x01]);
      await client.flush();

      await expectLater(
        protocolError.future.timeout(const Duration(seconds: 5)),
        completion(isA<FormatException>()),
      );
      expect(transport.isConnected, isFalse);

      client.destroy();
    });

    test('未知のチャンネルは読み飛ばして次の既知フレームを受信する', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final received = Completer<({ChannelId channel, Uint8List data})>();
      transport.receive().listen(received.complete);

      client.add([255, 0, 0, 0, 1, 99]);
      client.add(frame(ChannelId.control, [7]));
      await client.flush();

      final event = await received.future.timeout(const Duration(seconds: 5));
      expect(event.channel, ChannelId.control);
      expect(event.data, [7]);

      client.destroy();
    });

    test('分割して届いたフレームも組み立て直す', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final done = Completer<({ChannelId channel, Uint8List data})>();
      transport.receive().listen(done.complete);

      // ヘッダーの途中で切って送る。TCP では普通に起きる。
      final whole = frame(ChannelId.touch, [7, 7, 7, 7]);
      client.add(whole.sublist(0, 3));
      await client.flush();
      await Future<void>.delayed(const Duration(milliseconds: 50));
      client.add(whole.sublist(3));
      await client.flush();

      final event = await done.future.timeout(const Duration(seconds: 5));

      expect(event.channel, ChannelId.touch);
      expect(event.data, [7, 7, 7, 7]);

      client.destroy();
    });

    test('送信したものが相手に同じ形で届く', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final client = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      final received = <int>[];
      final done = Completer<void>();
      client.listen((chunk) {
        received.addAll(chunk);
        if (received.length >= 8) done.complete();
      });

      await transport.send(Uint8List.fromList([10, 20, 30]), ChannelId.control);

      await done.future.timeout(const Duration(seconds: 5));

      expect(received, [ChannelId.control.index, 0, 0, 0, 3, 10, 20, 30]);

      client.destroy();
    });

    test('2 台目は受け付けない（1 台目が生きている間）', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      final accepted = transport.acceptOne();
      final first = await Socket.connect(InternetAddress.loopbackIPv4, port);
      await accepted;

      // 受け入れたと同時に待ち受けを閉じるので、次は繋がらない
      await expectLater(
        Socket.connect(InternetAddress.loopbackIPv4, port,
            timeout: const Duration(seconds: 2)),
        throwsA(isA<SocketException>()),
      );

      first.destroy();
    });

    test('繋がる前に送ろうとすると分かる形で失敗する', () async {
      await transport.startListening(port: 0);

      expect(
        () => transport.send(Uint8List.fromList([1]), ChannelId.control),
        throwsA(isA<StateError>()),
      );
    });

    test('切断するとポートを手放す', () async {
      await transport.startListening(port: 0);
      final port = transport.listeningPort!;

      await transport.disconnect();

      expect(transport.listeningPort, isNull);

      // 同じポートをもう一度使える
      final again = WifiListenTransport();
      await again.startListening(port: port);
      expect(again.listeningPort, port);
      await again.disconnect();
    });
  });
}
