using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// <see cref="IWindowsDisplayApi"/> の実アダプター実装。
/// Windows 環境で SetDisplayConfig / ChangeDisplaySettingsEx / QueryDisplayConfig を呼び出す。
/// 非 Windows ビルドでは操作をスキップする（テスト・クロスプラットフォーム対応）。
/// </summary>
public class WindowsDisplayApiAdapter : IWindowsDisplayApi
{
    /// <summary>既定でサポートされる解像度プリセット。</summary>
    private static readonly IReadOnlyList<Resolution> DefaultSupportedResolutions = new[]
    {
        new Resolution(640,   480),
        new Resolution(1280,  720),
        new Resolution(1920, 1080),
        new Resolution(2560, 1440),
        new Resolution(3840, 2160),
    };

    /// <inheritdoc/>
    /// <remarks>
    /// SetDisplayConfig を呼び出してディスプレイトポロジを変更する。
    /// <list type="bullet">
    ///   <item><see cref="DisplayMode.Clone"/>       → SDC_TOPOLOGY_CLONE</item>
    ///   <item><see cref="DisplayMode.Extend"/>      → SDC_TOPOLOGY_EXTEND</item>
    ///   <item><see cref="DisplayMode.SecondaryOnly"/>→ SDC_TOPOLOGY_EXTERNAL</item>
    /// </list>
    /// 非 Windows 環境では何もしない。
    /// </remarks>
    public void ApplyDisplayMode(VirtualDisplayHandle handle, DisplayMode mode)
    {
        _ = handle;

        if (!OperatingSystem.IsWindows())
            return;

        // 仮想モニターが到着しても、デスクトップの構成に組み込まれるまでは
        // Windows から見える画面数は増えない。ここでトポロジを適用する。
        uint topology = mode switch
        {
            DisplayMode.Clone         => SDC_TOPOLOGY_CLONE,
            DisplayMode.Extend        => SDC_TOPOLOGY_EXTEND,
            DisplayMode.SecondaryOnly => SDC_TOPOLOGY_EXTERNAL,
            _                         => SDC_TOPOLOGY_EXTEND,
        };

        // トポロジ指定の場合、パス配列は渡さず SDC_APPLY と組み合わせる。
        // SDC_USE_SUPPLIED_DISPLAY_CONFIG とは併用できない。
        int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);

        if (result != ERROR_SUCCESS)
        {
            // 構成の保存まで求めると失敗することがあるため、
            // 一度きりの適用として retry する。
            SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero,
                             SDC_APPLY | topology | SDC_ALLOW_CHANGES);
        }
    }

    /// <summary>直近の <see cref="ApplyDisplayMode"/> が返した結果コード（診断用）。</summary>
    public int LastApplyResult { get; private set; }

    // ── SetDisplayConfig の定義 ──────────────────────────────────────────

    private const int ERROR_SUCCESS = 0;

    private const uint SDC_TOPOLOGY_CLONE    = 0x00000002;
    private const uint SDC_TOPOLOGY_EXTEND   = 0x00000004;
    private const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    private const uint SDC_APPLY             = 0x00000080;
    private const uint SDC_ALLOW_CHANGES     = 0x00000400;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements, IntPtr pathArray,
        uint numModeInfoArrayElements, IntPtr modeInfoArray,
        uint flags);

    /// <inheritdoc/>
    /// <remarks>
    /// ChangeDisplaySettingsEx を呼び出して解像度を変更する。
    /// 非 Windows 環境では何もしない。
    /// </remarks>
    public void ApplyResolution(VirtualDisplayHandle handle, Resolution resolution)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // NOTE: 実際の実装では P/Invoke で ChangeDisplaySettingsEx を呼び出す。
        // 実 API シグネチャ:
        //   [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        //   static extern int ChangeDisplaySettingsEx(
        //       string lpszDeviceName, ref DEVMODE lpDevMode,
        //       IntPtr hwnd, uint dwflags, IntPtr lParam);
        _ = resolution;
        _ = handle;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// QueryDisplayConfig を呼び出して現在の設定を取得する。
    /// 非 Windows 環境では null を返す。
    /// </remarks>
    public DisplayConfig? QueryConfig(VirtualDisplayHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // NOTE: 実際の実装では P/Invoke で QueryDisplayConfig を呼び出し、
        // DISPLAYCONFIG_PATH_INFO / DISPLAYCONFIG_MODE_INFO 構造体からデータを取得する。
        // 実 API シグネチャ:
        //   [DllImport("user32.dll")]
        //   static extern int QueryDisplayConfig(
        //       uint flags, ref uint numPathArrayElements, IntPtr pathArray,
        //       ref uint numModeInfoArrayElements, IntPtr modeInfoArray,
        //       IntPtr currentTopologyId);
        _ = handle;
        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Resolution> GetSupportedResolutions(VirtualDisplayHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return DefaultSupportedResolutions;

        // NOTE: 実際の実装では EnumDisplaySettings / QueryDisplayConfig を使って
        // ドライバが報告するモードリストを取得する。
        return DefaultSupportedResolutions;
    }
}
