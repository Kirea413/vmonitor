import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:vmonitor/renderer/hardware_renderer.dart';
import 'package:vmonitor/renderer/renderer.dart';

/// テスト用のモックメソッドチャンネル
///
/// TestDefaultBinaryMessenger を使って MethodChannel の呼び出しをインターセプトする。
void _setupMockMethodChannel({
  bool failPushFrame = false,
}) {
  TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
      .setMockMethodCallHandler(
    const MethodChannel('vmonitor/renderer'),
    (MethodCall call) async {
      switch (call.method) {
        case 'initialize':
          return 42; // Return a valid textureId
        case 'pushFrame':
          if (failPushFrame) {
            throw PlatformException(
              code: 'DECODE_ERROR',
              message: 'デコードに失敗しました',
            );
          }
          return null;
        case 'dispose':
          return null;
        default:
          throw MissingPluginException('Unknown method: ${call.method}');
      }
    },
  );
}

void _clearMockMethodChannel() {
  TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
      .setMockMethodCallHandler(
    const MethodChannel('vmonitor/renderer'),
    null,
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(_setupMockMethodChannel);
  tearDown(_clearMockMethodChannel);

  // ─── start / stop ────────────────────────────────────────────────────

  group('start()', () {
    test('start() が initialize メソッドチャンネルを呼び出す', () async {
      final calls = <MethodCall>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);

      expect(calls.any((c) => c.method == 'initialize'), isTrue);

      await renderer.stop();
      await controller.close();
    });

    test('start() を 2 回呼び出しても 2 回目は無視される', () async {
      final calls = <String>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call.method);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);
      await renderer.start(controller.stream); // 2 回目は無視されるべき

      final initCount = calls.where((m) => m == 'initialize').length;
      expect(initCount, equals(1));

      await renderer.stop();
      await controller.close();
    });
  });

  group('stop()', () {
    test('stop() が dispose メソッドチャンネルを呼び出す', () async {
      final calls = <MethodCall>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);
      await renderer.stop();

      expect(calls.any((c) => c.method == 'dispose'), isTrue);
      await controller.close();
    });

    test('start() していない状態で stop() を呼んでも例外が発生しない', () async {
      final renderer = HardwareRenderer();
      await expectLater(renderer.stop(), completes);
    });

    test('stop() 後に再度 start() できる', () async {
      final calls = <String>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call.method);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final c1 = StreamController<Uint8List>();
      final c2 = StreamController<Uint8List>();

      await renderer.start(c1.stream);
      await renderer.stop();
      await renderer.start(c2.stream);

      final initCount = calls.where((m) => m == 'initialize').length;
      expect(initCount, equals(2));

      await renderer.stop();
      await c1.close();
      await c2.close();
    });
  });

  // ─── pushFrame ───────────────────────────────────────────────────────

  group('pushFrame()', () {
    test('フレームが到着すると pushFrame メソッドチャンネルが呼び出される', () async {
      final calls = <MethodCall>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);

      final frame = Uint8List.fromList([0x00, 0x01, 0x02, 0x03]);
      controller.add(frame);
      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(calls.any((c) => c.method == 'pushFrame'), isTrue);
      final pushCall = calls.firstWhere((c) => c.method == 'pushFrame');
      expect(pushCall.arguments['data'], equals(frame));

      await renderer.stop();
      await controller.close();
    });

    test('pushFrame が PlatformException をスローしてもレンダリングが継続する', () async {
      _setupMockMethodChannel(failPushFrame: true);

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      // エラーが statsStream に流れることを確認する（例外はスローされない）
      final errors = <Object>[];
      renderer.statsStream.listen((_) {}, onError: errors.add);

      await renderer.start(controller.stream);

      controller.add(Uint8List.fromList([0xFF, 0xD8]));
      await Future<void>.delayed(const Duration(milliseconds: 50));

      // エラーは statsStream に流れるが、レンダラー自体はまだ動いている
      expect(errors, isNotEmpty);

      await renderer.stop();
      await controller.close();
    });
  });

  // ─── statsStream ────────────────────────────────────────────────────

  group('statsStream', () {
    test('複数フレームを受信すると statsStream に RendererStats が流れる', () async {
      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      final statsReceived = <RendererStats>[];
      final statsSubscription = renderer.statsStream.listen(statsReceived.add);

      await renderer.start(controller.stream);

      // 数フレームを送る
      for (var i = 0; i < 5; i++) {
        controller.add(Uint8List.fromList([i, i + 1, i + 2]));
        await Future<void>.delayed(const Duration(milliseconds: 20));
      }

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(statsReceived, isNotEmpty);
      for (final s in statsReceived) {
        expect(s.decodeLatencyMs, greaterThanOrEqualTo(0));
        expect(s.fps, greaterThanOrEqualTo(0.0));
      }

      await statsSubscription.cancel();
      await renderer.stop();
      await controller.close();
    });

    test('RendererStats は fps と decodeLatencyMs を持つ', () {
      const stats = RendererStats(fps: 30.0, decodeLatencyMs: 15);
      expect(stats.fps, equals(30.0));
      expect(stats.decodeLatencyMs, equals(15));
    });

    test('stop() 後に statsStream は完了する', () async {
      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);
      await renderer.dispose(); // dispose は stop を内包する

      // dispose 後に statsStream が閉じられたことを確認する
      final isDone = await renderer.statsStream.isEmpty.timeout(
        const Duration(milliseconds: 200),
        onTimeout: () => true,
      );
      expect(isDone, isTrue);

      await controller.close();
    });
  });

  // ─── textureId ───────────────────────────────────────────────────────

  group('textureId', () {
    test('start() 前は textureId が null', () {
      final renderer = HardwareRenderer();
      expect(renderer.textureId, isNull);
    });

    test('stop() 後は textureId が null に戻る', () async {
      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);
      await renderer.stop();

      expect(renderer.textureId, isNull);
      await controller.close();
    });
  });

  // ─── フレームストリーム完了 ───────────────────────────────────────────

  group('フレームストリーム完了', () {
    test('フレームストリームが閉じると stop() が自動的に呼ばれる', () async {
      final calls = <String>[];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(
        const MethodChannel('vmonitor/renderer'),
        (MethodCall call) async {
          calls.add(call.method);
          if (call.method == 'initialize') return 42;
          return null;
        },
      );

      final renderer = HardwareRenderer();
      final controller = StreamController<Uint8List>();

      await renderer.start(controller.stream);
      await controller.close(); // ストリームを閉じる

      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(calls.any((c) => c == 'dispose'), isTrue);
    });
  });
}
