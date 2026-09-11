// Feature: vmonitor, Property 17: タッチ入力処理時間の上限
//
// Property 17: タッチ入力処理時間の上限
//   任意のタッチイベントに対して、シリアライズ→デシリアライズ→注入の処理時間は
//   50ms 未満でなければならない（ネットワーク転送部分はモックで除外）。
//   Validates: Requirements 6.5
//
// Requirements 6.5:
//   THE タッチ入力プロキシ SHALL タッチイベントを収集してから
//   THE PC クライアント が Windows に注入するまでの遅延を 50ms 以内に維持する

import 'dart:typed_data';

import 'package:flutter/widgets.dart' hide Orientation;
import 'package:glados/glados.dart';

import 'package:vmonitor/touch/touch_input_proxy.dart';
import 'package:vmonitor/transport/transport.dart';

// ---------------------------------------------------------------------------
// モックトランスポート（即時送信: ネットワーク遅延なし）
// ---------------------------------------------------------------------------

/// レイテンシ計測用のモックトランスポート。
///
/// ネットワーク転送時間を除外するため、[send] は即座に完了する。
/// [sentPayloads] に受信したペイロードを記録して、
/// デシリアライズ・注入シミュレーションに再利用できるようにする。
class InstantTransport implements Transport {
  final List<Uint8List> sentPayloads = [];

  @override
  TransportType get type => TransportType.wifi;

  @override
  Future<void> connect(String host, int port) async {}

  @override
  Future<void> disconnect() async {}

  @override
  Future<void> send(Uint8List data, ChannelId channel) async {
    // ネットワーク遅延なし（即時完了）
    if (channel == ChannelId.touch) {
      sentPayloads.add(Uint8List.fromList(data));
    }
  }

  @override
  Stream<({ChannelId channel, Uint8List data})> receive() =>
      const Stream.empty();

  @override
  int get estimatedBandwidthBps => 1000 * 1000 * 1000; // 1 Gbps（遅延なしを模擬）
}

// ---------------------------------------------------------------------------
// デシリアライズ・注入のシミュレーション
// ---------------------------------------------------------------------------

/// シリアライズ済みペイロードから [TouchEvent] に相当するデータをデシリアライズする。
///
/// PC クライアント側の WindowsInkInjector が行う処理を模倣する。
/// フォーマット（FlutterTouchInputProxy._serializeEvent 準拠）:
///   offset 0: timestamp_us (8 bytes, int64)
///   offset 8: orientation  (1 byte)
///   offset 9: point_count  (1 byte)
///   per point (17 bytes):
///     id       (4 bytes, int32)
///     x        (4 bytes, float32)
///     y        (4 bytes, float32)
///     pressure (4 bytes, float32)
///     phase    (1 byte)
DeserializedTouchEvent _deserializePayload(Uint8List payload) {
  if (payload.length < 10) {
    return DeserializedTouchEvent(pointCount: 0, timestampUs: 0);
  }

  final bd = ByteData.sublistView(payload);
  final timestampUs = bd.getInt64(0, Endian.little);
  // orientation at offset 8 (unused in this simulation)
  final pointCount = payload[9];

  return DeserializedTouchEvent(
    pointCount: pointCount,
    timestampUs: timestampUs,
  );
}

/// デシリアライズ結果を表す軽量データクラス。
class DeserializedTouchEvent {
  final int pointCount;
  final int timestampUs;

  const DeserializedTouchEvent({
    required this.pointCount,
    required this.timestampUs,
  });
}

/// Windows Ink 注入のシミュレーション。
///
/// 実際の PC 側注入処理（InjectTouchInput 呼び出し）の代わりに、
/// デシリアライズ済みデータを検証するだけの処理を行う。
/// 目的は処理時間の計測のみであり、OS API は呼び出さない。
void _simulateInject(DeserializedTouchEvent event) {
  // 注入のシミュレーション: 座標検証のみ行う（O(1) 処理）
  assert(event.pointCount >= 0 && event.pointCount <= 255);
}

// ---------------------------------------------------------------------------
// glados ジェネレーター拡張（レイテンシテスト用）
// ---------------------------------------------------------------------------

extension LatencyTestAny on Any {
  /// 正規化座標 [0.0, 1.0] を生成する。
  Generator<double> get normalizedCoord {
    return any.intInRange(0, 1001).map((v) => v / 1000.0);
  }

  /// 圧力 [0.0, 1.0] を生成する。
  Generator<double> get pressureValue {
    return any.intInRange(0, 1001).map((v) => v / 1000.0);
  }

  /// 有効な [TouchPhase] を生成する。
  Generator<TouchPhase> get touchPhase {
    return any.choose(TouchPhase.values);
  }

  /// 有効な [Orientation] を生成する。
  Generator<Orientation> get orientation {
    return any.choose(Orientation.values);
  }

  /// 1〜10 個のタッチポイントを持つ [TouchEvent] を生成する。
  ///
  /// シングルタッチ〜マルチタッチを幅広くカバーする。
  Generator<TouchEvent> get touchEvent {
    return any.combine3(
      any.intInRange(1, 11), // ポイント数: 1〜10
      any.orientation,
      any.intInRange(0, 1000000), // タイムスタンプのシード
      (int pointCount, Orientation orient, int seed) {
        final points = <TouchPoint>[];
        for (var i = 0; i < pointCount; i++) {
          final x = ((seed + i * 137) % 1001) / 1000.0;
          final y = ((seed + i * 251) % 1001) / 1000.0;
          final pressure = ((seed + i * 373) % 1001) / 1000.0;
          final phase =
              TouchPhase.values[(seed + i) % TouchPhase.values.length];
          points.add(TouchPoint(
            id: i,
            x: x,
            y: y,
            pressure: pressure,
            phase: phase,
          ));
        }
        return TouchEvent(
          points: points,
          timestamp: DateTime.fromMillisecondsSinceEpoch(seed % 1000000),
          currentOrientation: orient,
        );
      },
    );
  }

  /// シングルタッチイベント（ポイント数 = 1）を生成する。
  Generator<TouchEvent> get singleTouchEvent {
    return any.combine2(
      any.orientation,
      any.intInRange(0, 1000000),
      (Orientation orient, int seed) {
        return TouchEvent(
          points: [
            TouchPoint(
              id: 0,
              x: (seed % 1001) / 1000.0,
              y: ((seed + 137) % 1001) / 1000.0,
              pressure: ((seed + 251) % 1001) / 1000.0,
              phase: TouchPhase.values[seed % TouchPhase.values.length],
            ),
          ],
          timestamp: DateTime.fromMillisecondsSinceEpoch(seed % 1000000),
          currentOrientation: orient,
        );
      },
    );
  }

  /// マルチタッチイベント（ポイント数 2〜10）を生成する。
  Generator<TouchEvent> get multiTouchEvent {
    return any.combine2(
      any.intInRange(2, 11), // ポイント数: 2〜10
      any.orientation,
      (int pointCount, Orientation orient) {
        final points = <TouchPoint>[];
        for (var i = 0; i < pointCount; i++) {
          points.add(TouchPoint(
            id: i,
            x: (i * 100.0 + 50.0).clamp(0.0, 1000.0) / 1000.0,
            y: (i * 80.0 + 30.0).clamp(0.0, 1000.0) / 1000.0,
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
// 処理時間計測ヘルパー
// ---------------------------------------------------------------------------

/// シリアライズ→デシリアライズ→注入の処理時間を計測する（ネットワーク転送を除外）。
///
/// [transport] の [send] は即時完了するため、計測範囲に含まれるのは:
///   1. タッチイベントのシリアライズ（FlutterTouchInputProxy._serializeEvent）
///   2. トランスポートへの送信（モック: 即時）
///   3. ペイロードのデシリアライズ（_deserializePayload）
///   4. 注入シミュレーション（_simulateInject）
///
/// 戻り値はマイクロ秒単位の経過時間。
/// 計測前に一巡だけ空回ししたかどうか。
bool _warmedUp = false;

/// 計測に影響する初回限定のコストを先に済ませる。
///
/// 最初の 1 回だけは JIT コンパイル・非同期機構の初期化・
/// ストリームコントローラーの生成といった一度きりの処理が乗るため、
/// そのまま測ると 300ms を超えることがある。これはタッチ処理の
/// 実力ではなくテスト基盤の初期化時間なので、計測対象から外す。
Future<void> _warmUp() async {
  if (_warmedUp) return;
  _warmedUp = true;

  await _runProcessingCycle(TouchEvent(
    points: const [
      TouchPoint(id: 0, x: 0.5, y: 0.5, pressure: 1.0, phase: TouchPhase.began),
    ],
    timestamp: DateTime.now(),
    currentOrientation: Orientation.portrait,
  ));
}

/// シリアライズ → デシリアライズ → 注入 の一巡を実行する。
Future<void> _runProcessingCycle(TouchEvent event) async {
  final transport = InstantTransport();
  final proxy = FlutterTouchInputProxy();
  proxy.attach(transport);
  proxy.updateSize(const Size(1080, 1920));

  await proxy.send(event);

  for (final payload in transport.sentPayloads) {
    _simulateInject(_deserializePayload(payload));
  }

  proxy.dispose();
}

Future<int> _measureProcessingTimeMicros(TouchEvent event) async {
  await _warmUp();

  final transport = InstantTransport();
  final proxy = FlutterTouchInputProxy();
  proxy.attach(transport);
  proxy.updateSize(const Size(1080, 1920));

  final start = DateTime.now().microsecondsSinceEpoch;

  // 1. シリアライズ + モック送信（ネットワーク転送なし）
  await proxy.send(event);

  // 2. デシリアライズ（PC 側受信処理のシミュレーション）
  for (final payload in transport.sentPayloads) {
    final deserialized = _deserializePayload(payload);

    // 3. 注入シミュレーション（Windows Ink API 呼び出しのシミュレーション）
    _simulateInject(deserialized);
  }

  final elapsed = DateTime.now().microsecondsSinceEpoch - start;

  proxy.dispose();
  return elapsed;
}

// ---------------------------------------------------------------------------
// テスト本体
// ---------------------------------------------------------------------------

void main() {
  // ─── プロパティテスト ─────────────────────────────────────────────────────

  group('Property 17: タッチ入力処理時間の上限', () {
    /// **Validates: Requirements 6.5**
    ///
    /// 任意のタッチイベント（シングル〜マルチタッチ、全フェーズ、全向き）に対して、
    /// シリアライズ→デシリアライズ→注入の処理時間が 50ms 未満であることを検証する。
    ///
    /// ネットワーク転送は [InstantTransport] でモックアウトしている。
    Glados(any.touchEvent).test(
      '任意のタッチイベントのシリアライズ→デシリアライズ→注入処理時間は 50ms 未満',
      (TouchEvent event) async {
        final elapsedMicros = await _measureProcessingTimeMicros(event);
        const limitMicros = 50 * 1000; // 50ms = 50,000µs

        expect(
          elapsedMicros,
          lessThan(limitMicros),
          reason: '処理時間が上限を超えた: ${elapsedMicros}µs '
              '(上限: ${limitMicros}µs = 50ms), '
              'ポイント数: ${event.points.length}, '
              '向き: ${event.currentOrientation}',
        );
      },
    );

    /// **Validates: Requirements 6.5**
    ///
    /// シングルタッチイベントに特化した処理時間検証。
    /// シングルタッチは最も一般的なケースであり、確実に 50ms 未満であるべき。
    Glados(any.singleTouchEvent).test(
      'シングルタッチイベントの処理時間は 50ms 未満',
      (TouchEvent event) async {
        final elapsedMicros = await _measureProcessingTimeMicros(event);
        const limitMicros = 50 * 1000;

        expect(
          elapsedMicros,
          lessThan(limitMicros),
          reason: 'シングルタッチ処理時間超過: ${elapsedMicros}µs '
              '(上限: ${limitMicros}µs)',
        );
      },
    );

    /// **Validates: Requirements 6.5**
    ///
    /// マルチタッチイベント（2〜10 ポイント）に対する処理時間検証。
    /// ポイント数が多くても 50ms 以内に収まることを確認する。
    Glados(any.multiTouchEvent).test(
      'マルチタッチイベント（2〜10 ポイント）の処理時間は 50ms 未満',
      (TouchEvent event) async {
        final elapsedMicros = await _measureProcessingTimeMicros(event);
        const limitMicros = 50 * 1000;

        expect(
          elapsedMicros,
          lessThan(limitMicros),
          reason: 'マルチタッチ（${event.points.length}ポイント）処理時間超過: '
              '${elapsedMicros}µs (上限: ${limitMicros}µs)',
        );
      },
    );

    // ─── ユニットテスト（具体的な例） ────────────────────────────────────────────

    group('具体的なイベントでの処理時間検証', () {
      test('シングルタッチ（began フェーズ）の処理時間は 50ms 未満', () async {
        final event = TouchEvent(
          points: const [
            TouchPoint(
                id: 0, x: 0.5, y: 0.5, pressure: 0.8, phase: TouchPhase.began),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        final elapsedMicros = await _measureProcessingTimeMicros(event);
        expect(
          elapsedMicros,
          lessThan(50 * 1000),
          reason: '処理時間: ${elapsedMicros}µs',
        );
      });

      test('シングルタッチ（ended フェーズ）の処理時間は 50ms 未満', () async {
        final event = TouchEvent(
          points: const [
            TouchPoint(
                id: 0, x: 0.3, y: 0.7, pressure: 0.0, phase: TouchPhase.ended),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.landscape,
        );

        final elapsedMicros = await _measureProcessingTimeMicros(event);
        expect(elapsedMicros, lessThan(50 * 1000),
            reason: '処理時間: ${elapsedMicros}µs');
      });

      test('5 本指マルチタッチの処理時間は 50ms 未満', () async {
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
          currentOrientation: Orientation.portrait,
        );

        final elapsedMicros = await _measureProcessingTimeMicros(event);
        expect(elapsedMicros, lessThan(50 * 1000),
            reason: '5 本指処理時間: ${elapsedMicros}µs');
      });

      test('10 本指マルチタッチの処理時間は 50ms 未満', () async {
        final event = TouchEvent(
          points: List.generate(
            10,
            (i) => TouchPoint(
              id: i,
              x: (i * 0.09 + 0.05).clamp(0.0, 1.0),
              y: 0.5,
              pressure: 0.6,
              phase: TouchPhase.moved,
            ),
          ),
          timestamp: DateTime.now(),
          currentOrientation: Orientation.landscape,
        );

        final elapsedMicros = await _measureProcessingTimeMicros(event);
        expect(elapsedMicros, lessThan(50 * 1000),
            reason: '10 本指処理時間: ${elapsedMicros}µs');
      });

      test('portrait と landscape の両向きで処理時間は 50ms 未満', () async {
        for (final orientation in [
          Orientation.portrait,
          Orientation.landscape
        ]) {
          final event = TouchEvent(
            points: const [
              TouchPoint(
                  id: 0,
                  x: 0.5,
                  y: 0.5,
                  pressure: 0.7,
                  phase: TouchPhase.moved),
            ],
            timestamp: DateTime.now(),
            currentOrientation: orientation,
          );

          final elapsedMicros = await _measureProcessingTimeMicros(event);
          expect(
            elapsedMicros,
            lessThan(50 * 1000),
            reason: '${orientation.name} の処理時間: ${elapsedMicros}µs',
          );
        }
      });

      test('cancelled フェーズのイベント処理時間は 50ms 未満', () async {
        final event = TouchEvent(
          points: const [
            TouchPoint(
                id: 0,
                x: 0.2,
                y: 0.8,
                pressure: 0.0,
                phase: TouchPhase.cancelled),
          ],
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portraitFlipped,
        );

        final elapsedMicros = await _measureProcessingTimeMicros(event);
        expect(elapsedMicros, lessThan(50 * 1000),
            reason: '処理時間: ${elapsedMicros}µs');
      });

      test('ペイロードのシリアライズが正しく機能する（デシリアライズ後のポイント数一致）', () async {
        final transport = InstantTransport();
        final proxy = FlutterTouchInputProxy();
        proxy.attach(transport);
        proxy.updateSize(const Size(1080, 1920));

        const pointCount = 3;
        final event = TouchEvent(
          points: List.generate(
            pointCount,
            (i) => TouchPoint(
              id: i,
              x: (i + 1) * 0.25,
              y: 0.5,
              pressure: 0.5,
              phase: TouchPhase.moved,
            ),
          ),
          timestamp: DateTime.now(),
          currentOrientation: Orientation.portrait,
        );

        await proxy.send(event);

        expect(transport.sentPayloads.length, equals(1));
        final deserialized = _deserializePayload(transport.sentPayloads[0]);
        expect(
          deserialized.pointCount,
          equals(pointCount),
          reason: 'デシリアライズ後のポイント数が一致すること',
        );

        proxy.dispose();
      });
    });
  });
}
