using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// サポート範囲外の解像度を最近傍のサポート解像度へフォールバックするサービス。
/// 要件 5.5: 自動検出した解像度がサポート範囲外の場合、最も近いサポート解像度にフォールバックしユーザーに通知する。
/// </summary>
public class ResolutionFallbackService
{
    /// <summary>
    /// 標準サポート解像度リスト（ランドスケープ基準、Portrait は縦横入れ替えで対応）。
    /// </summary>
    public static readonly IReadOnlyList<Resolution> SupportedResolutions = new List<Resolution>
    {
        // ランドスケープ
        new(640,  480),
        new(1280, 720),
        new(1920, 1080),
        new(2560, 1440),
        new(3840, 2160),
        // ポートレート
        new(480,  640),
        new(720,  1280),
        new(1080, 1920),
        new(1440, 2560),
        new(2160, 3840),
    }.AsReadOnly();

    /// <summary>
    /// 要求解像度がサポート範囲内かどうかを返す。
    /// サポート範囲は MinSupported (640×480) から MaxSupported (3840×2160) まで。
    /// Width と Height の両方がそれぞれの最小値以上かつ最大値以下である必要がある。
    /// </summary>
    /// <param name="resolution">検査する解像度。</param>
    /// <returns>サポート範囲内なら true。</returns>
    public static bool IsInSupportedRange(Resolution resolution)
    {
        return resolution.Width  >= Resolution.MinSupported.Width
            && resolution.Height >= Resolution.MinSupported.Height
            && resolution.Width  <= Resolution.MaxSupported.Width
            && resolution.Height <= Resolution.MaxSupported.Height;
    }

    /// <summary>
    /// 要求解像度を評価し、サポート範囲内ならそのまま返す。
    /// 範囲外なら最近傍サポート解像度を選択し、フォールバック通知を付けて返す。
    /// </summary>
    /// <param name="requested">要求解像度。</param>
    /// <returns>
    /// <see cref="ResolutionFallbackResult"/>。
    /// <list type="bullet">
    ///   <item><see cref="ResolutionFallbackResult.EffectiveResolution"/>: 実際に使用する解像度。</item>
    ///   <item><see cref="ResolutionFallbackResult.FallbackOccurred"/>: フォールバックが発生したかどうか。</item>
    ///   <item><see cref="ResolutionFallbackResult.NotificationMessage"/>: フォールバック時のユーザー通知メッセージ（null なら通知不要）。</item>
    /// </list>
    /// </returns>
    public ResolutionFallbackResult Evaluate(Resolution requested)
    {
        if (IsInSupportedRange(requested))
        {
            return new ResolutionFallbackResult(
                EffectiveResolution: requested,
                FallbackOccurred: false,
                NotificationMessage: null);
        }

        var nearest = FindNearest(requested);

        var message =
            $"要求された解像度 {requested.Width}×{requested.Height} はサポート範囲外です。" +
            $"最近傍のサポート解像度 {nearest.Width}×{nearest.Height} を使用します。";

        return new ResolutionFallbackResult(
            EffectiveResolution: nearest,
            FallbackOccurred: true,
            NotificationMessage: message);
    }

    /// <summary>
    /// サポート解像度リストから、要求解像度に最も近い解像度を返す。
    /// 距離は Width と Height の差の二乗和（ユークリッド距離の二乗）で計算する。
    /// </summary>
    /// <param name="requested">要求解像度。</param>
    /// <returns>最近傍のサポート解像度。</returns>
    public static Resolution FindNearest(Resolution requested)
    {
        return SupportedResolutions
            .OrderBy(r => SquaredDistance(r, requested))
            .First();
    }

    /// <summary>
    /// 2 解像度間のユークリッド距離の二乗を返す。
    /// </summary>
    private static long SquaredDistance(Resolution a, Resolution b)
    {
        long dw = (long)a.Width  - b.Width;
        long dh = (long)a.Height - b.Height;
        return dw * dw + dh * dh;
    }
}
