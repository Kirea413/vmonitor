// Feature: vmonitor, Property 12: 手動解像度指定の優先

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Driver;

namespace VMonitor.Tests;

/// <summary>
/// Property 12: 手動解像度指定の優先
/// Validates: Requirements 5.4
///
/// 任意の自動検出解像度と手動指定解像度の組み合わせに対して、
/// 手動解像度が指定されている場合、仮想ディスプレイは手動指定値を使用しなければならない。
/// </summary>
public class ManualResolutionPriorityPropertyTests
{
    // サポート解像度の範囲（FsCheck が生成する整数をこの範囲に正規化する）
    private const int MinDim = 640;
    private const int MaxDim = 3840;

    /// <summary>
    /// 有効な解像度値（640〜3840）に正規化するヘルパー。
    /// FsCheck の任意整数から決定的に有効範囲の値を生成する。
    /// </summary>
    private static int NormalizeDim(int raw) =>
        MinDim + Math.Abs(raw) % (MaxDim - MinDim + 1);

    /// <summary>
    /// Property 12a: 手動解像度が指定されている場合、選択された解像度は手動指定値と等しくなければならない。
    ///
    /// 任意の自動検出解像度と手動指定解像度の組み合わせに対して、
    /// ResolutionSelector.Select の返り値は手動指定解像度と一致しなければならない。
    ///
    /// パラメーター:
    ///   autoW / autoH   - 自動検出解像度の幅・高さ（640〜3840 に正規化）
    ///   manualW / manualH - 手動指定解像度の幅・高さ（640〜3840 に正規化）
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ManualResolutionTakesPriorityOverAutoDetected(
        int autoW,
        int autoH,
        int manualW,
        int manualH)
    {
        var autoDetected = new Resolution(NormalizeDim(autoW), NormalizeDim(autoH));
        var manual = new Resolution(NormalizeDim(manualW), NormalizeDim(manualH));

        var selected = ResolutionSelector.Select(autoDetected, manual);

        return selected == manual;
    }

    /// <summary>
    /// Property 12b: 手動解像度が指定されていない場合（null）、自動検出解像度がそのまま使われなければならない。
    ///
    /// 手動指定が null のとき、ResolutionSelector.Select の返り値は自動検出解像度と一致しなければならない。
    ///
    /// パラメーター:
    ///   autoW / autoH - 自動検出解像度の幅・高さ（640〜3840 に正規化）
    /// </summary>
    [Property(MaxTest = 100)]
    public bool AutoDetectedResolutionIsUsedWhenNoManualOverride(int autoW, int autoH)
    {
        var autoDetected = new Resolution(NormalizeDim(autoW), NormalizeDim(autoH));

        var selected = ResolutionSelector.Select(autoDetected, manualResolution: null);

        return selected == autoDetected;
    }

    /// <summary>
    /// Property 12c: 手動解像度は自動検出解像度の値に関わらず常に優先される。
    ///
    /// 自動検出解像度と手動指定解像度が同一でも異なっていても、
    /// 手動指定がある場合はその値が選択されなければならない。
    /// このプロパティは特に autoDetected == manual の場合も正しく動作することを確認する。
    ///
    /// パラメーター:
    ///   rawDim - 幅・高さ両方に使う共通の整数（autoDetected と manual が同値になるケース）
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ManualResolutionPrevailsEvenWhenEqualToAutoDetected(int rawDim)
    {
        var dim = NormalizeDim(rawDim);
        var resolution = new Resolution(dim, dim);

        // autoDetected と manual が同じ値のとき
        var selected = ResolutionSelector.Select(resolution, resolution);

        // 手動指定値が返されなければならない（同一値なので等価チェックで確認する）
        return selected == resolution;
    }
}
