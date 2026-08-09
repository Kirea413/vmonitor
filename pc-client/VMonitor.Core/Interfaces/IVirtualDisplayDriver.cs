using VMonitor.Core.Models;

namespace VMonitor.Core.Interfaces;

/// <summary>
/// 仮想ディスプレイドライバの管理インターフェース。
/// IddCx (Indirect Display Driver) ベースの仮想モニターを OS に登録・制御する。
/// </summary>
public interface IVirtualDisplayDriver
{
    /// <summary>ドライバを DriverStore にインストールする（インストーラーから呼び出し）。</summary>
    Task InstallAsync();

    /// <summary>ドライバを DriverStore からアンインストールする。</summary>
    Task UninstallAsync();

    /// <summary>指定のディスプレイ仕様で仮想ディスプレイを作成し、ハンドルを返す。</summary>
    Task<VirtualDisplayHandle> CreateDisplayAsync(DisplaySpec spec);

    /// <summary>指定ハンドルの仮想ディスプレイを削除する。</summary>
    Task RemoveDisplayAsync(VirtualDisplayHandle handle);

    /// <summary>仮想ディスプレイの解像度と向きを更新する。</summary>
    Task UpdateResolutionAsync(VirtualDisplayHandle handle, Resolution resolution, Orientation orientation);

    /// <summary>仮想ディスプレイのフレームを非同期ストリームとして取得する（ストリーマーが呼び出す）。</summary>
    IAsyncEnumerable<VideoFrame> GetFramesAsync(VirtualDisplayHandle handle, CancellationToken ct);

    /// <summary>
    /// 仮想ディスプレイの解像度・向きが更新されたときに発生するイベント。
    /// タッチ入力インジェクターなどがこのイベントを購読し、変換行列を更新する。
    /// </summary>
    event EventHandler<DisplayResolutionUpdatedEventArgs>? ResolutionUpdated;
}

/// <summary>
/// 解像度・向き更新イベントのデータ。
/// <see cref="IVirtualDisplayDriver.ResolutionUpdated"/> イベントで使用する。
/// </summary>
public class DisplayResolutionUpdatedEventArgs : EventArgs
{
    /// <summary>対象の仮想ディスプレイハンドル。</summary>
    public required VirtualDisplayHandle Handle { get; init; }

    /// <summary>向き調整済みの有効解像度。</summary>
    public required Resolution Resolution { get; init; }

    /// <summary>適用された向き。</summary>
    public required Orientation Orientation { get; init; }
}
