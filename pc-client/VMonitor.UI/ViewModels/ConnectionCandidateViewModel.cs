using System.ComponentModel;
using System.Runtime.CompilerServices;
using VMonitor.Core.Models;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// 接続候補リストの 1 エントリーを表すビューモデル。
/// </summary>
public sealed class ConnectionCandidateViewModel : INotifyPropertyChanged
{
    /// <summary>候補として表示するデバイス情報。</summary>
    public DeviceInfo Device { get; private set; }

    /// <summary>デバイス名（UI 表示用）。</summary>
    public string Name => Device.Name;

    /// <summary>プラットフォーム（iOS / Android）の文字列表現。</summary>
    public string Platform => Device.Platform.ToString();

    /// <summary>デバイスの物理解像度の文字列表現。</summary>
    public string Resolution => $"{Device.PhysicalResolution.Width} × {Device.PhysicalResolution.Height}";

    /// <summary>
    /// この端末との繋ぎ方。
    /// </summary>
    /// <remarks>
    /// 表示用の文字列しか持っていなかったため、繋ぐときに
    /// USB と Wi-Fi を見分けられなかった。値そのものを持つ。
    /// </remarks>
    public TransportType Transport { get; }

    /// <summary>接続種別の表示名。</summary>
    public string TransportLabel => Transport == TransportType.USB ? "USB" : "Wi-Fi";

    /// <summary>旧名。既存の束縛が参照している。</summary>
    public string TransportIcon => TransportLabel;

    /// <summary>この端末のセッションが現在動作中か。</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public string StatusLabel => IsConnected ? "接続中" : "接続待ち";

    private bool _isConnected;

    public ConnectionCandidateViewModel(DeviceInfo device, TransportType transport)
    {
        Device    = device ?? throw new ArgumentNullException(nameof(device));
        Transport = transport;
    }

    public void UpdateDevice(DeviceInfo device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Platform));
        OnPropertyChanged(nameof(Resolution));
    }

    public void SetConnected(bool connected) => IsConnected = connected;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
