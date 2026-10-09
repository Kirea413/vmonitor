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
    private static string? _fallbackReason;
    // 古いlibusbは新規文脈の既定オプションをWindowsバックエンド初期化より先に
    // 適用するため、UsbDkの利用可能フラグが未設定だとNOT_FOUNDになる。
    // 列挙・ハンドルを持たない初期化用文脈をプロセスの寿命まで維持する。
    private static IntPtr _bootstrapContext;

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
    internal static Func<string?> UsbDkProbe { get; set; } = ProbeUsbDk;
    internal static Func<int> UsbDkOptionSetter { get; set; } =
        () => libusb_set_option(IntPtr.Zero, OptionUseUsbDk);

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

                // DLLがある・オプションを設定できるだけでは、実際に使えるとは限らない。
                // 既定のオプションを変更する前に、独立した文脈で初期化と列挙を検証する。
                // 失敗した経路を既定にすると、以降の全UsbContextが作れなくなる。
                _fallbackReason = UsbDkProbe();
                if (_fallbackReason != null) return;

                // 文脈を指定しない呼び出しは、以降に作る文脈すべてに効く。
                int rc = UsbDkOptionSetter();
                _usbDkEnabled = rc == 0;
                if (rc != 0)
                {
                    _fallbackReason = $"UsbDkオプション設定エラー {rc}";
                    ReleaseBootstrap();
                }
            }
            catch (Exception ex)
            {
                // 古い libusb には無い。調べる側が壊れることもある。
                // どちらも既定の経路で続けられる。
                _usbDkEnabled = false;
                _fallbackReason = ex.Message;
                ReleaseBootstrap();
            }
        }
    }

    /// <summary>いまの経路を人が読める形で返す（記録用）。</summary>
    public static string Describe()
        => IsUsbDkEnabled
            ? "UsbDk 経由"
            : _fallbackReason == null
                ? "WinUSB 経由（UsbDk は使っていません）"
                : $"WinUSB 経由（UsbDkが使えないため切替: {_fallbackReason}）";

    [StructLayout(LayoutKind.Sequential)]
    private struct InitOption
    {
        public int Option;
        public IntPtr Value;
    }

    private static string? ProbeUsbDk()
    {
        // libusb 1.0.27以降の文脈ごとの初期化。古いDLLの場合もWinUSBを維持する。
        IntPtr context = IntPtr.Zero;
        IntPtr devices = IntPtr.Zero;
        try
        {
            var option = new InitOption { Option = OptionUseUsbDk };
            int rc;
            try
            {
                rc = libusb_init_context(out context, ref option, 1);
            }
            catch (EntryPointNotFoundException)
            {
                // 同梱の古いlibusbでは、まだ列挙していない独立文脈に設定する。
                // プロセス全体の既定は変更しない。
                rc = libusb_init(out context);
                if (rc != 0)
                {
                    context = IntPtr.Zero;
                    return $"USB初期化エラー {rc}";
                }
                rc = libusb_set_option(context, OptionUseUsbDk);
                if (rc != 0) return $"UsbDk利用確認エラー {rc}";
            }
            if (rc != 0)
            {
                context = IntPtr.Zero; // 失敗時の出力ポインタは使用しない。
                return $"UsbDk初期化エラー {rc}";
            }
            long count = libusb_get_device_list(context, out devices).ToInt64();
            if (count < 0) return $"UsbDk列挙エラー {count}";
            _bootstrapContext = context;
            context = IntPtr.Zero; // プロセス寿命まで保持。デバイス一覧はfinallyで解放。
            return null;
        }
        finally
        {
            if (devices != IntPtr.Zero) libusb_free_device_list(devices, 1);
            if (context != IntPtr.Zero) libusb_exit(context);
        }
    }

    private static void ReleaseBootstrap()
    {
        if (_bootstrapContext == IntPtr.Zero) return;
        libusb_exit(_bootstrapContext);
        _bootstrapContext = IntPtr.Zero;
    }

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

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int libusb_init_context(out IntPtr ctx, ref InitOption option, int numOptions);

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern int libusb_init(out IntPtr ctx);

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libusb_get_device_list(IntPtr ctx, out IntPtr devices);

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libusb_free_device_list(IntPtr devices, int unrefDevices);

    [DllImport("libusb-1.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libusb_exit(IntPtr ctx);
}
