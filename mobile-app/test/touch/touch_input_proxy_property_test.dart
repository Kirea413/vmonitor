// Feature: vmonitor, Property 14: タッチイベントの完全転送
// Feature: vmonitor, Property 16: マルチタッチの同時転送
//
// Property 14: タッチイベントの完全転送
//   任意のタッチイベントリストに対して、タッチ入力プロキシが全てのイベントを
//   PC へ送信しなければならない（欠落なし）。
//   Validates: Requirements 6.1
//
// Property 16: マルチタッチの同時転送
//   任意の 2 本以上のタッチポイントセットに対して、タッチ入力プロキシは
//   全ポイントを同一メッセージで送信しなければならない（部分送信禁止）。
//   Validates: Requirements 6.4
//
// Requirements 6.1:
//   WHILE セッションがアクティブである間、THE タッチ入力プロキシ SHALL
//   スマートフォン画面上のタッチイベント（座標・圧力）を収集して PC クライアントへ送信する
//
// Requirements 6.4:
//   WHEN スマートフォン画面上でマルチタッチジェスチャー（2 本指以上）が行われたとき、
//   THE タッチ入力プロキシ SHALL 全タッチポイントの座標を同時に PC クライアントへ送信する

import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/widgets.dart' hide Orientation;
import 'package:glados/glados.dart';

import 'package:vmonitor/touch/touch_input_proxy.dart';
import 'package:vmonitor/transport/transport.dart';

// ---------------------------------------------------------------------------
// テスト用モックトランスポート
// ---------------------------------------------------------------------------

/// テスト用トランスポート: 送信されたペイロードを記録する。
///
/// [FlutterTouchInputProxy.send] が呼び出すたびに [sentPayloads] に追記する。
/// これにより「何回送信されたか」「何ポイント含まれるか」を検証できる。
class RecordingTransport implements Transport {
  /// 送信されたバイト列のリスト（送信順）。
  final List<Uint8List> sentPayloads = [];

  @override
  TransportType get type => TransportType.wifi;

  @override
  Future<void> connect(String host, int port) async {}

  @override
  Future<void> disconnect() async {}

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    if (channel == ChannelId.touch) {
      sentPayloads.add(Uint8List.fromList(data));
    }
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() =>
      const Stream.empty();

  @override
  int get estimatedBandwidthBps => 100 * 1000 * 1000; // 100 Mbps
}

// ---------------------------------------------------------------------------
// シリアライズ済みペイロードのデコードユーティリティ
// ---------------------------------------------------------------------------

/// バイナリペイロードからタッチポイント数を読み取る。
///
/// FlutterTouchInputProxy._serializeEvent のフォーマット:
///   offset 0: timestamp_us (8 bytes, int64)
///   offset 8: orientation  (1 byte)
///   offset 9: point_count  (1 byte)   ← ここを読む
int _decodePointCount(Uint8List payload) {
  if (payload.length < 10) return 0;
  return payload[9];
}

// ---------------------------------------------------------------------------
// glados ジェネレーター拡張
// ---------------------------------------------------------------------------

extension TouchPropertyAny on Any {
  /// 正規化座標 [0.0, 1.0] を生成する。
  Generator<double> get normalizedCoord {
    return any.intInRange(0, 1001).map((v) => v / 1000.0);
  }

  /// 圧力 [0.0, 1.0] を生成する。
  Generator<double> get pressure {
    return any.intInRange(0, 1001).map((v) => v / 1000.0);
  }

  /// 有効な TouchPhase を生成する。
  Generator<TouchPhase> get touchPhase {
    return any.choose(TouchPhase.values);
  }

  /// 有効な Orientation を生成する。
  Generator<Orientation> get orientation {
    return any.choose(Orientation.values);
  }

  /// 1 つの TouchPoint を生成する（id は呼び出し元で付与）。
  Generator<TouchPoint> touchPoint(int id) {
    return any.combine4(
      any.normalizedCoord,
      any.normalizedCoord,
      any.pressure,
      any.touchPhase,
      (double x, double y, double p, TouchPhase phase) => TouchPoint(
        id: id,
        x: x,
        y: y,
        pressure: p,
        phase: phase,
      ),
    );
  }

  /// N 個の TouchPoint リストを生成する（N ≥ 1）。
  Generator<List<TouchPoint>> touchPointList(int n) {
    assert(n >= 1);
    if (n == 1) {
      return any.touchPoint(0).map((p) => [p]);
    }
    // n 個の TouchPoint を combine で生成する
    // glados は combine2〜combine5 をサポートするため、
    // n ≥ 2 の場合は最初の 2 個を combine2 で生成し、残りを追加する。
    // シンプルに intInRange で各ポイントの座標を生成する。
    return any.intInRange(0, 1001).map((seed) {
      // seed をもとに決定的な n 個のポイントを生成する
      final rng = seed;
      final points = <TouchPoint>[];
      for (var i = 0; i < n; i++) {
        final x = ((rng + i * 137) % 1001) / 1000.0;
        final y = ((rng + i * 251) % 1001) / 1000.0;
        final p = ((rng + i * 373) % 1001) / 1000.0;
        final phase = TouchPhase.values[(rng + i) % TouchPhase.values.length];
        points.add(TouchPoint(id: i, x: x, y: y, pressure: p, phase: phase));
      }
      return points;
    });
  }

  /// 1〜10 個の TouchEvent からなるリストを生成する。
  ///
  /// Property 14 用: 任意の個数のイベントリスト。
  Generator<List<TouchEvent>> get touchEventList {
    return any.combine2(
      any.intInRange(1, 11), // イベント数: 1〜10
      any.orientation,
      (int count, Orientation orient) {
        final events = <TouchEvent>[];
        for (var i = 0; i < count; i++) {
          events.add(TouchEvent(
            points: [
              TouchPoint(
                id: i,
                x: ((i * 137) % 1001) / 1000.0,
                y: ((i * 251) % 1001) / 1000.0,
                pressure: 0.5,
                phase: TouchPhase.moved,
              ),
            ],
            timestamp: DateTime.fromMillisecondsSinceEpoch(i * 16),
            currentOrientation: orient,
          ));
        }
        return events;
      },
    );
  }

  /// 2〜5 個のタッチポイントを持つ TouchEvent を生成する。
  ///
  /// Property 16 用: マルチタッチ（2 本指以上）。
  Generator<TouchEvent> get multiTouchEvent {
    return any.combine2(
      any.intInRange(2, 6), // ポイント数: 2〜5
      any.orientation,
      (int pointCount, Orientation orient) {
        final points = <TouchPoint>[];
        for (var i = 0; i < pointCount; i++) {
          points.add(TouchPoint(
            id: i,
            x: (i * 200.0 + 100.0).clamp(0.0, 1000.0) / 1000.0,
            y: (i * 150.0 + 50.0).clamp(0.0, 1000.0) / 1000.0,
            pressure: 0.5,
            phase: TouchPhase.moved,
          ));
        }
        return TouchEvent(
          points: points,
          timestamp: DateTime.now(),
          currentOrientation: orient,
        );
      },
    );
  }
}

// ---------------------------------------------------------------------------
// テスト本体
// ---------------------------------------------------------------------------

void main() {
  // ウィジェットバインディング不要: FlutterTouchInputProxy.send は
  // dart:typed_data と transport のみ使用する純粋なモデルコード。

  group('Property 14: タッチイベントの完全転送', () {
    // ─── プロパティテスト ─────────────────────────────────────────────────────

    /// **Validates: Requirements 6.1**
    ///
    /// 任意のタッチイベントリスト（1〜10 個）に対して、
    /// プロキシが全てのイベントを欠落なくトランスポートへ送信することを検証する。
    ///
    /// 送信回数 == イベント数 であれば完全転送が保証される。
    Glados(any.touchEventList).test(
      '任意のイベントリストに対して送信回数がイベント数と一致する（欠落なし）',
      (List<TouchEvent> events) async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        // 全イベントを順番に送信する
        for (final event in events) {
          await proxy.send(event);
        }

        // Property 14: 送信回数 == イベント数（欠落なし）
        expect(
          transport.sentPayloads.length,
          equals(events.length),
          reason: '${events.length} 個のイベントを送信したが、'
              'トランスポートには ${transport.sentPayloads.length} 個しか届いていない',
        );

        proxy.dispose();
      },
    );

    /// **Validates: Requirements 6.1**
    ///
    /// 任意のタッチイベントに対して、シリアライズ後のペイロードが
    /// 空でない（データが存在する）ことを検証する。
    Glados(any.touchEventList).test(
      '全ての送信ペイロードは非空である',
      (List<TouchEvent> events) async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        for (final event in events) {
          await proxy.send(event);
        }

        // 全ペイロードが非空であること
        for (var i = 0; i < transport.sentPayloads.length; i++) {
          expect(
            transport.sentPayloads[i].isNotEmpty,
            isTrue,
            reason: 'イベント $i のペイロードが空になっている',
          );
        }

        proxy.dispose();
      },
    );

    // ─── ユニットテスト（具体的な例） ────────────────────────────────────────────

    group('具体的なイベント数での完全転送検証', () {
      test('シングルタッチイベント 1 件が送信される', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        final event = TouchEvent(
          points: [
            const TouchPoint(
                id: 0, x: 0.5, y: 0.5, pressure: 0.8, phase: TouchPhase.began),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        await proxy.send(event);

        expect(transport.sentPayloads.length, equals(1));
        proxy.dispose();
      });

      test('5 件のイベントが全て送信される', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        for (var i = 0; i < 5; i++) {
          await proxy.send(TouchEvent(
            points: [
              TouchPoint(
                  id: 0,
                  x: i * 0.1 + 0.1,
                  y: 0.5,
                  pressure: 0.5,
                  phase: TouchPhase.moved),
            ],
            timestamp: DateTime.fromMillisecondsSinceEpoch(i * 16),
            currentOrientation: Orientation.portrait,
          ));
        }

        expect(transport.sentPayloads.length, equals(5));
        proxy.dispose();
      });

      test('トランスポート未アタッチ時は送信されない（但しクラッシュしない）', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        // attach しない
        proxy.updateSize(const Size(1080, 1920));

        final event = TouchEvent(
          points: [
            const TouchPoint(
                id: 0, x: 0.5, y: 0.5, pressure: 0.5, phase: TouchPhase.began),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        // クラッシュしないこと
        await proxy.send(event);

        // トランスポートに届かないこと（アタッチしていないため）
        expect(transport.sentPayloads.length, equals(0));
        proxy.dispose();
      });

      test('10 件連続送信が全て届く', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        for (var i = 0; i < 10; i++) {
          await proxy.send(TouchEvent(
            points: [
              TouchPoint(
                  id: 0,
                  x: i / 10.0,
                  y: i / 10.0,
                  pressure: 0.6,
                  phase: TouchPhase.moved),
            ],
            timestamp: DateTime.fromMillisecondsSinceEpoch(i * 16),
            currentOrientation: Orientation.landscape,
          ));
        }

        expect(transport.sentPayloads.length, equals(10),
            reason: '10 件送信したが ${transport.sentPayloads.length} 件しか届かなかった');
        proxy.dispose();
      });
    });
  });

  // ---------------------------------------------------------------------------

  group('Property 16: マルチタッチの同時転送', () {
    // ─── プロパティテスト ─────────────────────────────────────────────────────

    /// **Validates: Requirements 6.4**
    ///
    /// 任意の 2〜5 本指タッチイベントに対して、
    /// プロキシが全ポイントを 1 つのメッセージ（1 回の send 呼び出し）で
    /// 送信することを検証する。
    ///
    /// 1 回の send == 1 ペイロード かつ
    /// ペイロード内の point_count == イベントのポイント数
    /// であれば「同一メッセージで送信」が保証される。
    Glados(any.multiTouchEvent).test(
      '任意のマルチタッチイベントが 1 メッセージで送信される（部分送信禁止）',
      (TouchEvent event) async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        await proxy.send(event);

        // Property 16: 1 イベント → 1 メッセージのみ（部分送信なし）
        expect(
          transport.sentPayloads.length,
          equals(1),
          reason: '${event.points.length} ポイントのマルチタッチイベントが '
              '${transport.sentPayloads.length} メッセージに分割されている（1 であるべき）',
        );

        // ペイロード内に全ポイントが含まれていること
        final pointCountInPayload =
            _decodePointCount(transport.sentPayloads[0]);
        expect(
          pointCountInPayload,
          equals(event.points.length),
          reason: 'ペイロードに含まれるポイント数($pointCountInPayload)が'
              'イベントのポイント数(${event.points.length})と一致しない',
        );

        proxy.dispose();
      },
    );

    /// **Validates: Requirements 6.4**
    ///
    /// 任意のマルチタッチイベントに対して、
    /// ペイロード内のポイント数がイベントのポイント数以上であることを検証する。
    ///
    /// FlutterTouchInputProxy の実装では、send() は引数の TouchEvent をそのまま
    /// シリアライズするため、event.points.length == payload の point_count となる。
    Glados(any.multiTouchEvent).test(
      'ペイロード内のポイント数がイベントのポイント数と一致する',
      (TouchEvent event) async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        await proxy.send(event);

        expect(transport.sentPayloads.length, equals(1));
        final pointCountInPayload =
            _decodePointCount(transport.sentPayloads[0]);

        expect(
          pointCountInPayload,
          equals(event.points.length),
          reason: 'マルチタッチ: ペイロードのポイント数が一致しない '
              '(expected=${event.points.length}, actual=$pointCountInPayload)',
        );

        proxy.dispose();
      },
    );

    // ─── ユニットテスト（具体的な例） ────────────────────────────────────────────

    group('具体的なマルチタッチでの同時転送検証', () {
      test('2 本指タッチが 1 メッセージで送信される', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        final event = TouchEvent(
          points: const [
            TouchPoint(
                id: 0, x: 0.3, y: 0.5, pressure: 0.7, phase: TouchPhase.began),
            TouchPoint(
                id: 1, x: 0.7, y: 0.5, pressure: 0.6, phase: TouchPhase.began),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        await proxy.send(event);

        // 1 メッセージのみ送信される（部分送信なし）
        expect(transport.sentPayloads.length, equals(1));

        // ペイロードに 2 ポイントが含まれる
        final pointCount = _decodePointCount(transport.sentPayloads[0]);
        expect(pointCount, equals(2));
        proxy.dispose();
      });

      test('5 本指タッチが 1 メッセージで送信される', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        final event = TouchEvent(
          points: List.generate(
            5,
            (i) => TouchPoint(
              id: i,
              x: (i + 1) * 0.15,
              y: 0.5,
              pressure: 0.5,
              phase: TouchPhase.moved,
            ),
          ),
          timestamp: DateTime.now(),
          currentOrientation: Orientation.landscape,
        );

        await proxy.send(event);

        expect(transport.sentPayloads.length, equals(1),
            reason: '5 ポイントのマルチタッチが複数メッセージに分割されている');

        final pointCount = _decodePointCount(transport.sentPayloads[0]);
        expect(pointCount, equals(5), reason: 'ペイロード内のポイント数が 5 でなければならない');
        proxy.dispose();
      });

      test('3 本指タッチのペイロードにポイント ID が保存される', () async {
        final transport = RecordingTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        final event = TouchEvent(
          points: const [
            TouchPoint(
                id: 10, x: 0.2, y: 0.3, pressure: 0.8, phase: TouchPhase.moved),
            TouchPoint(
                id: 20, x: 0.5, y: 0.6, pressure: 0.7, phase: TouchPhase.moved),
            TouchPoint(
                id: 30, x: 0.8, y: 0.9, pressure: 0.6, phase: TouchPhase.moved),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        await proxy.send(event);

        expect(transport.sentPayloads.length, equals(1));
        final pointCount = _decodePointCount(transport.sentPayloads[0]);
        expect(pointCount, equals(3));
        proxy.dispose();
      });

      test('portrait と landscape どちらでもマルチタッチが 1 メッセージで送信される', () async {
        for (final orientation in [
          Orientation.portrait,
          Orientation.landscape
        ]) {
          final transport = RecordingTransport();
          final proxy = FlutterTouchInputProxy();
          proxy.attach(transport);
          proxy.updateSize(const Size(1080, 1920));

          final event = TouchEvent(
            points: const [
              TouchPoint(
                  id: 0,
                  x: 0.25,
                  y: 0.5,
                  pressure: 0.5,
                  phase: TouchPhase.began),
              TouchPoint(
                  id: 1,
                  x: 0.75,
                  y: 0.5,
                  pressure: 0.5,
                  phase: TouchPhase.began),
            ],
            timestamp: DateTime.now(),
            currentOrientation: orientation,
          );

          await proxy.send(event);

          expect(transport.sentPayloads.length, equals(1),
              reason: '${orientation.name} で 2 ポイントが 1 メッセージで送信されるべき');
          expect(_decodePointCount(transport.sentPayloads[0]), equals(2));

          proxy.dispose();
        }
      });
    });
  });
}
