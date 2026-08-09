using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// 解像度・向き更新イベントに関するデータ。
/// </summary>
public class ResolutionUpdatedEventArgs : EventArgs
{
    /// <summary>対象の仮想ディスプレイハンドル。</summary>
    public required VirtualDisplayHandle Handle { get; init; }

    /// <summary>向き調整済みの有効解像度。</summary>
    public required Resolution Resolution { get; init; }

    /// <summary>適用された向き。</summary>
    public required Orientation Orientation { get; init; }
}
