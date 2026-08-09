using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>
/// 設定に応じて USB 直結 (AOA) または ADB トランスポートを生成するファクトリー。
/// </summary>
/// <remarks>
/// <see cref="UsbConnectionMode.WinUsb"/> は AOA を指す。
/// アクセサリーモードの端末は Windows 側では WinUSB に束縛されるため、
/// 設定上の名前はそのままにしてある。
/// </remarks>
public static class UsbTransportFactory
{
    /// <summary>
    /// 設定とデバイスの接続状況に基づいて適切なトランスポートを返す。
    /// </summary>
    /// <param name="mode">設定から読み込んだ USB 接続モード。</param>
    /// <returns>使用するトランスポートインスタンス。</returns>
    public static ITransport Create(UsbConnectionMode mode)
    {
        return mode switch
        {
            UsbConnectionMode.WinUsb => new AoaTransport(),
            UsbConnectionMode.Adb    => new UsbTransport(),
            _                        => new AoaTransport()
        };
    }

    /// <summary>
    /// 自動検出: AOA で掴めそうな端末があれば USB 直結、
    /// なければ ADB にフォールバックする。
    /// </summary>
    public static ITransport CreateAuto()
    {
        if (AoaTransport.IsDeviceAvailable())
            return new AoaTransport();

        return new UsbTransport();
    }

    /// <summary>
    /// 現在のデバイス接続状況と ADB の有無から推奨モードを返す。
    /// 設定画面でのヒント表示に使用する。
    /// </summary>
    public static UsbConnectionMode GetRecommendedMode()
    {
        if (AoaTransport.IsDeviceAvailable())
            return UsbConnectionMode.WinUsb;

        if (IsAdbAvailable())
            return UsbConnectionMode.Adb;

        return UsbConnectionMode.WinUsb; // デフォルト
    }

    /// <summary>
    /// adb コマンドが PATH に存在するか確認する。
    /// </summary>
    public static bool IsAdbAvailable()
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("adb", "version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };
            proc.Start();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
