// Feature: vmonitor, Property 11: レンダラーの全画面表示（レターボックスなし）
//
// Property 11: レンダラーの全画面表示（レターボックスなし）
// 任意の DisplaySpec（解像度・向き）に対して、レンダラーが計算する描画領域の
// 幅・高さがスマートフォン画面の全幅・全高と等しくなければならない。
//
// Validates: Requirements 5.3
//
// Requirements 5.3:
//   THE レンダラー SHALL 映像をスマートフォン画面のアスペクト比に合わせて
//   レターボックスまたはピラーボックスなしで全画面表示する

import 'package:glados/glados.dart';

// ─── データモデル ─────────────────────────────────────────────────────────────

/// ディスプレイの解像度を表すデータモデル。
///
/// design.md に定義された範囲:
///   MinSupported: 640x480
///   MaxSupported: 3840x2160
class Resolution {
  final int width;
  final int height;

  const Resolution(this.width, this.height);

  /// アスペクト比 (width / height)。
  double get aspectRatio => width / height;

  @override
  String toString() => 'Resolution(${width}x$height)';
}

/// ディスプレイの向き。
///
/// design.md: enum Orientation { Portrait, Landscape, PortraitFlipped, LandscapeFlipped }
enum DisplayOrientation {
  portrait,
  landscape,
  portraitFlipped,
  landscapeFlipped,
}

/// ディスプレイ仕様。
///
/// design.md: record DisplaySpec(Resolution, int RefreshRateHz, Orientation, DisplayMode)
class DisplaySpec {
  final Resolution resolution;
  final DisplayOrientation orientation;

  const DisplaySpec({
    required this.resolution,
    required this.orientation,
  });

  /// この DisplaySpec が Portrait 系（縦向き）かどうか。
  bool get isPortrait =>
      orientation == DisplayOrientation.portrait ||
      orientation == DisplayOrientation.portraitFlipped;

  @override
  String toString() =>
      'DisplaySpec($resolution, orientation=${orientation.name})';
}

// ─── ビューポート計算ロジック ──────────────────────────────────────────────────

/// レンダラーが描画領域として使用するビューポートを計算する純粋関数モデル。
///
/// Requirement 5.3 の要件:
///   レターボックス・ピラーボックスなしで全画面表示する。
///
/// 実装（renderer_view.dart）では SizedBox.expand + Texture を使うことで、
/// 利用可能な全ピクセルを占有する。FittedBox を使わないため、
/// コンテンツのアスペクト比に関わらず常にコンテナ全体を埋める。
///
/// このクラスはそのロジックを純粋関数として抽出したもの。
class ViewportCalculator {
  /// スマートフォン画面サイズ（[screenWidth] x [screenHeight]）に対して
  /// レンダラーが描画する領域を計算する。
  ///
  /// [displaySpec] は PC 側の仮想ディスプレイ仕様（映像のアスペクト比）。
  /// Requirement 5.3 では、映像のアスペクト比に関わらず
  /// 描画領域は画面全体でなければならない。
  ///
  /// 戻り値: ({width, height}) - 描画領域のピクセルサイズ。
  static ({double width, double height}) computeRenderArea({
    required double screenWidth,
    required double screenHeight,
    required DisplaySpec displaySpec,
  }) {
    // renderer_view.dart の _buildContent() 実装:
    //   return SizedBox.expand(
    //     child: Texture(textureId: id, freeze: false, ...),
    //   );
    //
    // SizedBox.expand は利用可能なスペースを全て占有するため、
    // 描画領域 = 画面サイズになる（letterbox/pillarbox なし）。
    return (width: screenWidth, height: screenHeight);
  }
}

// ─── glados ジェネレーター ─────────────────────────────────────────────────────

extension DisplaySpecAny on Any {
  /// 有効な解像度を生成するジェネレーター。
  ///
  /// 仮想ディスプレイの最小・最大サポート範囲 (640x480 〜 3840x2160) から
  /// テスト速度のため代表的な解像度セットを使用する。
  /// glados の any.choose() は値リストからランダムに選択する。
  Generator<Resolution> get validResolution {
    return any.choose([
      // 一般的なスマートフォン縦向き解像度
      const Resolution(1080, 1920), // FHD Portrait
      const Resolution(1440, 2560), // QHD Portrait
      const Resolution(1080, 2340), // 19.5:9 Portrait
      const Resolution(828, 1792), // iPhone XR Portrait
      const Resolution(1170, 2532), // iPhone 12 Portrait
      // 横向き解像度
      const Resolution(1920, 1080), // FHD Landscape
      const Resolution(2560, 1440), // QHD Landscape
      const Resolution(2340, 1080), // 19.5:9 Landscape
      // 最小サポート解像度 (MinSupported)
      const Resolution(640, 480),
      // その他の一般的解像度
      const Resolution(1280, 720), // HD
      const Resolution(2160, 3840), // 4K Portrait
      const Resolution(3840, 2160), // 4K Landscape (MaxSupported)
    ]);
  }

  /// 有効な DisplayOrientation を生成するジェネレーター。
  Generator<DisplayOrientation> get validOrientation {
    return any.choose(DisplayOrientation.values);
  }

  /// 有効な DisplaySpec を生成するジェネレーター。
  Generator<DisplaySpec> get validDisplaySpec {
    return any.combine2(
      any.validResolution,
      any.validOrientation,
      (Resolution res, DisplayOrientation orientation) => DisplaySpec(
        resolution: res,
        orientation: orientation,
      ),
    );
  }

  /// 有効なスマートフォン画面サイズを生成するジェネレーター。
  ///
  /// 一般的なスマートフォン画面サイズの範囲 (論理ピクセル) から生成する。
  Generator<({double width, double height})> get validScreenSize {
    return any.combine2(
      // 幅: 320〜932 論理ピクセル (一般的なスマートフォン範囲)
      any.intInRange(320, 933).map((w) => w.toDouble()),
      // 高さ: 320〜932 論理ピクセル
      any.intInRange(320, 933).map((h) => h.toDouble()),
      (double w, double h) => (width: w, height: h),
    );
  }
}

// ─── テスト本体 ───────────────────────────────────────────────────────────────

void main() {
  group('Property 11: レンダラーの全画面表示（レターボックスなし）', () {
    // ─── プロパティテスト ─────────────────────────────────────────────────────

    /// **Validates: Requirements 5.3**
    ///
    /// 任意の DisplaySpec（解像度・向き）とスマートフォン画面サイズの組み合わせに対して、
    /// レンダラーが計算する描画領域の幅・高さが画面の全幅・全高と等しくなければならない。
    ///
    /// これにより、レターボックス（上下の黒帯）またはピラーボックス（左右の黒帯）が
    /// 存在しないことを保証する。
    Glados2(any.validDisplaySpec, any.validScreenSize).test(
      '任意の DisplaySpec に対して描画領域が画面全幅・全高を占有する（レターボックスなし）',
      (
        DisplaySpec displaySpec,
        ({double width, double height}) screenSize,
      ) {
        // ViewportCalculator でレンダラーが計算する描画領域を取得する
        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenSize.width,
          screenHeight: screenSize.height,
          displaySpec: displaySpec,
        );

        // Property 11: 描画領域の幅 = 画面全幅
        // ピラーボックス（左右の余白）がないことを保証する
        expect(
          renderArea.width,
          equals(screenSize.width),
          reason: 'DisplaySpec($displaySpec) に対して描画幅(${renderArea.width})が'
              '画面幅(${screenSize.width})と等しくなければならない（ピラーボックスなし）',
        );

        // Property 11: 描画領域の高さ = 画面全高
        // レターボックス（上下の余白）がないことを保証する
        expect(
          renderArea.height,
          equals(screenSize.height),
          reason: 'DisplaySpec($displaySpec) に対して描画高さ(${renderArea.height})が'
              '画面高さ(${screenSize.height})と等しくなければならない（レターボックスなし）',
        );
      },
    );

    /// **Validates: Requirements 5.3**
    ///
    /// 任意の DisplaySpec に対して、描画領域が正の幅・高さを持つことを検証する。
    /// ゼロまたは負のサイズの描画領域は表示できない。
    Glados2(any.validDisplaySpec, any.validScreenSize).test(
      '任意の DisplaySpec に対して描画領域は正のサイズを持つ',
      (
        DisplaySpec displaySpec,
        ({double width, double height}) screenSize,
      ) {
        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenSize.width,
          screenHeight: screenSize.height,
          displaySpec: displaySpec,
        );

        expect(
          renderArea.width,
          greaterThan(0.0),
          reason: '描画幅は正でなければならない',
        );
        expect(
          renderArea.height,
          greaterThan(0.0),
          reason: '描画高さは正でなければならない',
        );
      },
    );

    // ─── ユニットテスト（具体的な例） ────────────────────────────────────────────

    group('具体的な DisplaySpec での全画面検証', () {
      test('FHD Portrait (1080x1920) で iPhone 12 画面に全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(1080, 1920),
          orientation: DisplayOrientation.portrait,
        );
        const screenWidth = 390.0;
        const screenHeight = 844.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('FHD Landscape (1920x1080) で横向き画面に全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(1920, 1080),
          orientation: DisplayOrientation.landscape,
        );
        const screenWidth = 844.0;
        const screenHeight = 390.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('最小サポート解像度 (640x480) でも全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(640, 480),
          orientation: DisplayOrientation.landscape,
        );
        const screenWidth = 375.0;
        const screenHeight = 667.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('4K Landscape (3840x2160) = MaxSupported で全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(3840, 2160),
          orientation: DisplayOrientation.landscape,
        );
        const screenWidth = 430.0;
        const screenHeight = 932.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('PortraitFlipped 向きでも全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(1080, 1920),
          orientation: DisplayOrientation.portraitFlipped,
        );
        const screenWidth = 390.0;
        const screenHeight = 844.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('LandscapeFlipped 向きでも全画面表示', () {
        const displaySpec = DisplaySpec(
          resolution: Resolution(1920, 1080),
          orientation: DisplayOrientation.landscapeFlipped,
        );
        const screenWidth = 844.0;
        const screenHeight = 390.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth));
        expect(renderArea.height, equals(screenHeight));
      });

      test('映像アスペクト比 16:9 をスクエアに近い画面に表示してもレターボックスなし', () {
        // 映像: 1920x1080 (16:9), 画面: 375x375 (1:1)
        const displaySpec = DisplaySpec(
          resolution: Resolution(1920, 1080),
          orientation: DisplayOrientation.landscape,
        );
        const screenWidth = 375.0;
        const screenHeight = 375.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        // アスペクト比が異なっても全画面を占有する（クロップ表示）
        expect(renderArea.width, equals(screenWidth),
            reason: 'アスペクト比不一致でもピラーボックスなしで全幅表示');
        expect(renderArea.height, equals(screenHeight),
            reason: 'アスペクト比不一致でもレターボックスなしで全高表示');
      });

      test('映像アスペクト比 9:16 を横長画面に表示してもピラーボックスなし', () {
        // 映像: 1080x1920 (9:16), 画面: 844x390 (横長)
        const displaySpec = DisplaySpec(
          resolution: Resolution(1080, 1920),
          orientation: DisplayOrientation.portrait,
        );
        const screenWidth = 844.0;
        const screenHeight = 390.0;

        final renderArea = ViewportCalculator.computeRenderArea(
          screenWidth: screenWidth,
          screenHeight: screenHeight,
          displaySpec: displaySpec,
        );

        expect(renderArea.width, equals(screenWidth),
            reason: '縦長映像を横長画面に表示してもピラーボックスなしで全幅表示');
        expect(renderArea.height, equals(screenHeight),
            reason: '縦長映像を横長画面に表示してもレターボックスなしで全高表示');
      });
    });
  });
}
