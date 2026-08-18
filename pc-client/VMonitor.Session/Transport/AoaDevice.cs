using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace VMonitor.Session.Transport;

/// <summary>
/// AOA (Android Open Accessory) プロトコルの下回り。
/// </summary>
/// <remarks>
/// <para>
/// AOA は Android 端末を「USB デバイス側」、PC を「USB ホスト側」に置く仕組み。
/// adb も TCP/IP も介さず、バルクエンドポイント上で自由なバイト列をやり取りできる。
/// </para>
/// <para>
/// 手順は 3 段階になっている。
/// </para>
/// <list type="number">
///   <item>
///     通常モードの端末にベンダーリクエスト 51 を投げ、AOA 対応バージョンを聞く。
///     返答が 1 以上ならアクセサリーモードに入れる。
///   </item>
///   <item>
///     リクエスト 52 で 6 本の識別文字列を送る。ここで送るメーカー名・モデル名が
///     Android 側アプリの accessory_filter.xml と一致していないと、端末は
///     「対応アプリがありません」と判断してしまう。
///   </item>
///   <item>
///     リクエスト 53 で切り替えを指示する。端末は USB を一度切断し、
///     VID 0x18D1 / PID 0x2D0x として再列挙してくる。以降はこの新しい
///     デバイスをバルク転送で掴む。
///   </item>
/// </list>
/// <para>
/// 仕様: https://source.android.com/docs/core/interaction/accessories/aoa
/// </para>
/// </remarks>
public sealed class AoaDevice : IDisposable
{
    // ── AOA 識別子 ───────────────────────────────────────────────────────

    /// <summary>アクセサリーモードの端末が名乗るベンダー ID（Google）。</summary>
    public const int GoogleVendorId = 0x18D1;

    /// <summary>
    /// アクセサリーモードのプロダクト ID。
    /// 0x2D00 が素のアクセサリー、以降は ADB / オーディオの有無で変わる。
    /// どれで来ても掴めるよう全部見る。
    /// </summary>
    public static readonly int[] AccessoryProductIds =
        { 0x2D00, 0x2D01, 0x2D02, 0x2D03, 0x2D04, 0x2D05 };

    // ── 名乗る内容（Android 側の accessory_filter.xml と一致させること）──

    public const string AccessoryManufacturer = "vmonitor";
    public const string AccessoryModel        = "vmonitor Virtual Display";
    public const string AccessoryDescription  = "PC の画面をこの端末に表示します";
    public const string AccessoryVersion      = "1.0";
    public const string AccessoryUri          = "https://github.com/vmonitor/vmonitor";
    public const string AccessorySerial       = "VMON0001";

    // ── 制御リクエスト ───────────────────────────────────────────────────

    private const byte ReqGetProtocol = 51;
    private const byte ReqSendString  = 52;
    private const byte ReqStart       = 53;

    /// <summary>ベンダー要求 / 宛先はデバイス / 方向は IN。</summary>
    private const byte VendorIn = 0xC0;

    /// <summary>ベンダー要求 / 宛先はデバイス / 方向は OUT。</summary>
    private const byte VendorOut = 0x40;

    // 文字列インデックス（順番は仕様で決まっている）
    private const int StrManufacturer = 0;
    private const int StrModel        = 1;
    private const int StrDescription  = 2;
    private const int StrVersion      = 3;
    private const int StrUri          = 4;
    private const int StrSerial       = 5;

    // ── 転送のパラメータ ─────────────────────────────────────────────────

    /// <summary>
    /// 1 回のバルク転送で送る上限。
    /// Android 側の <c>InputStream.read</c> は受け側バッファより大きな塊が
    /// 届くと溢れた分を捨てる実装があるため、両端で同じ値に揃えておく。
    /// </summary>
    public const int MaxTransferSize = 16 * 1024;

    // ── 状態 ─────────────────────────────────────────────────────────────

    private readonly IUsbDevice          _device;
    private readonly UsbEndpointReader   _reader;
    private readonly UsbEndpointWriter   _writer;
    private UsbDeviceCollection?         _ownedDevices;
    private UsbContext?                  _ownedContext;
    private bool                         _disposed;

    /// <summary>掴んだアクセサリーデバイスのプロダクト ID。</summary>
    public int ProductId { get; }

    /// <summary>読み出しに使っているエンドポイントアドレス。</summary>
    public byte InEndpoint { get; }

    /// <summary>書き込みに使っているエンドポイントアドレス。</summary>
    public byte OutEndpoint { get; }

    private AoaDevice(
        UsbContext          context,
        UsbDeviceCollection devices,
        IUsbDevice          device,
        UsbEndpointReader   reader,
        UsbEndpointWriter   writer,
        byte                inEndpoint,
        byte                outEndpoint)
    {
        _ownedContext = context;
        _ownedDevices = devices;
        _device       = device;
        _reader       = reader;
        _writer       = writer;
        ProductId     = device.ProductId;
        InEndpoint    = inEndpoint;
        OutEndpoint   = outEndpoint;
    }

    // ── 列挙 ─────────────────────────────────────────────────────────────

    /// <summary>USB バス上で見えている端末の要約。</summary>
    /// <param name="VendorId">ベンダー ID。</param>
    /// <param name="ProductId">プロダクト ID。</param>
    /// <param name="Product">製品名（取得できないときは空）。</param>
    /// <param name="Manufacturer">メーカー名（取得できないときは空）。</param>
    /// <param name="InAccessoryMode">既にアクセサリーモードで動いているか。</param>
    /// <param name="Openable">libusb から開けたか（Windows では WinUSB 系に束縛されている必要がある）。</param>
    /// <param name="OpenError">開けなかった理由。</param>
    public readonly record struct UsbDeviceSummary(
        int    VendorId,
        int    ProductId,
        string Product,
        string Manufacturer,
        bool   InAccessoryMode,
        bool   Openable,
        string OpenError);

    /// <summary>
    /// 接続されている USB デバイスを一覧する（診断用）。
    /// </summary>
    public static IReadOnlyList<UsbDeviceSummary> ListDevices()
    {
        var results = new List<UsbDeviceSummary>();

        using var context = new UsbContext();
        context.SetDebugLevel(LogLevel.None);

        // 一覧は context より先に捨てる（Dispose のコメント参照）
        using var devices = context.List();

        foreach (var device in devices)
        {
            bool   openable  = false;
            string openError = string.Empty;
            string product   = string.Empty;
            string maker     = string.Empty;

            // Dispose した後のデバイスからは何も読めない。先に控えておく。
            int vendorId  = device.VendorId;
            int productId = device.ProductId;

            try
            {
                openable = device.TryOpen();

                if (openable)
                {
                    // 文字列ディスクリプタは端末によっては読めない。取れなくても続ける。
                    try { product = device.Info?.Product      ?? string.Empty; } catch { }
                    try { maker   = device.Info?.Manufacturer ?? string.Empty; } catch { }
                }
                else
                {
                    openError = "他のドライバに束縛されています";
                }
            }
            catch (Exception ex)
            {
                openError = ex.Message;
            }
            finally
            {
                // Dispose は一覧側がまとめて行う。ここでは閉じるだけ。
                try { device.Close(); } catch { }
            }

            results.Add(new UsbDeviceSummary(
                vendorId,
                productId,
                product,
                maker,
                IsAccessoryPid(vendorId, productId),
                openable,
                openError));
        }

        return results;
    }

    /// <summary>アクセサリーモードの VID/PID かどうか。</summary>
    public static bool IsAccessoryPid(int vendorId, int productId)
        => vendorId == GoogleVendorId && AccessoryProductIds.Contains(productId);

    /// <summary>
    /// アクセサリーモードの端末が繋がっているかだけを、開かずに確かめる。
    /// </summary>
    /// <remarks>
    /// <see cref="ListDevices"/> は製品名や開けるかどうかを見るために
    /// 実際にデバイスを開く。存在確認のたびに開いて閉じていると、
    /// その直後に本番の接続が同じデバイスを開こうとして失敗することがある
    /// （閉じた直後はまだ解放されきっていない）。
    /// 定期的に呼ぶ用途にはこちらを使う。
    /// </remarks>
    public static bool HasAccessoryDevice()
    {
        try
        {
            using var context = new UsbContext();
            context.SetDebugLevel(LogLevel.None);

            using var devices = context.List();

            foreach (var device in devices)
            {
                if (IsAccessoryPid(device.VendorId, device.ProductId))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // ── アクセサリーモードへの切り替え ───────────────────────────────────

    /// <summary>切り替えを試みた結果。</summary>
    /// <param name="Outcome">どうなったか。</param>
    /// <param name="ProtocolVersion">端末が答えた AOA バージョン（0 なら未対応）。</param>
    /// <param name="Detail">利用者に見せる説明。</param>
    public readonly record struct SwitchResult(
        SwitchOutcome Outcome,
        int           ProtocolVersion,
        string        Detail);

    public enum SwitchOutcome
    {
        /// <summary>切り替えを指示した。端末が再列挙してくるのを待つ。</summary>
        Switched,

        /// <summary>既にアクセサリーモードだった。そのまま接続できる。</summary>
        AlreadyInAccessoryMode,

        /// <summary>AOA に対応した端末が見つからなかった。</summary>
        NoDeviceFound,

        /// <summary>端末は見つかったが開けなかった（ドライバ束縛や adb の占有）。</summary>
        DeviceNotAccessible,

        /// <summary>端末は開けたが AOA に対応していなかった。</summary>
        NotSupported,
    }

    /// <summary>
    /// 繋がっている Android 端末をアクセサリーモードへ切り替える。
    /// </summary>
    /// <remarks>
    /// 成功すると端末はいったん USB から消え、1〜3 秒後に別の VID/PID で
    /// 戻ってくる。呼び出し側はその間待ってから <see cref="OpenAccessory"/> を呼ぶ。
    /// </remarks>
    public static SwitchResult SwitchToAccessoryMode()
    {
        using var context = new UsbContext();
        context.SetDebugLevel(LogLevel.None);

        // 一覧は context より先に捨てる（Dispose のコメント参照）
        using var devices = context.List();

        var  failures     = new List<string>();
        bool sawCandidate = false;
        int  bestProtocol = 0;

        foreach (var device in devices)
        {
            // Dispose した後のデバイスからは何も読めない。先に控えておく。
            int    vendorId  = device.VendorId;
            int    productId = device.ProductId;
            string label     = $"VID=0x{vendorId:X4} PID=0x{productId:X4}";

            // 既に切り替わっているならそこで終わり
            if (IsAccessoryPid(vendorId, productId))
            {
                return new SwitchResult(
                    SwitchOutcome.AlreadyInAccessoryMode, 0,
                    "端末は既にアクセサリーモードです。");
            }

            try
            {
                if (!device.TryOpen())
                {
                    // 開けないデバイスは Android ではないか、別ドライバの持ち物。
                    // ただし Google の VID なら Android なので、理由を伝える。
                    if (vendorId == GoogleVendorId)
                        failures.Add($"{label}: {OpenFailureHint()}");

                    continue;
                }

                // Windows では、インターフェースを取ってからでないと
                // 制御転送が LIBUSB_ERROR_NOT_FOUND になる。
                //
                // 番号は 0 とは限らない。通常モードの端末では、WinUSB が
                // 当たっているのが ADB のインターフェースだけ、という
                // ことがある。その番号は機種や USB の設定で変わる。
                // 0 だけを見ていると、掴めるのに掴めないことになる。
                int claimedInterface = TryClaimAnyInterface(device);
                bool claimed = claimedInterface >= 0;

                int protocol = QueryProtocol(device);

                if (protocol <= 0)
                {
                    if (claimed) TryReleaseInterface(device, claimedInterface);
                    continue;   // AOA を知らないデバイス。webcam などが該当する
                }

                sawCandidate = true;
                bestProtocol = Math.Max(bestProtocol, protocol);

                if (!claimed)
                {
                    failures.Add($"{label}: AOA v{protocol} に対応していますが、" +
                                 "インターフェースを確保できませんでした" +
                                 "（adb サーバーが掴んでいる可能性があります）");
                    continue;
                }

                if (!SendIdentity(device, out string sendError))
                {
                    failures.Add($"{label}: 識別文字列の送信に失敗しました ({sendError})");
                    TryReleaseInterface(device, claimedInterface);
                    continue;
                }

                // 切り替え指示。この時点で端末は USB を切るので、
                // 戻り値やこの後のハンドル操作は当てにしない。
                device.ControlTransfer(
                    new UsbSetupPacket(VendorOut, ReqStart, 0, 0, 0),
                    Array.Empty<byte>(), 0, 0);

                return new SwitchResult(
                    SwitchOutcome.Switched, protocol,
                    $"{label} をアクセサリーモードへ切り替えました (AOA v{protocol})。");
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
            finally
            {
                // Dispose は一覧側がまとめて行う。ここでは閉じるだけ。
                try { device.Close(); } catch { }
            }
        }

        if (sawCandidate)
            return new SwitchResult(
                SwitchOutcome.DeviceNotAccessible, bestProtocol,
                string.Join("\n", failures));

        return new SwitchResult(
            failures.Count > 0 ? SwitchOutcome.DeviceNotAccessible : SwitchOutcome.NoDeviceFound,
            0,
            failures.Count > 0
                ? string.Join("\n", failures)
                : "AOA に対応した端末が見つかりませんでした。");
    }

    /// <summary>
    /// 端末に AOA の対応バージョンを聞く。対応していなければ 0。
    /// </summary>
    /// <summary>
    /// 掴めるインターフェースを順に試し、最初に取れた番号を返す。取れなければ -1。
    /// </summary>
    /// <remarks>
    /// 通常モードの端末では、WinUSB が当たっているのが ADB の
    /// インターフェースだけ、ということがある。その番号は機種や
    /// USB の設定で変わるため、決め打ちにできない。
    /// </remarks>
    private static int TryClaimAnyInterface(IUsbDevice device)
    {
        int count = 1;

        try
        {
            var config = device.Configs.FirstOrDefault();
            if (config is not null) count = Math.Max(1, config.Interfaces.Count);
        }
        catch
        {
            // 構成を読めない端末もある。0 番だけ試す。
        }

        for (int i = 0; i < count; i++)
        {
            try
            {
                if (device.ClaimInterface(i)) return i;
            }
            catch
            {
                // 別のドライバや adb が持っている。次を試す。
            }
        }

        return -1;
    }

    private static int QueryProtocol(IUsbDevice device)
    {
        var buffer = new byte[2];

        try
        {
            int transferred = device.ControlTransfer(
                new UsbSetupPacket(VendorIn, ReqGetProtocol, 0, 0, buffer.Length),
                buffer, 0, buffer.Length);

            if (transferred != 2)
                return 0;

            // リトルエンディアンの 16bit
            return buffer[0] | (buffer[1] << 8);
        }
        catch
        {
            // AOA を知らないデバイスは STALL を返す。異常ではない。
            return 0;
        }
    }

    /// <summary>6 本の識別文字列を順に送る。</summary>
    private static bool SendIdentity(IUsbDevice device, out string error)
    {
        (int Index, string Value)[] strings =
        {
            (StrManufacturer, AccessoryManufacturer),
            (StrModel,        AccessoryModel),
            (StrDescription,  AccessoryDescription),
            (StrVersion,      AccessoryVersion),
            (StrUri,          AccessoryUri),
            (StrSerial,       AccessorySerial),
        };

        foreach (var (index, value) in strings)
        {
            // 仕様上、終端の NUL を含めて送る
            var utf8    = System.Text.Encoding.UTF8.GetBytes(value);
            var payload = new byte[utf8.Length + 1];
            utf8.CopyTo(payload, 0);

            int transferred;
            try
            {
                transferred = device.ControlTransfer(
                    new UsbSetupPacket(VendorOut, ReqSendString, 0, index, payload.Length),
                    payload, 0, payload.Length);
            }
            catch (Exception ex)
            {
                error = $"index {index}: {ex.Message}";
                return false;
            }

            if (transferred != payload.Length)
            {
                error = $"index {index}: {transferred}/{payload.Length} バイトしか送れませんでした";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>掴んだインターフェースを手放す。番号は掴んだときのものを渡す。</summary>
    private static void TryReleaseInterface(IUsbDevice device, int interfaceNumber = 0)
    {
        try { device.ReleaseInterface(interfaceNumber); } catch { }
    }

    /// <summary>
    /// 繋がっている USB デバイスと、Windows が割り当てたドライバを並べる。
    /// </summary>
    /// <remarks>
    /// 同じアプリなのに端末によって使えたり使えなかったりする。その差は
    /// たいてい、Windows が何のドライバを当てたかで決まる。ドライバの
    /// 当たっていないデバイスは、こちらから開けない。
    ///
    /// 手元に無い PC で起きている以上、こちらから調べに行けない。
    /// 失敗したときに自分で書き残しておけば、記録を送ってもらうだけで
    /// 切り分けが済む。
    /// </remarks>
    public static string DescribeUsbDevices()
    {
        if (!OperatingSystem.IsWindows())
            return "（Windows ではないため調べられません）";

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Name, Service, Status FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'USB\\VID_%'");

            var lines = new List<string>();

            foreach (var entity in searcher.Get())
            {
                if (entity["DeviceID"] is not string id) continue;

                // 同じ端末がインターフェースごとに何度も出てくる。
                // 親だけに絞ると、肝心の割り当てが見えなくなるので残す。
                string service = entity["Service"] as string ?? "(割り当て無し)";
                string status  = entity["Status"]  as string ?? "?";
                string name    = entity["Name"]    as string ?? "?";

                // VID/PID だけ取り出す。シリアル番号は個人が特定できる
                // 場合があるので載せない。
                string vid = Extract(id, "VID_");
                string pid = Extract(id, "PID_");

                lines.Add($"  VID={vid} PID={pid} 状態={status} " +
                          $"ドライバ={service} 名前={name}");
            }

            if (lines.Count == 0)
                return "（USB デバイスが 1 つも見つかりませんでした）";

            lines.Sort(StringComparer.Ordinal);

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"（一覧を取れませんでした: {ex.Message}）";
        }

        static string Extract(string id, string key)
        {
            int at = id.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "????";

            int start = at + key.Length;
            if (start + 4 > id.Length) return "????";

            return id.Substring(start, 4).ToUpperInvariant();
        }
    }

    /// <summary>
    /// デバイスを開けなかったときに、よくある原因を案内する。
    /// </summary>
    /// <remarks>
    /// 端末の USB デバッグが有効だと、アクセサリーモードでも ADB の
    /// インターフェースが同居した複合デバイス (PID 0x2D01) になる。
    /// この状態で adb サーバーが動いていると、こちらからは
    /// アクセサリー側のインターフェースも開けなくなる。
    /// 実測でこれが最も多い失敗要因だったので、名指しで伝える。
    /// </remarks>
    public static string OpenFailureHint()
    {
        if (IsAdbServerRunning())
        {
            return "adb サーバーが USB を掴んでいます。" +
                   "コマンドプロンプトで `adb kill-server` を実行してから、もう一度お試しください。";
        }

        return "WinUSB ドライバが割り当てられているか確認してください " +
               "（デバイスマネージャーに不明なデバイスがある場合は VMonitorAOA ドライバが未導入です）。";
    }

    /// <summary>adb サーバーが動いているか。</summary>
    public static bool IsAdbServerRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("adb").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ── アクセサリーデバイスを開く ───────────────────────────────────────

    /// <summary>
    /// アクセサリーモードの端末を開き、バルクエンドポイントを掴む。
    /// 見つからなければ null を返し、<paramref name="error"/> に理由を入れる。
    /// </summary>
    public static AoaDevice? OpenAccessory(out string error)
    {
        error = string.Empty;

        // 掴んだデバイスは返した後も生かし続ける必要があるので、
        // context と一覧の寿命を AoaDevice に預ける。
        // 失敗した経路では必ず自分で片付ける。
        var                  context = new UsbContext();
        UsbDeviceCollection? devices = null;

        try
        {
            context.SetDebugLevel(LogLevel.None);
            devices = context.List();

            foreach (var device in devices)
            {
                if (!IsAccessoryPid(device.VendorId, device.ProductId))
                    continue;

                int productId = device.ProductId;

                if (!device.TryOpen())
                {
                    error = $"アクセサリーデバイス (PID=0x{productId:X4}) を開けませんでした。{OpenFailureHint()}";
                    break;
                }

                if (!device.ClaimInterface(0))
                {
                    error = $"アクセサリーデバイス (PID=0x{productId:X4}) の" +
                            "インターフェースを確保できませんでした。";
                    try { device.Close(); } catch { }
                    break;
                }

                if (!TryFindBulkEndpoints(device, out byte inEp, out byte outEp))
                {
                    error = "アクセサリーデバイスにバルクエンドポイントが見つかりませんでした。";
                    TryReleaseInterface(device);
                    try { device.Close(); } catch { }
                    break;
                }

                var reader = device.OpenEndpointReader(
                    (ReadEndpointID)inEp, MaxTransferSize, EndpointType.Bulk);
                var writer = device.OpenEndpointWriter(
                    (WriteEndpointID)outEp, EndpointType.Bulk);

                error = string.Empty;
                return new AoaDevice(context, devices, device, reader, writer, inEp, outEp);
            }

            if (string.IsNullOrEmpty(error))
                error = "アクセサリーモードの端末が見つかりませんでした。";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        try { devices?.Dispose(); } catch { }
        try { context.Dispose();  } catch { }
        return null;
    }

    /// <summary>
    /// インターフェース 0 のバルクエンドポイントを探す。
    /// アクセサリーは IN / OUT を 1 本ずつ持つ。
    /// </summary>
    private static bool TryFindBulkEndpoints(IUsbDevice device, out byte inEndpoint, out byte outEndpoint)
    {
        inEndpoint  = 0;
        outEndpoint = 0;

        var config = device.Configs.FirstOrDefault();
        if (config == null) return false;

        var iface = config.Interfaces.FirstOrDefault();
        if (iface == null) return false;

        foreach (var endpoint in iface.Endpoints)
        {
            // bmAttributes の下位 2bit が転送タイプ。2 がバルク。
            if ((endpoint.Attributes & 0x03) != 0x02) continue;

            // アドレスの最上位ビットが立っていれば IN
            if ((endpoint.EndpointAddress & 0x80) != 0)
            {
                if (inEndpoint == 0) inEndpoint = endpoint.EndpointAddress;
            }
            else
            {
                if (outEndpoint == 0) outEndpoint = endpoint.EndpointAddress;
            }
        }

        return inEndpoint != 0 && outEndpoint != 0;
    }

    // ── 転送 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// バルク OUT へ書き込む。<see cref="MaxTransferSize"/> ごとに分割して送る。
    /// </summary>
    /// <exception cref="IOException">転送に失敗したとき。</exception>
    public void Write(ReadOnlySpan<byte> data, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int offset = 0;

        while (offset < data.Length)
        {
            int chunk  = Math.Min(MaxTransferSize, data.Length - offset);
            var buffer = data.Slice(offset, chunk).ToArray();

            var result = _writer.Write(buffer, timeoutMs, out int transferred);

            if (result != Error.Success)
                throw new IOException($"USB 書き込みに失敗しました: {result}");

            if (transferred <= 0)
                throw new IOException("USB 書き込みが 0 バイトで返りました。");

            offset += transferred;
        }
    }

    /// <summary>
    /// バルク IN から読み出す。タイムアウトしたときは 0 を返す（異常ではない）。
    /// </summary>
    /// <exception cref="IOException">端末が外れたなど、継続できないとき。</exception>
    public int Read(byte[] buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = _reader.Read(buffer, timeoutMs, out int transferred);

        if (result == Error.Timeout)
            return 0;

        if (result != Error.Success)
            throw new IOException($"USB 読み出しに失敗しました: {result}");

        return transferred;
    }

    // ── 後始末 ───────────────────────────────────────────────────────────

    /// <remarks>
    /// 解放の順番が重要。libusb のデバイス参照は context に紐づいているため、
    /// context を先に捨てると、後から GC がデバイスのハンドルを解放しようとして
    /// 解放済みメモリを触り、プロセスごと落ちる
    /// （AccessViolationException at NativeMethods.UnrefDevice）。
    /// デバイス → 一覧 → context の順で片付ける。
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TryReleaseInterface(_device);

        try { _device.Close(); } catch { }

        // デバイスは一覧が持っているので、個別 Dispose ではなく一覧ごと捨てる
        try { _ownedDevices?.Dispose(); } catch { }
        _ownedDevices = null;

        try { _ownedContext?.Dispose(); } catch { }
        _ownedContext = null;
    }
}
