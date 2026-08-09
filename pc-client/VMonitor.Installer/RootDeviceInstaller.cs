using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VMonitor.Installer;

/// <summary>
/// ルート列挙デバイス (Root\...) のデバイスノードを作成・削除する。
/// </summary>
/// <remarks>
/// <para>
/// vmonitor の仮想ディスプレイには対応する物理ハードウェアが無いため、
/// <c>Root\VMonitorVDD</c> というルート列挙デバイスとして OS に登録する。
/// </para>
/// <para>
/// <c>pnputil /add-driver /install</c> は「既に存在するデバイスに合うドライバを当てる」
/// 操作であって、デバイスノード自体は作らない。物理デバイスが無い以上、
/// 誰かが明示的に作らないとドライバは永久に読み込まれず、
/// DriverStore に登録されただけで仮想ディスプレイは現れない。
/// ここで SetupAPI を使ってノードを作る。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RootDeviceInstaller
{
    private const string SetupApi = "setupapi.dll";
    private const string Newdev   = "newdev.dll";

    // SetupDiCreateDeviceInfo のフラグ
    private const uint DICD_GENERATE_ID = 0x00000001;

    // デバイスプロパティ
    private const uint SPDRP_HARDWAREID = 0x00000001;

    // クラスインストーラーへの指示
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint DIF_REMOVE         = 0x00000005;

    // UpdateDriverForPlugAndPlayDevices のフラグ
    private const uint INSTALLFLAG_FORCE = 0x00000001;

    // SetupDiGetClassDevs のフラグ
    private const uint DIGCF_PRESENT = 0x00000002;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint  cbSize;
        public Guid  ClassGuid;
        public uint  DevInst;
        public IntPtr Reserved;
    }

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceName,
        ref Guid classGuid,
        string? deviceDescription,
        IntPtr hwndParent,
        uint creationFlags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        uint propertyBufferSize);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetINFClass(
        string infName,
        out Guid classGuid,
        System.Text.StringBuilder className,
        uint classNameSize,
        out uint requiredSize);

    [DllImport(Newdev, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    /// <summary>
    /// ルート列挙デバイスを作成し、指定した INF のドライバを適用する。
    /// </summary>
    /// <param name="hardwareId">ハードウェア ID（例: <c>Root\VMonitorVDD</c>）。</param>
    /// <param name="infPath">INF ファイルの絶対パス。</param>
    /// <param name="classGuid">デバイスクラスの GUID。</param>
    /// <param name="rebootRequired">再起動が必要な場合 true。</param>
    /// <returns>成功した場合は null、失敗した場合はエラー内容。</returns>
    public static string? CreateDevice(
        string hardwareId, string infPath, Guid classGuid, out bool rebootRequired)
    {
        rebootRequired = false;

        if (!Path.IsPathRooted(infPath))
            return $"INF は絶対パスで指定する必要があります: {infPath}";

        // 既に作成済みなら二重に作らない
        if (DeviceExists(hardwareId, classGuid))
        {
            // ドライバだけ当て直す
            return UpdateDriver(hardwareId, infPath, out rebootRequired);
        }

        // INF からセットアップクラス（GUID と名前）を読み取る。
        //
        // SetupDiCreateDeviceInfo に DICD_GENERATE_ID を渡す場合、DeviceName には
        // デバイスインスタンス ID の「根」となる名前を渡す決まりで、
        // ここにバックスラッシュを含めることはできない（Windows が
        // ROOT\<名前>\<連番> の形に組み立てるため）。
        // ハードウェア ID (Root\VMonitorVDD) をそのまま渡すと
        // ERROR_INVALID_DEVINST_NAME (0xE0000205) で弾かれる。
        // ここで渡すべきなのはクラス名 ("Display")。
        var classNameBuffer = new System.Text.StringBuilder(64);

        if (!SetupDiGetINFClass(infPath, out Guid infClassGuid, classNameBuffer,
                                (uint)classNameBuffer.Capacity, out _))
        {
            return $"INF からクラス情報を読み取れませんでした (エラー 0x{Marshal.GetLastWin32Error():X8})";
        }

        classGuid = infClassGuid;
        string className = classNameBuffer.ToString();

        var deviceInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (deviceInfoSet == INVALID_HANDLE_VALUE)
            return $"SetupDiCreateDeviceInfoList 失敗 (エラー 0x{Marshal.GetLastWin32Error():X8})";

        try
        {
            var devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            // クラス名から新しいデバイス情報要素を作る
            if (!SetupDiCreateDeviceInfo(
                    deviceInfoSet, className, ref classGuid, null,
                    IntPtr.Zero, DICD_GENERATE_ID, ref devInfoData))
            {
                return $"SetupDiCreateDeviceInfo 失敗 " +
                       $"(クラス '{className}', エラー 0x{Marshal.GetLastWin32Error():X8})";
            }

            // ハードウェア ID は REG_MULTI_SZ（末尾に二重の NUL が必要）
            var hardwareIdBuffer = MultiSzBytes(hardwareId);

            if (!SetupDiSetDeviceRegistryProperty(
                    deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID,
                    hardwareIdBuffer, (uint)hardwareIdBuffer.Length))
            {
                return $"ハードウェア ID の設定に失敗 (エラー 0x{Marshal.GetLastWin32Error():X8})";
            }

            // デバイスノードを実際に登録する
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, deviceInfoSet, ref devInfoData))
                return $"デバイスの登録に失敗 (エラー 0x{Marshal.GetLastWin32Error():X8})";
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        // 作ったデバイスにドライバを当てる
        return UpdateDriver(hardwareId, infPath, out rebootRequired);
    }

    /// <summary>作成済みデバイスに INF のドライバを適用する。</summary>
    private static string? UpdateDriver(string hardwareId, string infPath, out bool rebootRequired)
    {
        if (!UpdateDriverForPlugAndPlayDevices(
                IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out rebootRequired))
        {
            int error = Marshal.GetLastWin32Error();

            // ERROR_NO_SUCH_DEVINST (0xE000020B): デバイスがまだ現れていない
            const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);

            return error == ERROR_NO_SUCH_DEVINST
                ? "デバイスノードが見つかりません。デバイスの登録に失敗している可能性があります。"
                : $"ドライバの適用に失敗 (エラー 0x{error:X8})";
        }

        return null;
    }

    /// <summary>指定のハードウェア ID を持つデバイスが既に存在するか調べる。</summary>
    public static bool DeviceExists(string hardwareId, Guid classGuid)
    {
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (deviceInfoSet == INVALID_HANDLE_VALUE) return false;

        try
        {
            var devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint i = 0; SetupDiEnumDeviceInfo(deviceInfoSet, i, ref devInfoData); i++)
            {
                if (!SetupDiGetDeviceRegistryProperty(
                        deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID,
                        out _, null, 0, out uint requiredSize))
                {
                    if (requiredSize == 0) continue;
                }

                var buffer = new byte[requiredSize];

                if (!SetupDiGetDeviceRegistryProperty(
                        deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID,
                        out _, buffer, requiredSize, out _))
                {
                    continue;
                }

                foreach (var id in ParseMultiSz(buffer))
                {
                    if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return false;
    }

    /// <summary>
    /// 指定のハードウェア ID を持つデバイスノードをすべて削除する。
    /// </summary>
    /// <returns>削除した個数。</returns>
    public static int RemoveDevices(string hardwareId, Guid classGuid)
    {
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (deviceInfoSet == INVALID_HANDLE_VALUE) return 0;

        int removed = 0;

        try
        {
            var devInfoData = new SP_DEVINFO_DATA();
            devInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint i = 0; SetupDiEnumDeviceInfo(deviceInfoSet, i, ref devInfoData); i++)
            {
                SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID,
                    out _, null, 0, out uint requiredSize);

                if (requiredSize == 0) continue;

                var buffer = new byte[requiredSize];

                if (!SetupDiGetDeviceRegistryProperty(
                        deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID,
                        out _, buffer, requiredSize, out _))
                {
                    continue;
                }

                bool match = ParseMultiSz(buffer)
                    .Any(id => string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase));

                if (!match) continue;

                if (SetupDiCallClassInstaller(DIF_REMOVE, deviceInfoSet, ref devInfoData))
                    removed++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return removed;
    }

    // ── REG_MULTI_SZ の変換 ──────────────────────────────────────────────

    /// <summary>文字列を REG_MULTI_SZ 形式（末尾が二重 NUL）のバイト列にする。</summary>
    private static byte[] MultiSzBytes(string value)
    {
        // 値 + NUL + 終端の NUL
        var bytes = new byte[(value.Length + 2) * 2];
        System.Text.Encoding.Unicode.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }

    /// <summary>REG_MULTI_SZ のバイト列を文字列の並びに戻す。</summary>
    private static IEnumerable<string> ParseMultiSz(byte[] buffer)
    {
        var text = System.Text.Encoding.Unicode.GetString(buffer);

        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
