using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("VMonitor.Tests")]

namespace VMonitor.Session.Transport;

/// <summary>
/// libusb がどの経路で USB へ届くかを決める。
/// </summary>
/// <remarks>
/// <para>
/// Windows は、ドライバの当たっていない USB デバイスをユーザーモードの
/// アプリに触らせない。既定の経路 (WinUSB) では、WinUSB が割り当てられて
/// いるデバイスしか開けない。
/// </para>
/// <para>
/// ところが通常モードの Android は、たいてい MTP ドライバの持ち物に
/// なっている。実機では Pixel 9a が
/// <c>VID=18D1 PID=4EE1 ドライバ=WUDFWpdMtp</c> で出ていた。この状態では
/// AOA の切り替え指示 (ベンダーリクエスト 51/52/53) すら送れない。
/// 切り替えが始まってもいないので、以降の仕組みは何も働かない。
/// </para>
/// <para>
/// UsbDk はこれを迂回する。既存のドライバを外さずに USB へ到達できる
/// ので、MTP を壊さずに済む。同梱している libusb は UsbDk 経路を
/// 最初から持っており、こちらは「使う」と指定するだけでよい。
/// </para>
/// <para>
/// 入っていない環境では今までどおり既定の経路で動く。UsbDk を前提に
/// すると、いま使えている人が使えなくなる。
/// </para>
/// </remarks>
public static class LibUsbBackend
{
    /// <summary>libusb のオプション番号。libusb.h の LIBUSB_OPTION_USE_USBDK。</summary>
    private const int OptionUseUsbDk = 1;

    private static readonly object Gate = new();
    private static bool _decided;
    private static bool _usbDkEnabled;

    /// <summary>
    /// UsbDk 経由になっているか。<see cref="Prepare"/> を呼ぶまでは false。
    /// </summary>
    public static bool IsUsbDkEnabled
    {
        get { lock (Gate) return _usbDkEnabled; }
    }

    /// <summary>
    /// UsbDk が入っているかを調べる差し替え口（テスト用）。
    /// </summary>
    internal static Func<bool> UsbDkDetector { get; set; } = IsUsbDkInstalled;

    /// <summary>
    /// libusb を使い始める前に一度だけ呼ぶ。
    /// </summary>
    /// <remarks>
    /// libusb のオプションは、文脈を作る前に決めておく必要がある。
    /// 既に作った文脈には後から効かない。
    /// </remarks>
    public static void Prepare()
    {
        lock (Gate)
        {
            if (_decided) return;
            _decided = true;

            if (!OperatingSystem.IsWindows()) return;

            // ここから先で投げると、USB 接続そのものが始まらなくなる。
            // 経路を決めるだけの処理なので、何が起きても既定へ倒す。
            try
            {
                // 入っていないのに指定すると、libusb の初期化そのものが
                // 失敗する。いま使えている人を巻き添えにしない。
                if (!UsbDkDetector()) return;

                // 文脈を指定しない呼び出しは、以降に作る文脈すべてに効く。
                int rc = libusb_set_option(IntPtr.Zero, OptionUseUsbDk);
                _usbDkEnabled = rc == 0;
            }
            catch (Exception)
            {
                // 古い libusb には無い。調べる側が壊れることもある。
                // どちらも既定の経路で続けられる。
                _usbDkEnabled = false;
            }
        }
    }

    /// <summary>いまの経路を人が読める形で返す（記録用）。</summary>
    public static string Describe()
        => IsUsbDkEnabled
            ? "UsbDk 経由"
            : "WinUSB 経由（UsbDk は使っていません）";

    /// <summary>
    /// UsbDk が入っているかを調べる。
    /// </summary>
    /// <remarks>
    /// libusb は UsbDkHelper.dll を実行時に読みに行く。これが在るか
    /// どうかが、そのまま使えるかどうかになる。サービスの登録だけを
    /// 見ると、消し残しを拾ってしまうことがある。
    /// </remarks>
    private static bool IsUsbDkInstalled()
    {
        try
        {
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.System),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     })
            {
                if (string.IsNullOrEmpty(dir)) continue;

                if (File.Exists(Path.Combine(dir, "UsbDkHelper.dll")))
                    return true;

                var nested = Path.Combine(dir, "UsbDk Runtime Library", "UsbDkHelper.dll");

                if (File.Exists(nested)) return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int libusb_set_option(IntPtr ctx, int option);
}
