using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// 解像度フォールバック評価の結果を表すレコード。
/// </summary>
/// <param name="EffectiveResolution">実際に使用する解像度。</param>
/// <param name="FallbackOccurred">フォールバックが発生したかどうか。</param>
/// <param name="NotificationMessage">フォールバック時のユーザー通知メッセージ（通知不要なら null）。</param>
public record ResolutionFallbackResult(
    Resolution EffectiveResolution,
    bool FallbackOccurred,
    string? NotificationMessage);
