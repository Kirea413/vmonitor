// ペンの傾きを、Flutter の持ちかたから Windows の持ちかたに直す。
//
// Flutter は「垂直から何度倒れているか」(tilt) と「どちらへ倒れて
// いるか」(orientation) の二つで持つ。Windows は横方向と縦方向に
// 分けて持つ。

import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';

import 'package:vmonitor/touch/touch_input_proxy.dart';

void main() {
  group('penTiltToWindows', () {
    test('立てていれば両方 0', () {
      expect(penTiltToWindows(0, 0), (x: 0, y: 0));
      expect(penTiltToWindows(0, math.pi / 3), (x: 0, y: 0));
    });

    test('画面の上（奥）へ倒すと縦が負になる', () {
      // orientation 0 は画面の上向き。Windows の tiltY は手前が正
      // なので、奥へ倒したここは負になる。
      final tilt = penTiltToWindows(math.pi / 6, 0);

      expect(tilt.x, 0);
      expect(tilt.y, -30);
    });

    test('右へ倒すと横だけに出る', () {
      final tilt = penTiltToWindows(math.pi / 6, math.pi / 2);

      expect(tilt.x, 30);
      expect(tilt.y, 0);
    });

    test('左へ倒すと横が負になる', () {
      final tilt = penTiltToWindows(math.pi / 6, -math.pi / 2);

      expect(tilt.x, -30);
      expect(tilt.y, 0);
    });

    test('手前へ倒すと縦が正になる', () {
      // 画面の下向き。Windows の tiltY は手前が正。
      // ここが逆だと前後がひっくり返る。
      final tilt = penTiltToWindows(math.pi / 6, math.pi);

      expect(tilt.x, 0);
      expect(tilt.y, 30);
    });

    test('斜めは両方に分かれる', () {
      final tilt = penTiltToWindows(math.pi / 4, math.pi / 4);

      // 右かつ奥へ倒したので、横は正、縦は負
      expect(tilt.x, greaterThan(0));
      expect(tilt.y, lessThan(0));

      // 45 度を斜め 45 度へ倒すと、横も縦も 45 度より浅くなる
      expect(tilt.x, lessThan(45));
      expect(tilt.y.abs(), lessThan(45));
    });

    test('寝かせ切っても範囲を超えない', () {
      // tan が発散する。そのまま渡すと Windows に弾かれ、
      // その拒否は触れている接触の巻き添え取り消しを伴う。
      final tilt = penTiltToWindows(math.pi / 2, math.pi / 2);

      expect(tilt.x.abs(), lessThanOrEqualTo(90));
      expect(tilt.y.abs(), lessThanOrEqualTo(90));
    });

    test('壊れた値でも落ちない', () {
      expect(penTiltToWindows(double.nan, 0), (x: 0, y: 0));
      expect(penTiltToWindows(0, double.nan), (x: 0, y: 0));
      expect(penTiltToWindows(double.infinity, 0), (x: 0, y: 0));
    });
  });

  group('シリアライズ', () {
    test('傾きが 20 バイトの並びに載る', () {
      const point = TouchPoint(
        id: 1,
        x: 0.5,
        y: 0.5,
        pressure: 0.8,
        phase: TouchPhase.moved,
        isPen: true,
        tiltX: 40,
        tiltY: -25,
      );

      final proxy = FlutterTouchInputProxy();
      final payload = proxy.debugSerialize(TouchEvent(
        points: const [point],
        timestamp: DateTime.fromMicrosecondsSinceEpoch(0),
        currentOrientation: Orientation.portrait,
      ));

      // ヘッダー 10 + 1 点 20
      expect(payload.length, 30);

      // 18 バイト目と 19 バイト目が傾き（符号付き）
      expect(payload.buffer.asByteData().getInt8(10 + 18), 40);
      expect(payload.buffer.asByteData().getInt8(10 + 19), -25);
    });
  });
}
