import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:vmonitor/ui/display_preferences.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('余白は向きごとに持つ', () {
    test('縦で決めた値が横に漏れない', () async {
      SharedPreferences.setMockInitialValues({});

      final prefs = DisplayPreferences();
      await prefs.load();

      prefs.setOrientation(Orientation.portrait);
      await prefs.setInsets(bottom: 40);

      expect(prefs.insets.bottom, 40);

      // 横にしても、縦で決めた下の余白は付いてこない。
      // 付いてくると、避けたかったホームバーとは別の場所が削られる。
      prefs.setOrientation(Orientation.landscape);
      expect(prefs.insets.bottom, 0);
    });

    test('それぞれの向きで別々に覚える', () async {
      SharedPreferences.setMockInitialValues({});

      final prefs = DisplayPreferences();
      await prefs.load();

      prefs.setOrientation(Orientation.portrait);
      await prefs.setInsets(top: 10, bottom: 20);

      prefs.setOrientation(Orientation.landscape);
      await prefs.setInsets(left: 30, right: 15);

      expect(prefs.insetsFor(Orientation.portrait),
          const EdgeInsets.fromLTRB(0, 10, 0, 20));
      expect(prefs.insetsFor(Orientation.landscape),
          const EdgeInsets.fromLTRB(30, 0, 15, 0));
    });

    test('保存したものを読み直せる', () async {
      SharedPreferences.setMockInitialValues({});

      final first = DisplayPreferences();
      await first.load();
      first.setOrientation(Orientation.landscape);
      await first.setInsets(left: 25);

      final second = DisplayPreferences();
      await second.load();

      expect(second.insetsFor(Orientation.landscape).left, 25);
    });

    test('向きで分ける前の設定を引き継ぐ', () async {
      // 古い版が書いた鍵しかない状態
      SharedPreferences.setMockInitialValues({
        'display.inset.top': 12.0,
        'display.inset.bottom': 34.0,
        'display.inset.left': 0.0,
        'display.inset.right': 0.0,
      });

      final prefs = DisplayPreferences();
      await prefs.load();

      // 捨てずに引き継ぐ。合わなければ画面を見ながら直せる。
      expect(prefs.insetsFor(Orientation.portrait).top, 12);
      expect(prefs.insetsFor(Orientation.portrait).bottom, 34);
    });

    test('上限を超えた値は丸める', () async {
      SharedPreferences.setMockInitialValues({});

      final prefs = DisplayPreferences();
      await prefs.load();

      await prefs.setInsets(top: 9999);

      expect(prefs.insets.top, DisplayPreferences.maxInset);
    });

    test('リセットはいまの向きだけを 0 に戻す', () async {
      SharedPreferences.setMockInitialValues({});

      final prefs = DisplayPreferences();
      await prefs.load();

      prefs.setOrientation(Orientation.portrait);
      await prefs.setInsets(top: 20);

      prefs.setOrientation(Orientation.landscape);
      await prefs.setInsets(top: 30);
      await prefs.clearInsets();

      expect(prefs.insetsFor(Orientation.landscape).top, 0);
      expect(prefs.insetsFor(Orientation.portrait).top, 20);
    });
  });
}
