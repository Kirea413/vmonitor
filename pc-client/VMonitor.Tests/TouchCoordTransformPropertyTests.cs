// Feature: vmonitor, Property 18: 向き変更後のタッチ座標変換の正確さ

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Session.Input;

namespace VMonitor.Tests;

/// <summary>
/// Property 18: 向き変更後のタッチ座標変換の正確さ
/// Validates: Requirements 6.6
///
/// 任意の Orientation と正規化タッチ座標 (x, y) に対して、
/// 変換後の座標が仮想ディスプレイ解像度における正しいピクセル位置と一致しなければならない。
///
/// 変換式:
///   Portrait:         (x*W, y*H)
///   Landscape:        (y*W, (1-x)*H)
///   PortraitFlipped:  ((1-x)*W, (1-y)*H)
///   LandscapeFlipped: ((1-y)*W, x*H)
/// </summary>
public class TouchCoordTransformPropertyTests
{
    // ── ヘルパー ────────────────────────────────────────────────────────────

    /// <summary>
    /// FsCheck の任意整数から有効な解像度幅・高さ（640〜1920）を生成するヘルパー。
    /// テストの速度を考慮し最大値を 1920 に制限する。
    /// </summary>
    private static int NormalizeDim(int raw) =>
        640 + Math.Abs(raw) % (1920 - 640 + 1);

    /// <summary>
    /// FsCheck の任意整数から正規化座標 [0.0, 1.0] を生成するヘルパー。
    /// 10001 段階（0.0, 0.0001, …, 1.0）に量子化する。
    /// </summary>
    private static double NormalizeCoord(int raw) =>
        Math.Abs(raw) % 10001 / 10000.0;

    /// <summary>
    /// テスト対象の WindowsInkInjector を生成する。
    ///
    /// 座標変換だけを検証するテストなので、実際に OS へ注入しない
    /// 記録用バックエンドを使う。
    /// </summary>
    private static WindowsInkInjector CreateSut() =>
        new(new RecordingPointerInjectionBackend(), ownsBackend: true);

    /// <summary>
    /// 向きに応じた期待ピクセル座標を計算する（float 精度・クランプあり）。
    /// WindowsInkInjector.TransformPoint と同じロジックを独立実装する。
    /// </summary>
    private static (int ExpectedX, int ExpectedY) ComputeExpected(
        double x, double y, int w, int h, Orientation orientation)
    {
        float fx = (float)x;
        float fy = (float)y;
        float fw = (float)w;
        float fh = (float)h;

        (float rawX, float rawY) = orientation switch
        {
            Orientation.Portrait         => (fx * fw,          fy * fh),
            Orientation.Landscape        => (fy * fw,          (1f - fx) * fh),
            Orientation.PortraitFlipped  => ((1f - fx) * fw,   (1f - fy) * fh),
            Orientation.LandscapeFlipped => ((1f - fy) * fw,   fx * fh),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation))
        };

        int pixelX = (int)Math.Clamp(rawX, 0f, fw - 1f);
        int pixelY = (int)Math.Clamp(rawY, 0f, fh - 1f);
        return (pixelX, pixelY);
    }

    // ── Property 18-A: Portrait ─────────────────────────────────────────────

    /// <summary>
    /// Property 18-A: Portrait 向きで任意の正規化座標 (x, y) を変換した結果が
    /// (x*W, y*H) に一致しなければならない。
    ///
    /// Validates: Requirements 6.6
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Portrait_TransformMatchesExpected(int rawW, int rawH, int rawX, int rawY)
    {
        int w = NormalizeDim(rawW);
        int h = NormalizeDim(rawH);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), Orientation.Portrait);
        var (actualX, actualY) = injector.TransformPoint(x, y);

        var (expectedX, expectedY) = ComputeExpected(x, y, w, h, Orientation.Portrait);

        return actualX == expectedX && actualY == expectedY;
    }

    // ── Property 18-B: Landscape ───────────────────────────────────────────

    /// <summary>
    /// Property 18-B: Landscape 向きで任意の正規化座標 (x, y) を変換した結果が
    /// (y*W, (1-x)*H) に一致しなければならない。
    ///
    /// Validates: Requirements 6.6
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Landscape_TransformMatchesExpected(int rawW, int rawH, int rawX, int rawY)
    {
        int w = NormalizeDim(rawW);
        int h = NormalizeDim(rawH);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), Orientation.Landscape);
        var (actualX, actualY) = injector.TransformPoint(x, y);

        var (expectedX, expectedY) = ComputeExpected(x, y, w, h, Orientation.Landscape);

        return actualX == expectedX && actualY == expectedY;
    }

    // ── Property 18-C: PortraitFlipped ─────────────────────────────────────

    /// <summary>
    /// Property 18-C: PortraitFlipped 向きで任意の正規化座標 (x, y) を変換した結果が
    /// ((1-x)*W, (1-y)*H) に一致しなければならない。
    ///
    /// Validates: Requirements 6.6
    /// </summary>
    [Property(MaxTest = 100)]
    public bool PortraitFlipped_TransformMatchesExpected(int rawW, int rawH, int rawX, int rawY)
    {
        int w = NormalizeDim(rawW);
        int h = NormalizeDim(rawH);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), Orientation.PortraitFlipped);
        var (actualX, actualY) = injector.TransformPoint(x, y);

        var (expectedX, expectedY) = ComputeExpected(x, y, w, h, Orientation.PortraitFlipped);

        return actualX == expectedX && actualY == expectedY;
    }

    // ── Property 18-D: LandscapeFlipped ────────────────────────────────────

    /// <summary>
    /// Property 18-D: LandscapeFlipped 向きで任意の正規化座標 (x, y) を変換した結果が
    /// ((1-y)*W, x*H) に一致しなければならない。
    ///
    /// Validates: Requirements 6.6
    /// </summary>
    [Property(MaxTest = 100)]
    public bool LandscapeFlipped_TransformMatchesExpected(int rawW, int rawH, int rawX, int rawY)
    {
        int w = NormalizeDim(rawW);
        int h = NormalizeDim(rawH);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), Orientation.LandscapeFlipped);
        var (actualX, actualY) = injector.TransformPoint(x, y);

        var (expectedX, expectedY) = ComputeExpected(x, y, w, h, Orientation.LandscapeFlipped);

        return actualX == expectedX && actualY == expectedY;
    }

    // ── Property 18-E: 全向きで座標がディスプレイ範囲内に収まる ─────────────

    /// <summary>
    /// Property 18-E: 任意の Orientation と正規化座標に対して、
    /// 変換後のピクセル座標が仮想ディスプレイの解像度範囲内（[0, W-1] × [0, H-1]）に
    /// 収まらなければならない（クランプ保証）。
    ///
    /// Validates: Requirements 6.6
    /// </summary>
    [Property(MaxTest = 100)]
    public bool TransformedCoordIsWithinDisplayBounds(
        int rawW, int rawH, int rawX, int rawY, int rawOrientation)
    {
        int w = NormalizeDim(rawW);
        int h = NormalizeDim(rawH);
        double x = NormalizeCoord(rawX);
        double y = NormalizeCoord(rawY);

        // 4 つの向きを順番に使い回す
        var orientation = (Orientation)(Math.Abs(rawOrientation) % 4);

        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(w, h), orientation);
        var (actualX, actualY) = injector.TransformPoint(x, y);

        return actualX >= 0 && actualX < w
            && actualY >= 0 && actualY < h;
    }

    // ── 具体的なユニットテスト（代表値・境界値） ───────────────────────────

    /// <summary>Portrait: (0.5, 0.5) を 1920×1080 で変換すると (960, 540) になること。</summary>
    [Fact]
    public void Portrait_Center_ConvertsToExpectedPixel()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.Portrait);

        var (x, y) = injector.TransformPoint(0.5, 0.5);

        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    /// <summary>Landscape: (0.5, 0.5) を 1920×1080 で変換すると (960, 540) になること。</summary>
    [Fact]
    public void Landscape_Center_ConvertsToExpectedPixel()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.Landscape);

        // Landscape: x' = y*W = 0.5*1920 = 960, y' = (1-x)*H = 0.5*1080 = 540
        var (x, y) = injector.TransformPoint(0.5, 0.5);

        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    /// <summary>Landscape: (0.0, 0.0) を 1920×1080 で変換すると (0, 1079) になること。</summary>
    [Fact]
    public void Landscape_Origin_ConvertsToBottomLeft()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.Landscape);

        // Landscape: x' = y*W = 0*1920 = 0, y' = (1-x)*H = 1*1080 = 1080 → clamp → 1079
        var (x, y) = injector.TransformPoint(0.0, 0.0);

        Assert.Equal(0, x);
        Assert.Equal(1079, y);
    }

    /// <summary>PortraitFlipped: (0.0, 0.0) を 1920×1080 で変換すると (1919, 1079) になること。</summary>
    [Fact]
    public void PortraitFlipped_Origin_ConvertsToBottomRight()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.PortraitFlipped);

        // PortraitFlipped: x' = (1-x)*W = 1*1920 = 1920 → clamp → 1919
        //                  y' = (1-y)*H = 1*1080 = 1080 → clamp → 1079
        var (x, y) = injector.TransformPoint(0.0, 0.0);

        Assert.Equal(1919, x);
        Assert.Equal(1079, y);
    }

    /// <summary>LandscapeFlipped: (0.0, 1.0) を 1920×1080 で変換すると (0, 0) になること。</summary>
    [Fact]
    public void LandscapeFlipped_BottomLeft_ConvertsToTopLeft()
    {
        var injector = CreateSut();
        injector.UpdateTransform(new Resolution(1920, 1080), Orientation.LandscapeFlipped);

        // LandscapeFlipped: x' = (1-y)*W = 0*1920 = 0, y' = x*H = 0*1080 = 0
        var (x, y) = injector.TransformPoint(0.0, 1.0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}
