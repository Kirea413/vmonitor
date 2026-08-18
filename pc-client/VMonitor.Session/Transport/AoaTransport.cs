using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>
/// AOA (Android Open Accessory) による USB 直結トランスポート。
/// </summary>
/// <remarks>
/// <para>
/// adb も TCP/IP も介さず、端末のバルクエンドポイントへ直接書き込む。
/// 端末側で開発者オプションや USB デバッグを有効にする必要もない。
/// </para>
/// <para>
/// フレーム構造は Wi-Fi 側と共通にしてある。
/// </para>
/// <code>
/// ┌──────────────────────────────────────────────┐
/// │ ChannelId      (1 バイト)                     │
/// │ PayloadLength  (4 バイト, ビッグエンディアン)  │
/// │ Payload        (PayloadLength バイト)         │
/// └──────────────────────────────────────────────┘
/// </code>
/// <para>
/// プロトコルの詳細は <see cref="AoaDevice"/> を参照。
/// </para>
/// </remarks>
public sealed class AoaTransport : ITransport, IAsyncDisposable
{
    private const int FrameHeaderSize = 5;

    /// <summary>受信バッファの大きさ。<see cref="AoaDevice.MaxTransferSize"/> と揃える。</summary>
    private const int ReadBufferSize = AoaDevice.MaxTransferSize;

    private const int WriteTimeoutMs = 5_000;

    /// <summary>
    /// 読み出しのタイムアウト。短めにしておき、
    /// タイムアウトのたびに停止要求を確認できるようにする。
    /// </summary>
    private const int ReadTimeoutMs = 200;

    /// <summary>
    /// 切断時に受信スレッドの終了を待つ上限。
    /// 読み出し 1 回ぶんのタイムアウトより十分長くとる。
    /// </summary>
    private const int ShutdownJoinMs = 3_000;

    /// <summary>
    /// 壊れたフレームを受け取ったときに、でたらめな長さで確保してしまわないための上限。
    /// </summary>
    private const int MaxPayloadSize = 32 * 1024 * 1024;

    /// <summary>アクセサリーモードへ切り替えた端末が戻ってくるのを待つ上限。</summary>
    private const int ReattachTimeoutMs = 10_000;

    /// <summary>USB 2.0 の公称帯域。</summary>
    private const long DefaultBandwidth = 480_000_000L;

    // ── 状態 ─────────────────────────────────────────────────────────────

    private AoaDevice? _device;
    private bool       _disposed;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// PC が受け取るのはタッチと制御だけで量は少ない。
    /// それでも読み手が止まったときに際限なく溜まらないよう上限を設ける。
    /// </summary>
    private readonly Channel<(ChannelId, Memory<byte>)> _receiveChannel =
        Channel.CreateBounded<(ChannelId, Memory<byte>)>(
            new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationTokenSource? _receiveCts;
    private Thread?                  _receiveThread;

    /// <summary>接続時に検出した内容（診断表示用）。</summary>
    public string? ConnectionDetail { get; private set; }

    // ── ITransport ───────────────────────────────────────────────────────

    public TransportType Type => TransportType.USB;

    public long EstimatedBandwidthBps => DefaultBandwidth;

    /// <summary>
    /// 端末を掴む。<paramref name="endpoint"/> は使わない（USB は宛先が一意なため）。
    /// </summary>
    /// <remarks>
    /// 端末がまだ通常モードなら、まずアクセサリーモードへ切り替える。
    /// 切り替えると端末は USB から一度消えて別の VID/PID で戻ってくるので、
    /// 戻ってくるまで待ってから掴み直す。
    /// </remarks>
    public Task ConnectAsync(EndPoint endpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 既に掴んでいるなら何もしない。
        //
        // SessionManager はセッションを確立するときに改めて ConnectAsync を呼ぶ。
        // Wi-Fi では繋ぎ直しても困らないが、USB では自分が握っている
        // エンドポイントを自分で開き直そうとして失敗する。
        if (_device != null)
            return Task.CompletedTask;

        // 既にアクセサリーモードならそのまま掴める
        var device = AoaDevice.OpenAccessory(out string error);

        if (device == null)
        {
            var result = AoaDevice.SwitchToAccessoryMode();

            if (result.Outcome is not (AoaDevice.SwitchOutcome.Switched
                                    or AoaDevice.SwitchOutcome.AlreadyInAccessoryMode))
            {
                throw new InvalidOperationException(
                    "USB 直結できる端末が見つかりませんでした。\n" + result.Detail
                    + DescribeEnvironment());
            }

            device = WaitForAccessory(ct, out error);

            if (device == null)
            {
                throw new InvalidOperationException(
                    "アクセサリーモードへ切り替えましたが、端末を掴めませんでした。\n" + error
                    + DescribeEnvironment());
            }
        }

        _device = device;

        ConnectionDetail =
            $"PID=0x{device.ProductId:X4} IN=0x{device.InEndpoint:X2} OUT=0x{device.OutEndpoint:X2}";

        // libusb の読み出しは同期呼び出しなので、専用スレッドを立てる。
        // スレッドプールを長時間占有させないための措置。
        _receiveCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveThread = new Thread(() => ReceiveLoop(_receiveCts.Token))
        {
            IsBackground = true,
            Name         = "vmonitor-aoa-receive",
        };
        _receiveThread.Start();

        return Task.CompletedTask;
    }

    /// <summary>切り替え後、端末が別の VID/PID で戻ってくるのを待つ。</summary>
    private static AoaDevice? WaitForAccessory(CancellationToken ct, out string error)
    {
        const int PollIntervalMs = 250;

        error = "タイムアウトしました。";

        for (int waited = 0; waited < ReattachTimeoutMs; waited += PollIntervalMs)
        {
            if (ct.IsCancellationRequested) break;

            Thread.Sleep(PollIntervalMs);

            var device = AoaDevice.OpenAccessory(out error);
            if (device != null) return device;
        }

        return null;
    }

    /// <remarks>
    /// 受信スレッドが抜けきってからデバイスを閉じる。順番を守らないと、
    /// カーネルに投げたままの読み出しがある状態でハンドルを閉じることになり、
    /// スレッドが USB の I/O から戻ってこなくなる。
    /// こうなるとプロセスを終了させても後始末が終わらず、
    /// 抜け殻のプロセスが端末を掴んだまま残る（ケーブルを挿し直すまで直らない）。
    /// </remarks>
    public Task DisconnectAsync()
    {
        _receiveCts?.Cancel();

        // 読み出しは最長 ReadTimeoutMs で戻ってくる。
        // 余裕を見て待ち、それでも戻らなければデバイスには触らない。
        bool threadStopped = _receiveThread?.Join(ShutdownJoinMs) ?? true;
        _receiveThread = null;

        _receiveCts?.Dispose();
        _receiveCts = null;

        if (threadStopped)
        {
            _device?.Dispose();
        }
        else
        {
            // まだ読み出しの中にいる。ここで閉じると状況が悪化するので触らない。
            // ハンドルは残るが、プロセスが終われば OS が回収する。
            ShutdownTimedOut = true;
        }

        _device = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// 切断時に受信スレッドが期限内に止まらなかったか（診断用）。
    /// </summary>
    public bool ShutdownTimedOut { get; private set; }

    public async Task SendAsync(ReadOnlyMemory<byte> data, ChannelId channel, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var device = _device
            ?? throw new InvalidOperationException(
                "USB 接続が確立されていません。ConnectAsync を先に呼び出してください。");

        var frame = BuildFrame(data.Span, channel);

        // 1 本のエンドポイントを複数チャンネルで共有するので、
        // フレームが混ざらないよう送信を直列化する。
        await _sendLock.WaitAsync(ct);
        try
        {
            device.Write(frame, WriteTimeoutMs);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _receiveChannel.Reader.ReadAllAsync(ct))
            yield return item;
    }

    // ── 受信 ─────────────────────────────────────────────────────────────

    private void ReceiveLoop(CancellationToken ct)
    {
        var chunk = new byte[ReadBufferSize];

        // 届いたバイト列を溜めて、フレームが揃った分から取り出す。
        // USB のバルク転送はこちらの都合とは無関係な切れ目で届くため、
        // 1 回の読み出しが 1 フレームに対応するとは限らない。
        var pending      = new byte[ReadBufferSize * 4];
        int pendingCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = _device!.Read(chunk, ReadTimeoutMs);

                if (read <= 0) continue;   // タイムアウト。停止要求を見て回り直す

                EnsureCapacity(ref pending, pendingCount + read);
                Array.Copy(chunk, 0, pending, pendingCount, read);
                pendingCount += read;

                pendingCount = DrainFrames(pending, pendingCount, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 停止要求。正常な終わり方
        }
        catch (Exception)
        {
            // 端末が抜かれたなど。受信側にストリームの終わりとして伝える
        }
        finally
        {
            _receiveChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 溜まったバイト列から、揃っているフレームをすべて取り出す。
    /// </summary>
    /// <returns>取り出した後に残ったバイト数。</returns>
    private int DrainFrames(byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;

        while (count - offset >= FrameHeaderSize)
        {
            var channel = (ChannelId)buffer[offset];

            uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(
                buffer.AsSpan(offset + 1, 4));

            if (payloadLength > MaxPayloadSize)
            {
                // 同期がずれている。ここから先は解釈できないので捨てて仕切り直す。
                return 0;
            }

            int total = FrameHeaderSize + (int)payloadLength;

            if (count - offset < total) break;   // まだ届ききっていない

            var payload = buffer.AsSpan(offset + FrameHeaderSize, (int)payloadLength).ToArray();
            offset += total;

            if (ct.IsCancellationRequested) break;

            _receiveChannel.Writer.TryWrite((channel, payload.AsMemory()));
        }

        // 残りを先頭へ寄せる
        int remaining = count - offset;

        if (remaining > 0 && offset > 0)
            Array.Copy(buffer, offset, buffer, 0, remaining);

        return remaining;
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required) return;

        int size = buffer.Length;
        while (size < required) size *= 2;

        Array.Resize(ref buffer, size);
    }

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload, ChannelId channel)
    {
        var frame = new byte[FrameHeaderSize + payload.Length];

        frame[0] = (byte)channel;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(FrameHeaderSize));

        return frame;
    }

    // ── 後始末 ───────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _sendLock.Dispose();
    }

    // ── 静的ユーティリティ ───────────────────────────────────────────────

    /// <summary>
    /// USB 直結が使えそうかを調べる。
    /// アクセサリーモードの端末があるか、切り替えられる端末があれば true。
    /// </summary>
    /// <remarks>
    /// 実際に切り替えは行わない。接続方法を選ぶ画面での判定に使う。
    /// </remarks>
    /// <summary>
    /// 端末が見つからないときに、思い当たる理由を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「端末を待っています」としか出ないと、ケーブルは挿さっているのに
    /// 何も起きない状況で何を直せばよいのか分からない。
    /// </para>
    /// <para>
    /// よくあるのは 2 つ。ドライバが当たっていない場合と、
    /// adb サーバーが USB を掴んでいる場合。どちらも Windows 側から
    /// 見て取れるので、分かる範囲で伝える。
    /// </para>
    /// </remarks>
    /// <returns>理由。思い当たらなければ空文字。</returns>
    public static string UnavailableHint()
    {
        try
        {
            if (IsDeviceAvailable()) return string.Empty;

            return ConnectFailureHint();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 掴めなかった理由として思い当たるものを返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 端末が見えていても掴めないことがある。libusb は
    /// 「Operation not supported or unimplemented on this platform」のような
    /// 原因を指さないエラーを返すため、症状からは辿り着けない。
    /// </para>
    /// <para>
    /// よくあるのは 2 つ。ドライバが当たっていない場合と、
    /// adb サーバーが USB を掴んでいる場合。どちらも Windows 側から
    /// 見て取れるので、分かる範囲で伝える。
    /// </para>
    /// </remarks>
    /// <returns>理由。思い当たらなければ空文字。</returns>
    public static string ConnectFailureHint()
    {
        try
        {
            if (HasUnboundAccessoryInterface())
            {
                return "USB 直結用のドライバが当たっていません。" +
                       "VMonitorSetup.exe /driver-only を管理者で実行してください";
            }

            if (AoaDevice.IsAdbServerRunning())
            {
                return "adb サーバーが USB を掴んでいます。" +
                       "コマンドプロンプトで adb kill-server を実行してください";
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 失敗の知らせに、USB まわりの様子を添える。
    /// </summary>
    /// <remarks>
    /// 「端末によって使えたり使えなかったりする」の切り分けは、
    /// Windows が何のドライバを当てたかを見ないと進まない。手元に
    /// 無い PC で起きている以上、記録に残しておくしかない。
    /// 例外の本文に入れておけば、そのままログへ流れる。
    /// </remarks>
    private static string DescribeEnvironment()
        => Environment.NewLine
         + Environment.NewLine
         + "USB への経路: " + LibUsbBackend.Describe() + Environment.NewLine
         + Environment.NewLine
         + "繋がっている USB デバイス:" + Environment.NewLine
         + AoaDevice.DescribeUsbDevices()
         + Environment.NewLine
         + Environment.NewLine
         + "ドライバの欄が WUDFWpdMtp などになっている端末は、"
         + "MTP に握られていて開けません。"
         + Environment.NewLine
         + "UsbDk を入れると、既存のドライバを外さずに届くようになります。"
         + Environment.NewLine
         + "https://github.com/daynix/UsbDk/releases";


    /// <summary>
    /// 繋がっている Android 端末の USB シリアル番号を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 挿し直しても変わらない値なので、同じ端末だと見分けるのに使う。
    /// これが無いと、繋ぎ直すたびに別の端末として扱われ、
    /// 接続候補の一覧に同じスマホが何台も並ぶ。
    /// </para>
    /// <para>
    /// Windows のデバイス ID の末尾に入っている。
    /// 例: <c>USB\VID_18D1&amp;PID_2D01\2B181JEGR13171</c>
    /// </para>
    /// </remarks>
    /// <returns>シリアル番号。取れなければ null。</returns>
    public static string? GetDeviceSerial()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'USB\\\\VID_18D1&PID_2D0%'");

            foreach (var entity in searcher.Get())
            {
                if (entity["DeviceID"] is not string id) continue;

                // インターフェース側 (&MI_00) ではなく、親のデバイスを見る。
                // シリアルが入っているのは親のほう。
                if (id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;

                var serial = id.Split('\\').LastOrDefault();

                if (string.IsNullOrWhiteSpace(serial)) continue;

                // 端末がシリアルを持たない場合、Windows は "&" 始まりの
                // その場限りの値を入れる。それでは見分けに使えない。
                if (serial.StartsWith('&')) continue;

                return serial;
            }
        }
        catch
        {
            // WMI が使えない環境では諦める
        }

        return null;
    }

    /// <summary>
    /// アクセサリーインターフェースが現れているのに、
    /// ドライバが当たっていない状態かを調べる。
    /// </summary>
    /// <remarks>
    /// この状態では libusb から開けず、
    /// 「Operation not supported or unimplemented on this platform」で失敗する。
    /// 症状だけでは原因に辿り着けないので、Windows 側の見え方から判断する。
    /// </remarks>
    private static bool HasUnboundAccessoryInterface()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ConfigManagerErrorCode FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE 'USB\\\\VID_18D1&PID_2D0%'");

            foreach (var entity in searcher.Get())
            {
                var code = entity["ConfigManagerErrorCode"];
                // 28 = ドライバが入っていない (CM_PROB_FAILED_INSTALL)
                if (code is uint value && value == 28) return true;
            }
        }
        catch
        {
            // WMI が使えない環境では判断しない
        }

        return false;
    }

    public static bool IsDeviceAvailable()
    {
        try
        {
            // 既にアクセサリーモードなら、開かずに分かる。
            //
            // ここで一覧を取ると（製品名を見るために）デバイスを開いてしまい、
            // 閉じた直後に本番の接続が同じデバイスを開こうとして失敗することがある。
            // 定期的に呼ばれる経路なので、開かずに済む判定を先に置く。
            if (AoaDevice.HasAccessoryDevice()) return true;

            return AoaDevice.ListDevices().Any(IsLikelyAndroid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Android 端末らしいかどうか。
    /// 開けること（＝WinUSB 系に束縛されていること）を条件に入れているのは、
    /// 開けないデバイスにはどのみち AOA の問い合わせすらできないため。
    /// </summary>
    private static bool IsLikelyAndroid(AoaDevice.UsbDeviceSummary device)
        => device.Openable && AndroidVendorIds.Contains(device.VendorId);

    /// <summary>Android 端末でよく使われるベンダー ID。</summary>
    private static readonly int[] AndroidVendorIds =
    {
        0x18D1, // Google
        0x04E8, // Samsung
        0x22B8, // Motorola
        0x12D1, // Huawei
        0x2717, // Xiaomi
        0x0BB4, // HTC / Fairphone
        0x0FCE, // Sony
        0x05C6, // Qualcomm
        0x19D2, // ZTE
        0x0489, // Foxconn / Sharp
        0x1004, // LG
        0x2A70, // OnePlus
        0x2D95, // Vivo
        0x22D9, // Oppo
        0x0E8D, // MediaTek
    };
}
