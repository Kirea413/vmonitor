import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:vmonitor/l10n/app_localizations.dart';
import 'package:vmonitor/main.dart';

void main() {
  testWidgets('VMonitorApp renders without error', (WidgetTester tester) async {
    await tester.pumpWidget(const VMonitorApp());

    // 初期フレームをレンダリングする
    await tester.pump();

    // AppBar のタイトルが出ていることを確認する。
    //
    // 文字そのものではなく訳文から引く。直接書くと、言語を足したり
    // 文言を直したりするたびにテストが落ちる。「表示が出ているか」を
    // 見たいのであって、日本語かどうかを見たいわけではない。
    final context = tester.element(find.byType(Scaffold).first);
    expect(find.text(L.of(context).homeTitle), findsOneWidget);

    // デバイス探索の 5 秒タイムアウトをスキップして保留タイマーを解消する
    await tester.pump(const Duration(seconds: 6));
  });

  testWidgets('英語の端末では英語で出る', (WidgetTester tester) async {
    await tester.pumpWidget(const VMonitorApp());
    await tester.pump();

    final context = tester.element(find.byType(Scaffold).first);

    // 既定の言語（テスト環境は en）で、日本語のままになっていないこと。
    // 訳し忘れがあると、ここが日本語のまま残る。
    expect(L.of(context).homeTitle, isNot(contains('接続')));

    await tester.pump(const Duration(seconds: 6));
  });

  testWidgets('ホーム画面からライセンス一覧を開ける',
      (WidgetTester tester) async {
    await tester.pumpWidget(const VMonitorApp());
    await tester.pump();

    final context = tester.element(find.byType(Scaffold).first);
    final thanks = L.of(context).licensesThanks;

    await tester.tap(find.byKey(const Key('open-licenses')));
    await tester.pumpAndSettle();

    expect(find.byType(LicensePage), findsOneWidget);
    expect(find.text(thanks), findsOneWidget);
  });
}
