using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VMonitor.Driver;

/// <summary>
/// ディスプレイの表示スケール（Windows の「拡大縮小」）を読み書きする。
/// </summary>
/// <remarks>
/// <para>
/// スマホの画面は小さく、PC の画面をそのまま映すと文字が細かすぎて読めない。
/// 解決には 2 通りある。
/// </para>
/// <list type="bullet">
///   <item>解像度を下げる — 大きくなるがぼやける</item>
///   <item>表示スケールを上げる — くっきりしたまま大きくなる</item>
///   <item></item>
/// </list>
/// <para>
/// 後者が Windows で「拡大率」と呼ばれているもので、こちらを使う。
/// </para>
/// <para>
/// 設定するための公開 API は用意されていない。Windows の設定アプリが
/// 使っている <c>DisplayConfigSetDeviceInfo</c> の未公開の型を叩く。
/// 未公開なので将来変わりうる。失敗しても接続そのものは続けられるよう、
/// ここでは例外を投げずに成否を返す。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class DisplayScale
{
    // ── Win32 ────────────────────────────────────────────────────────────

    private const int  ErrorSuccess = 0;
    private const uint QdcOnlyActivePaths = 0x00000002;

    /// <summary>未公開: 拡大率の取得。</summary>
    private const int DeviceInfoGetDpiScale = -3;

    /// <summary>未公開: 拡大率の設定。</summary>
    private const int DeviceInfoSetDpiScale = -4;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount, [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount, [Out] DisplayConfigModeInfo[] modes,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DpiScaleGet request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DpiScaleSet request);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int  Type;
        public int  Size;
        public Luid AdapterId;
        public uint Id;
    }

    // パスとモードは中身を読まないので、大きさだけ合わせておく。
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public Luid SourceAdapterId;  public uint SourceId;  public uint SourceModeIdx;  public uint SourceStatus;
        public Luid TargetAdapterId;  public uint TargetId;  public uint TargetModeIdx;
        public uint OutputTechnology; public uint Rotation;  public uint Scaling;
        public ulong RefreshRate;     public uint ScanLineOrdering;
        public int  TargetAvailable;  public uint TargetStatus;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        // 以降の共用体は読まないので Size で確保だけする
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    /// <summary>
    /// 拡大率の取得。値は「刻みの番号」で返る。
    /// </summary>
    /// <remarks>
    /// Windows は拡大率を百分率ではなく、用意された刻み
    /// （100/125/150/175/200/225/250/300/350/400）の相対位置で扱う。
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleGet
    {
        public DeviceInfoHeader Header;
        public int MinScaleRel;      // 最小までの相対値（0 以下）
        public int CurScaleRel;      // いまの相対値
        public int MaxScaleRel;      // 最大までの相対値（0 以上）
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DpiScaleSet
    {
        public DeviceInfoHeader Header;
        public int ScaleRel;
    }

    /// <summary>Windows が用意している拡大率の刻み。</summary>
    private static readonly int[] ScaleSteps =
        { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400 };

    // ── 公開 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 指定したディスプレイの表示スケールを設定する。
    /// </summary>
    /// <param name="gdiDeviceName">
    /// <c>\\.\DISPLAY7</c> のような GDI 名。取り込み元と同じものを渡す。
    /// </param>
    /// <param name="percent">
    /// 望む拡大率（百分率）。Windows が持つ刻みのうち、
    /// 指定に最も近く、その画面で使えるものに丸める。
    /// </param>
    /// <returns>実際に設定された拡大率。設定できなければ null。</returns>
    public static int? Apply(string gdiDeviceName, int percent)
    {
        try
        {
            if (!TryFindSource(gdiDeviceName, out var adapterId, out uint sourceId))
                return null;

            var query = new DpiScaleGet
            {
                Header = new DeviceInfoHeader
                {
                    Type      = DeviceInfoGetDpiScale,
                    Size      = Marshal.SizeOf<DpiScaleGet>(),
                    AdapterId = adapterId,
                    Id        = sourceId,
                }
            };

            if (DisplayConfigGetDeviceInfo(ref query) != ErrorSuccess) return null;

            // いまの相対値 0 が「推奨」に当たる。そこを基準に刻みの並びへ写す。
            int recommendedIndex = IndexOfRecommended(query);
            if (recommendedIndex < 0) return null;

            int wantedIndex = NearestStepIndex(percent);

            // その画面で使える範囲に収める
            int minIndex = recommendedIndex + query.MinScaleRel;
            int maxIndex = recommendedIndex + query.MaxScaleRel;

            wantedIndex = Math.Clamp(wantedIndex, minIndex, maxIndex);

            var apply = new DpiScaleSet
            {
                Header = new DeviceInfoHeader
                {
                    Type      = DeviceInfoSetDpiScale,
                    Size      = Marshal.SizeOf<DpiScaleSet>(),
                    AdapterId = adapterId,
                    Id        = sourceId,
                },
                ScaleRel = wantedIndex - recommendedIndex,
            };

            if (DisplayConfigSetDeviceInfo(ref apply) != ErrorSuccess) return null;

            return ScaleSteps[wantedIndex];
        }
        catch
        {
            // 未公開の API なので、将来の Windows で形が変わりうる。
            // 効かなくても映すことはできるので、接続は止めない。
            return null;
        }
    }

    // ── 内部 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// いまの拡大率が刻みの何番目かを求める。
    /// </summary>
    /// <remarks>
    /// 取得できるのは「推奨からの相対値」だけで、百分率は返らない。
    /// 最小・最大の相対値と刻みの数から、推奨の位置を逆算する。
    /// </remarks>
    private static int IndexOfRecommended(DpiScaleGet info)
    {
        // 使える刻みの本数
        int span = info.MaxScaleRel - info.MinScaleRel + 1;

        if (span <= 0 || span > ScaleSteps.Length) return -1;

        // 最小側は必ず 100% から始まる
        return -info.MinScaleRel;
    }

    /// <summary>指定した百分率にいちばん近い刻みの番号。</summary>
    private static int NearestStepIndex(int percent)
    {
        int best = 0;
        int bestGap = int.MaxValue;

        for (int i = 0; i < ScaleSteps.Length; i++)
        {
            int gap = Math.Abs(ScaleSteps[i] - percent);
            if (gap >= bestGap) continue;

            bestGap = gap;
            best    = i;
        }

        return best;
    }

    /// <summary>GDI 名から、その画面を指す識別子を探す。</summary>
    private static bool TryFindSource(string gdiDeviceName, out Luid adapterId, out uint sourceId)
    {
        adapterId = default;
        sourceId  = 0;

        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount)
            != ErrorSuccess)
        {
            return false;
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];

        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero)
            != ErrorSuccess)
        {
            return false;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var name = new SourceDeviceName
            {
                Header = new DeviceInfoHeader
                {
                    // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                    Type      = 1,
                    Size      = Marshal.SizeOf<SourceDeviceName>(),
                    AdapterId = paths[i].SourceAdapterId,
                    Id        = paths[i].SourceId,
                }
            };

            if (DisplayConfigGetDeviceInfo(ref name) != ErrorSuccess) continue;

            if (!string.Equals(name.ViewGdiDeviceName, gdiDeviceName,
                               StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            adapterId = paths[i].SourceAdapterId;
            sourceId  = paths[i].SourceId;
            return true;
        }

        return false;
    }
}
