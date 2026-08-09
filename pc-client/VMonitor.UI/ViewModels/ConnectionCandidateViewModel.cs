using VMonitor.Core.Models;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// 接続候補リストの 1 エントリーを表すビューモデル。
/// </summary>
public sealed class ConnectionCandidateViewModel
{
    /// <summary>候補として表示するデバイス情報。</summary>
    public DeviceInfo Device { get; }

    /// <summary>デバイス名（UI 表示用）。</summary>
    public string Name => Device.Name;

    /// <summary>プラットフォーム（iOS / Android）の文字列表現。</summary>
    public string Platform => Device.Platform.ToString();

    /// <summary>デバイスの物理解像度の文字列表現。</summary>
    public string Resolution => $"{Device.PhysicalResolution.Width} × {Device.PhysicalResolution.Height}";

    /// <summary>接続種別のアイコンテキスト（Wi-Fi は 📶、USB は 🔌）。</summary>
    public string TransportIcon { get; }

    public ConnectionCandidateViewModel(DeviceInfo device, TransportType transport)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        TransportIcon = transport == TransportType.USB ? "🔌 USB" : "📶 Wi-Fi";
    }
}
