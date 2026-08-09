using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// 仮想ディスプレイの追加・削除イベントに関するデータ。
/// </summary>
public class DisplayEventArgs : EventArgs
{
    /// <summary>対象の仮想ディスプレイハンドル。</summary>
    public required VirtualDisplayHandle Handle { get; init; }

    /// <summary>対象の仮想ディスプレイ仕様。</summary>
    public required DisplaySpec Spec { get; init; }
}
