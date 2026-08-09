// Feature: vmonitor, Property 13: 解像度フォールバックの最近傍保証

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Driver;

namespace VMonitor.Tests;

/// <summary>
/// Property 13: 解像度フォールバックの最近傍保証
/// Validates: Requirements 5.5
///
/// 任意のサポート範囲外の解像度に対して、フォールバック後の解像度は
/// サポート済みリストに含まれ、かつ入力解像度との距離が最小でなければならない。
/// </summary>
public class ResolutionFallbackNearestPropertyTests
{
    // サポート範囲外となる境界値（MinSupported より小さいか MaxSupported より大きい）
    // Width: MinSupported.Width = 640, MaxSupported.Width = 3840
    // Height: MinSupported.Height = 480, MaxSupported.Height = 2160

    /// <summary>
    /// サポート範囲外の幅・高さを生成するヘルパー。
    /// raw が正の場合は MaxSupported + (raw % 5000 + 1) で上限超過、
    /// raw が負の場合は Max(1, MinSupported - (|raw| % 300 + 1)) で下限未満を生成する。
    /// </summary>
    private static int OutOfRangeDim(int raw, int minSupported, int maxSupported)
    {
        // 0 を避けるため絶対値 + 1 で処理する
        int abs = Math.Abs(raw) + 1;
        if (raw >= 0)
        {
            // 上限超過: maxSupported + 1 以上
            return maxSupported + (abs % 5000) + 1;
        }
        else
        {
            // 下限未満: minSupported - 1 以下 (最小 1)
            int below = minSupported - (abs % (minSupported - 1 + 1));
            return Math.Max(1, below);
        }
    }

    /// <summary>
    /// Property 13a: フォールバック後の解像度はサポート済みリストに含まれなければならない。
    ///
    /// サポート範囲外の解像度に対して Evaluate を呼び出したとき、
    /// EffectiveResolution が SupportedResolutions リストのいずれかと一致しなければならない。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FallbackResolutionIsInSupportedList(int rawW, int rawH)
    {
        // サポート範囲外の解像度を生成（Width を上限超過にする）
        int outW = Resolution.MaxSupported.Width + (Math.Abs(rawW) % 5000) + 1;
        int outH = Math.Max(1, Resolution.MinSupported.Height - (Math.Abs(rawH) % 200) - 1);

        var requested = new Resolution(outW, outH);

        // 範囲外であることを前提条件として確認する
        if (ResolutionFallbackService.IsInSupportedRange(requested))
            return true; // 前提条件が成立しない場合はスキップ（vaild test case の絞り込み）

        var result = new ResolutionFallbackService().Evaluate(requested);

        return ResolutionFallbackService.SupportedResolutions.Contains(result.EffectiveResolution);
    }

    /// <summary>
    /// Property 13b: フォールバック後の解像度は入力との距離が最小でなければならない。
    ///
    /// サポート範囲外の任意の解像度に対して、フォールバック解像度は
    /// サポート済みリスト内のすべての解像度の中でユークリッド距離が最小のものでなければならない。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FallbackResolutionHasMinimumDistanceToInput(int rawW, int rawH)
    {
        // 任意のサポート範囲外解像度を生成する
        // Width を MaxSupported 超過にする
        int outW = Resolution.MaxSupported.Width + (Math.Abs(rawW) % 5000) + 1;
        // Height は MinSupported 未満にする
        int outH = Math.Max(1, Resolution.MinSupported.Height - (Math.Abs(rawH) % 200) - 1);

        var requested = new Resolution(outW, outH);

        if (ResolutionFallbackService.IsInSupportedRange(requested))
            return true; // 前提条件が成立しない場合はスキップ

        var result = new ResolutionFallbackService().Evaluate(requested);
        var fallback = result.EffectiveResolution;

        long fallbackDist = SquaredDistance(fallback, requested);

        // フォールバック解像度はサポート済みリスト内で最小距離でなければならない
        foreach (var supported in ResolutionFallbackService.SupportedResolutions)
        {
            long dist = SquaredDistance(supported, requested);
            if (dist < fallbackDist)
                return false; // より近いサポート解像度が存在してしまう
        }

        return true;
    }

    /// <summary>
    /// Property 13c: フォールバックが発生した場合、FallbackOccurred は true でなければならない。
    ///
    /// サポート範囲外の解像度を Evaluate すると FallbackOccurred == true が返されなければならない。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FallbackOccurredIsTrueForOutOfRangeResolution(int rawW, int rawH)
    {
        // Width を下限未満、Height を上限超過にして確実にサポート範囲外とする
        int outW = Math.Max(1, Resolution.MinSupported.Width - (Math.Abs(rawW) % 300) - 1);
        int outH = Resolution.MaxSupported.Height + (Math.Abs(rawH) % 5000) + 1;

        var requested = new Resolution(outW, outH);

        if (ResolutionFallbackService.IsInSupportedRange(requested))
            return true; // 前提条件が成立しない場合はスキップ

        var result = new ResolutionFallbackService().Evaluate(requested);

        return result.FallbackOccurred;
    }

    /// <summary>
    /// Property 13d: フォールバックが発生した場合、NotificationMessage は非空でなければならない。
    ///
    /// 要件 5.5 の「ユーザーに通知する」を検証する。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FallbackNotificationMessageIsNonEmptyWhenFallbackOccurs(int rawW, int rawH)
    {
        int outW = Resolution.MaxSupported.Width + (Math.Abs(rawW) % 5000) + 1;
        int outH = Resolution.MaxSupported.Height + (Math.Abs(rawH) % 5000) + 1;

        var requested = new Resolution(outW, outH);

        if (ResolutionFallbackService.IsInSupportedRange(requested))
            return true; // 前提条件が成立しない場合はスキップ

        var result = new ResolutionFallbackService().Evaluate(requested);

        // フォールバック発生時は通知メッセージが空でないこと
        return result.FallbackOccurred && !string.IsNullOrEmpty(result.NotificationMessage);
    }

    /// <summary>
    /// Property 13e: サポート範囲内の解像度はフォールバックされずそのまま返されなければならない。
    ///
    /// IsInSupportedRange == true の解像度に対して、FallbackOccurred == false かつ
    /// EffectiveResolution == requested が成立しなければならない（逆方向の健全性検証）。
    /// </summary>
    [Property(MaxTest = 100)]
    public bool InRangeResolutionIsNotFallenBack(int rawW, int rawH)
    {
        // 640〜3840 の範囲に正規化する
        int w = Resolution.MinSupported.Width
              + Math.Abs(rawW) % (Resolution.MaxSupported.Width - Resolution.MinSupported.Width + 1);
        int h = Resolution.MinSupported.Height
              + Math.Abs(rawH) % (Resolution.MaxSupported.Height - Resolution.MinSupported.Height + 1);

        var requested = new Resolution(w, h);

        var result = new ResolutionFallbackService().Evaluate(requested);

        return !result.FallbackOccurred && result.EffectiveResolution == requested;
    }

    // ユークリッド距離の二乗（ResolutionFallbackService.FindNearest と同じ計算式）
    private static long SquaredDistance(Resolution a, Resolution b)
    {
        long dw = (long)a.Width  - b.Width;
        long dh = (long)a.Height - b.Height;
        return dw * dw + dh * dh;
    }
}
