using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// スマホから受信したタッチイベントを Windows Ink API (InjectTouchInput) で
/// Windows に注入するインターフェース。
/// </summary>
public interface IWindowsInkInjector
{
    /// <summary>
    /// タッチポイントのリストを Windows Ink API 経由で注入する。
    /// 座標変換には <see cref="UpdateTransform"/> で設定された行列を使用する。
    /// </summary>
    void InjectTouch(IReadOnlyList<TouchPoint> points, DisplayTransform transform);

    /// <summary>
    /// 画面向きや解像度変更時に座標変換行列を更新する。
    /// スマホの正規化座標 [0.0, 1.0] を仮想ディスプレイのピクセル座標に変換するために使用する。
    /// </summary>
    void UpdateTransform(Resolution displayResolution, Orientation orientation);
}

/// <summary>スマホ正規化座標から仮想ディスプレイピクセル座標への変換情報。</summary>
public record DisplayTransform(
    Resolution DisplayResolution,
    Orientation Orientation
);
