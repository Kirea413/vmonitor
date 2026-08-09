using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Driver;
using VMonitor.Session;
using VMonitor.Session.Input;
using VMonitor.Session.Transport;
using VMonitor.UI.ViewModels;

namespace VMonitor.UI;

/// <summary>
/// スマホアプリからの TCP 接続を待ち受けるサーバー。
/// 接続ごとにセッションを確立し、画面ミラーの配信とタッチ入力の注入を行う。
/// </summary>
public sealed class ConnectionServer
{
    /// <summary>PC クライアントが待ち受けるポート番号（固定）。</summary>
    public const int ListenPort = 7979;

    private readonly ConnectionViewModel _vm;
    private readonly VirtualDisplayDriver _vdd;
    private readonly AuthManager _authManager;
    private readonly VMonitorLogger _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// ディスプレイまわりの設定。接続のたびに読み直す必要はないが、
    /// 設定画面で変えたら次の接続から効くようにしたいので
    /// <see cref="UpdateDisplaySettings"/> で差し替える。
    /// </summary>
    private DisplaySettings _displaySettings = DisplaySettings.Default;

    /// <summary>ディスプレイ設定を反映する。次に確立するセッションから効く。</summary>
    public void UpdateDisplaySettings(DisplaySettings settings)
        => _displaySettings = settings ?? DisplaySettings.Default;

    public ConnectionServer(
        ConnectionViewModel vm,
        VirtualDisplayDriver vdd,
        AuthManager authManager,
        VMonitorLogger logger)
    {
        _vm = vm;
        _vdd = vdd;
        _authManager = authManager;
        _logger = logger;
    }

    /// <summary>
    /// 重い処理を UI スレッドで走らせていないか確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ここは USB の列挙、仮想ディスプレイの接続、DXGI の初期化といった
    /// 数十ミリ秒から十数秒かかる同期処理を通る。UI スレッドで回ると
    /// そのあいだ画面が固まる。
    /// </para>
    /// <para>
    /// WPF の同期コンテキストは await のたびに UI スレッドへ戻すため、
    /// 呼び出し元が UI スレッドだと以降すべてが UI スレッドに乗る。
    /// 実際にそうなっていて、接続を試すたびに画面が止まっていた。
    /// 見た目には分からず、測って初めて分かる類なので番人を置いておく。
    /// </para>
    /// </remarks>
    private bool _uiThreadWarned;

    private void WarnIfOnUiThread(string what)
    {
        if (_uiThreadWarned) return;
        if (Application.Current?.Dispatcher.CheckAccess() != true) return;

        _uiThreadWarned = true;

        _logger.Warn("ConnectionServer",
            $"{what} が UI スレッドで動いています。画面が固まります。" +
            "呼び出し元を Task.Run で包んでください。");
    }

    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 自己署名証明書を生成（開発用）
        var cert = GenerateSelfSignedCertificate();

        // mDNS でサービスをアドバタイズする（Flutter アプリが探索できるようにする）
        var mdns = new MdnsService();
        await mdns.RegisterServiceAsync(ListenPort);
        _logger.Info("ConnectionServer", $"mDNS advertised: _vmonitor._tcp port={ListenPort}");

        // USB 直結は PC 側から掴みにいく。Wi-Fi の待ち受けとは独立に回す。
        //
        // Task.Run で切り離すのが要。ここを素の `_ = StartUsbWatcherAsync(ct)` に
        // すると、呼び出し元（UI スレッド）の同期コンテキストを引き継いでしまい、
        // 2 秒ごとの USB 列挙も接続処理もすべて UI スレッドで動く。
        // 実測で、列挙だけで毎回 46〜608 ms、接続を試すと 13 秒、画面が固まっていた。
        _ = Task.Run(() => StartUsbWatcherAsync(ct), ct);

        try
        {
            _listener = new TcpListener(IPAddress.Any, ListenPort);
            _listener.Start();
            _logger.Info("ConnectionServer", $"Listening on port {ListenPort}");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(tcpClient, cert, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Error("ConnectionServer", $"Accept error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("ConnectionServer", $"Listener failed: {ex.Message}");
        }
        finally
        {
            _listener?.Stop();
            await mdns.UnregisterServiceAsync();
            mdns.Dispose();
        }
    }

    /// <summary>USB セッションが後始末を終えたことを知らせる。</summary>
    private readonly ManualResetEventSlim _usbWatcherStopped = new(initialState: true);

    // ── PC 側からの接続操作 ──────────────────────────────────────────────

    /// <summary>
    /// 待ち時間を飛ばして、すぐ次の接続を試させるための合図。
    /// </summary>
    private readonly ManualResetEventSlim _usbRetryNow = new(initialState: false);

    /// <summary>いま動いている USB セッションを止めるための札。</summary>
    private CancellationTokenSource? _usbSessionCts;

    /// <summary>
    /// 端末が繋がったら自動で接続するか。
    /// 切断ボタンで止めたあと、すぐ繋ぎ直してしまわないように使う。
    /// </summary>
    public bool AutoConnectUsb { get; set; } = true;

    /// <summary>USB の状態（画面に出す文言）。</summary>
    public string UsbStatus { get; private set; } = "端末を待っています";

    /// <summary>
    /// 映像が流れるセッションが確立しているか。
    /// </summary>
    /// <remarks>
    /// ケーブルが繋がっていることとは別。<see cref="IsUsbLinkUp"/> を参照。
    /// </remarks>
    public bool IsUsbConnected { get; private set; }

    /// <summary>
    /// ケーブルで繋がり、やり取りできる状態か。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「繋がっている」には 2 つの意味があり、混ぜると話が通じなくなる。
    /// </para>
    /// <list type="bullet">
    ///   <item>ケーブルが挿さっていて端末が見えている（ここ）</item>
    ///   <item>承認が済んで映像が流れている（<see cref="IsUsbConnected"/>）</item>
    /// </list>
    /// <para>
    /// 前者だけの状態は普通にある。ケーブルは挿したが、まだどちらも
    /// 「接続」を押していない、という場面。
    /// </para>
    /// </remarks>
    public bool IsUsbLinkUp { get; private set; }

    private void SetUsbLink(bool up)
    {
        if (IsUsbLinkUp == up) return;

        IsUsbLinkUp = up;
        UsbStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><see cref="UsbStatus"/> か <see cref="IsUsbConnected"/> が変わったとき。</summary>
    public event EventHandler? UsbStateChanged;

    private void SetUsbState(string status, bool connected)
    {
        if (UsbStatus == status && IsUsbConnected == connected) return;

        UsbStatus      = status;
        IsUsbConnected = connected;

        UsbStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// この PC 側から接続を申し込んだかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 押した側の反対で承認を取る。PC で「接続」を押したなら、
    /// スマホを持っている人に尋ねる。逆にスマホで押されたなら、
    /// PC を触っている人に尋ねる。
    /// </para>
    /// <para>
    /// 断りなく相手の画面を使い始めないための決まりごと。
    /// </para>
    /// </remarks>
    private volatile bool _pcInitiated;

    /// <summary>
    /// 接続待ちで見張っている合図。押されたらここを完了させる。
    /// </summary>
    private TaskCompletionSource<bool>? _pcConnectSignal;

    /// <summary>
    /// 待ち始める前に押された場合の取りこぼし防止。
    /// </summary>
    /// <remarks>
    /// ボタンは待ち受けが始まる前にも押せる。合図だけを見ていると、
    /// そのひと押しが無かったことになる。1 回ぶん覚えておく。
    /// </remarks>
    private int _pcConnectRequested;

    /// <summary>
    /// この PC から接続を申し込む。
    /// </summary>
    /// <remarks>
    /// 接続待ちの最中なら、その場で先へ進める。まだ待ち受けに入っていない
    /// 場合は覚えておき、待ち受けに入った時点で消化する。
    /// 既にセッションが動いている場合は、畳んでから繋ぎ直す。
    /// </remarks>
    public void ConnectUsbNow()
    {
        AutoConnectUsb = true;
        _pcInitiated   = true;

        // 押されたことを必ず 1 回ぶん残す
        Interlocked.Exchange(ref _pcConnectRequested, 1);

        // 接続待ちで見張っていれば、その場で起こす
        var signal = Volatile.Read(ref _pcConnectSignal);

        if (signal is not null && signal.TrySetResult(true))
        {
            Interlocked.Exchange(ref _pcConnectRequested, 0);
            SetUsbState("スマホの承認を待っています…", connected: false);
            return;
        }

        // 動いているセッションがあるなら、いったん終わらせて繋ぎ直す
        if (IsUsbConnected)
        {
            SetUsbState("繋ぎ直しています…", connected: false);
            try { _usbSessionCts?.Cancel(); } catch { }
        }

        _usbRetryNow.Set();
    }

    /// <summary>
    /// 動いている USB セッションを止める。
    /// </summary>
    /// <remarks>
    /// 止めたあとに自動で繋ぎ直すと、切ったつもりが切れないので、
    /// 自動接続も併せて止める。<see cref="ConnectUsbNow"/> で再開する。
    /// </remarks>
    public void DisconnectUsb()
    {
        AutoConnectUsb = false;

        try { _usbSessionCts?.Cancel(); } catch { }

        SetUsbState("切断しました（「接続」で繋ぎ直せます）", connected: false);
    }

    /// <summary>
    /// 待ち受けを止め、USB セッションの後始末が終わるまで待つ。
    /// </summary>
    /// <remarks>
    /// USB は掴んだまま終わらせてはいけない。読み出しを投げたままプロセスが
    /// 落ちると、カーネル側の I/O が終わらず抜け殻のプロセスが残り、
    /// 端末を握ったままになる。ケーブルを挿し直すまで次の接続ができなくなるため、
    /// 終了時はここで畳み終わるのを待つ。
    /// </remarks>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();

        // 後始末は受信のタイムアウト待ちを含むので、少し余裕をみる
        _usbWatcherStopped.Wait(TimeSpan.FromSeconds(6));
    }

    // ── PC から端末へ Wi-Fi で繋ぐ ───────────────────────────────────────

    /// <summary>端末が待ち受けている既定のポート。</summary>
    public const int DevicePort = 7980;

    /// <summary>いま動いている「PC 発信」セッションを止めるための札。</summary>
    private CancellationTokenSource? _outboundCts;

    /// <summary>PC 発信の状態（画面に出す文言）。</summary>
    public string OutboundStatus { get; private set; } = "未接続";

    /// <summary>PC 発信のセッションが繋がっているか。</summary>
    public bool IsOutboundConnected { get; private set; }

    /// <summary>接続を試している最中か。</summary>
    public bool IsOutboundBusy { get; private set; }

    /// <summary>PC 発信の状態が変わったとき。</summary>
    public event EventHandler? OutboundStateChanged;

    private void SetOutboundState(string status, bool connected, bool busy)
    {
        if (OutboundStatus == status &&
            IsOutboundConnected == connected &&
            IsOutboundBusy == busy) return;

        OutboundStatus      = status;
        IsOutboundConnected = connected;
        IsOutboundBusy      = busy;

        OutboundStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 端末が待ち受けているところへ、こちらから繋ぎにいく。
    /// </summary>
    /// <remarks>
    /// 端末側のホーム画面に出ているアドレスとポートを入れてもらう。
    /// 繋がったあとの流れは、端末から繋いできた場合とまったく同じ。
    /// </remarks>
    /// <param name="host">端末の IP アドレス。</param>
    /// <param name="port">端末が待ち受けているポート。</param>
    public async Task ConnectToDeviceAsync(string host, int port)
    {
        if (IsOutboundBusy || IsOutboundConnected)
        {
            SetOutboundState("すでに接続処理が動いています", IsOutboundConnected, IsOutboundBusy);
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            SetOutboundState("スマホのアドレスを入力してください", connected: false, busy: false);
            return;
        }

        // USB と同時には繋がない。
        //
        // セッションごとに仮想ディスプレイを 1 枚繋ぐので、2 本走ると
        // 画面が 2 枚増えたうえ、どちらがどちらに映っているのか分からなくなる。
        if (IsUsbConnected)
        {
            SetOutboundState("USB で接続中です。先に USB を切断してください",
                             connected: false, busy: false);
            return;
        }

        if (!IPAddress.TryParse(host.Trim(), out var address))
        {
            // 名前で入れられることもある。解決を試みる。
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host.Trim());
                address = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? resolved.FirstOrDefault();
            }
            catch
            {
                address = null;
            }

            if (address is null)
            {
                SetOutboundState($"アドレスを解決できません: {host}", connected: false, busy: false);
                return;
            }
        }

        // PC から繋ぎにいくので、承認はスマホ側で取る
        _pcInitiated = true;

        SetOutboundState($"{address}:{port} へ接続しています…", connected: false, busy: true);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts?.Token ?? CancellationToken.None);

        _outboundCts = sessionCts;

        WifiTransport? transport = null;

        try
        {
            transport = new WifiTransport();

            // 相手が待っていない場合に長く固まらせない
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));

            await transport.ConnectPlainAsync(new IPEndPoint(address, port), connectCts.Token);

            _logger.Info("ConnectionServer", $"Connected to device: {address}:{port}");

            // 同じ相手に繋ぎ直したら同じ行に戻るよう、宛先から識別子を決める
            var device = new DeviceInfo(
                Id: DeviceIdentifier.FromKey($"vmonitor:wifi:{address}"),
                Name: _lastDeviceName ?? $"Android ({address})",
                Platform: DevicePlatform.Android,
                PhysicalResolution: new Resolution(1080, 1920),
                PixelDensity: 420f);

            SetOutboundState($"{address}:{port} に接続中", connected: true, busy: false);

            await RunSessionAsync(transport, device, $"{address}:{port}",
                                  VMonitor.Core.Models.TransportType.WiFi,
                                  sessionCts.Token);

            SetOutboundState("切断しました", connected: false, busy: false);
        }
        catch (OperationCanceledException)
        {
            // 10 秒の打ち切りと、切断ボタンの区別を付ける。
            // 「タイムアウト」と出しておいて実は自分で切った、では分からない。
            SetOutboundState(
                sessionCts.IsCancellationRequested ? "切断しました" : "接続できませんでした（応答なし）",
                connected: false, busy: false);
        }
        catch (Exception ex)
        {
            _logger.Warn("ConnectionServer", $"Outbound connect failed: {ex.Message}");
            SetOutboundState($"接続できませんでした: {ex.Message}", connected: false, busy: false);
        }
        finally
        {
            _outboundCts = null;

            if (transport is not null)
                await transport.DisposeAsync();
        }
    }

    /// <summary>PC 発信のセッションを切る。</summary>
    public void DisconnectFromDevice()
    {
        try { _outboundCts?.Cancel(); } catch { }
    }

    // ── USB 直結 (AOA) ───────────────────────────────────────────────────

    /// <summary>
    /// USB で繋がった端末を待ち受ける。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wi-Fi と違い、USB では PC 側から相手を掴みにいく。端末が現れるまで
    /// 定期的に見に行き、掴めたらセッションを回し、外れたらまた待ちに戻る。
    /// </para>
    /// <para>
    /// アクセサリーモードへの切り替えは端末側に確認ダイアログを出すことがあるため、
    /// 繋がっているのに掴めない状態が続いても、警告は一度しか出さない。
    /// </para>
    /// </remarks>
    public async Task StartUsbWatcherAsync(CancellationToken ct)
    {
        // 終了時に「まだ畳み終わっていない」と分かるようにする
        _usbWatcherStopped.Reset();

        try
        {
            await RunUsbWatcherLoopAsync(ct);
        }
        finally
        {
            _usbWatcherStopped.Set();
        }
    }

    // ── 一覧に出す端末 ───────────────────────────────────────────────────

    /// <summary>いま一覧に出している USB 端末。</summary>
    private DeviceInfo? _usbCandidate;

    /// <summary>
    /// 端末から名乗ってきた呼び名。次に一覧へ出すときに使う。
    /// </summary>
    /// <remarks>
    /// 呼び名は繋がってからでないと分からないが、一覧にはそれより早く出したい。
    /// 一度聞けたら覚えておいて、次からは最初からその名前で出す。
    /// </remarks>
    private string? _lastDeviceName;

    /// <summary>USB で繋がっている端末を接続候補の一覧に出す。</summary>
    private void RememberUsbCandidate()
    {
        // セッションが始まると一覧はいったん作り直される。
        // 早期 return にすると、そのあと端末が一覧から消えたままになるので、
        // 毎回追加を頼む（同じ Id なら AddCandidate 側で弾かれる）。
        var device = _usbCandidate ?? new DeviceInfo(
            Id: UsbDeviceIdentity(),
            Name: _lastDeviceName ?? "Android 端末（USB）",
            Platform: DevicePlatform.Android,
            PhysicalResolution: new Resolution(1080, 1920),
            PixelDensity: 420f);

        _usbCandidate = device;

        Application.Current?.Dispatcher.Invoke(() =>
            _vm.AddCandidate(device, VMonitor.Core.Models.TransportType.USB));
    }

    /// <summary>
    /// USB で繋がっている端末の識別子。挿し直しても変わらない。
    /// </summary>
    /// <remarks>
    /// 毎回ランダムな識別子を振っていたため、ケーブルを挿し直すたびに
    /// 別の端末として一覧に追加され、同じスマホが何台も並んでいた。
    /// USB のシリアル番号から決めることで、同じ端末は同じ行に戻る。
    /// </remarks>
    private static DeviceIdentifier UsbDeviceIdentity()
    {
        var serial = AoaTransport.GetDeviceSerial();

        // シリアルが読めない端末もある。その場合でも、USB に繋がるのは
        // 一度に 1 台なので、固定の合言葉で同じ行に戻るようにする。
        return DeviceIdentifier.FromKey(
            string.IsNullOrWhiteSpace(serial) ? "vmonitor:usb" : $"vmonitor:usb:{serial}");
    }

    /// <summary>ケーブルが抜かれたら一覧から下げる。</summary>
    private void ForgetUsbCandidate()
    {
        var device = _usbCandidate;
        if (device is null) return;

        _usbCandidate = null;

        Application.Current?.Dispatcher.Invoke(() => _vm.RemoveCandidate(device.Id));
    }

    /// <summary>
    /// 端末が見つからないときの文言。
    /// </summary>
    /// <remarks>
    /// 「端末を待っています」だけだと、ケーブルが挿さっているのに
    /// 出続ける場合に何を直せばよいのか分からない。
    /// 見えているのに掴めないのなら、その理由を出す。
    /// </remarks>
    private static string DescribeMissingDevice()
    {
        var hint = AoaTransport.UnavailableHint();

        return string.IsNullOrEmpty(hint) ? "端末を待っています" : hint;
    }

    /// <summary>
    /// 掴めなかったときに、何をすればよいかまで含めて伝える。
    /// </summary>
    /// <remarks>
    /// libusb のエラーは「Operation not supported or unimplemented on this platform」
    /// のように、原因を指していない。実際にはドライバが当たっていないだけ、
    /// ということが多いので、Windows 側の見え方から補って伝える。
    /// </remarks>
    private static string DescribeConnectFailure(Exception ex)
    {
        var hint = AoaTransport.ConnectFailureHint();

        return string.IsNullOrEmpty(hint)
            ? $"接続できませんでした: {FirstLine(ex.Message)}"
            : hint;
    }

    /// <summary>複数行のメッセージを 1 行に詰める（状態表示は 1 行のため）。</summary>
    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();

        return line.Length <= 90 ? line : line[..90] + "…";
    }

    private async Task RunUsbWatcherLoopAsync(CancellationToken ct)
    {
        const int PollIntervalMs = 2000;

        string? lastFailure = null;

        // ウィンドウが出来上がるまで待つ。
        // 接続の確認ダイアログをアプリ本体より先に出さないため。
        try { await Task.Delay(1500, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            AoaTransport? transport = null;

            try
            {
                WarnIfOnUiThread("USB 監視ループ");

                bool devicePresent = AoaTransport.IsDeviceAvailable();

                if (!devicePresent)
                {
                    lastFailure = null;
                    SetUsbLink(false);
                    SetUsbState(DescribeMissingDevice(), connected: false);
                    ForgetUsbCandidate();
                    await Task.Delay(PollIntervalMs, ct);
                    continue;
                }

                // 掴めるかどうかに関わらず、繋がっていることは見せる。
                // 一覧に出ていないと、そもそも認識されているのか分からない。
                SetUsbLink(true);
                RememberUsbCandidate();

                // 利用者が「切断」したあとは、こちらから繋ぎ直さない。
                // 「接続」を押されるまで待つ。
                if (!AutoConnectUsb)
                {
                    await Task.Delay(PollIntervalMs, ct);
                    continue;
                }

                // Wi-Fi で繋がっている最中に割り込まない。
                // 仮想ディスプレイが 2 枚になり、どちらが映っているのか分からなくなる。
                if (IsOutboundConnected || IsOutboundBusy)
                {
                    SetUsbState("Wi-Fi で接続中です", connected: false);
                    await Task.Delay(PollIntervalMs, ct);
                    continue;
                }

                SetUsbState("接続しています…", connected: false);

                transport = new AoaTransport();
                await transport.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 0), ct);

                _logger.Info("ConnectionServer", $"USB accessory connected: {transport.ConnectionDetail}");
                lastFailure = null;

                // 一覧に出しているものと同じ識別子を使う。
                // ここで別の識別子を振ると、セッション中の行と待機中の行が
                // 別物になり、切断しても一覧から消えなくなる。
                var device = new DeviceInfo(
                    Id: UsbDeviceIdentity(),
                    Name: _lastDeviceName ?? "Android 端末（USB）",
                    Platform: DevicePlatform.Android,
                    PhysicalResolution: new Resolution(1080, 1920),
                    PixelDensity: 420f);

                // このセッションだけを止められるようにしておく（切断ボタン用）
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _usbSessionCts = sessionCts;

                SetUsbState("接続中", connected: true);

                var outcome = await RunSessionAsync(transport, device, "USB",
                                                    VMonitor.Core.Models.TransportType.USB,
                                                    sessionCts.Token);

                _usbSessionCts = null;

                // 終わり方によって見せるものを変える。
                //
                // 端末が消えているなら、電源が落ちたかケーブルが抜かれた。
                // 「端末を待っています」に戻すだけだと、切れたことに
                // 気づけないまま、繋がっているつもりで操作してしまう。
                bool deviceStillThere = AoaTransport.IsDeviceAvailable();

                if (!deviceStillThere)
                {
                    ForgetUsbCandidate();

                    if (AutoConnectUsb)
                        SetUsbState("接続が切断されました（端末が見えなくなりました）", connected: false);
                }
                else if (AutoConnectUsb)
                {
                    SetUsbState("接続が切断されました。もう一度「接続」を押してください",
                                connected: false);
                }

                if (outcome == SessionOutcome.Denied)
                {
                    // 拒否されたのにすぐ繋ぎ直すと、確認ダイアログが出続ける。
                    // ケーブルが抜かれるまで手を出さない。
                    _logger.Info("ConnectionServer",
                                 "USB connection denied; waiting for the cable to be unplugged");

                    await WaitForAccessoryGoneAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (lastFailure != ex.Message)
                {
                    _logger.Info("ConnectionServer", $"USB not ready: {ex.Message}");
                    lastFailure = ex.Message;
                }

                // 掴めなかった理由を画面にも出す。
                //
                // ここをログだけにしていたため、ケーブルが挿さっていて
                // 端末も見えているのに繋がらないとき、画面には何の変化も
                // 出なかった。利用者からは「押しても何も起きない」としか見えない。
                SetUsbState(DescribeConnectFailure(ex), connected: false);
            }
            finally
            {
                if (transport is not null)
                    await transport.DisposeAsync();
            }

            // 次に試すまで待つ。
            // ただし「接続」を押されたら待たずに進む。
            try
            {
                await Task.Run(() => _usbRetryNow.Wait(PollIntervalMs, ct), ct);
                _usbRetryNow.Reset();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ── 遅延の実測 ───────────────────────────────────────────────────────

    /// <summary>
    /// 端末との往復時間を定期的に測る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 制御メッセージは映像と同じ経路（同じバルクエンドポイント）を通るので、
    /// 映像が詰まっていれば、その後ろに並んだ制御メッセージも同じだけ遅れる。
    /// つまりこの往復時間は「送ったものが端末に届くまでの遅れ」の目安になる。
    /// </para>
    /// <para>
    /// 端末から PC への向きはほとんど流れていないので、
    /// 往復時間はおおむね PC → 端末の待ち時間とみてよい。
    /// </para>
    /// </remarks>
    /// <summary>往復時間を測るための時計。セッションごとに作り直す。</summary>
    private readonly System.Diagnostics.Stopwatch _probeClock = System.Diagnostics.Stopwatch.StartNew();

    private async Task SendLatencyProbesAsync(ITransport transport, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                var payload = System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"ping\",\"t\":{_probeClock.ElapsedMilliseconds}}}");

                await transport.SendAsync(payload, ChannelId.Control, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 計測が止まってもセッションは続ける */ }
    }

    /// <summary>端末から返ってきた応答から往復時間を求めて記録する。</summary>
    private void HandleLatencyPong(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload.ToArray());

            if (!document.RootElement.TryGetProperty("type", out var type)) return;
            if (type.GetString() != "pong") return;

            if (!document.RootElement.TryGetProperty("t", out var t)) return;

            long roundTripMs = _probeClock.ElapsedMilliseconds - t.GetInt64();

            _logger.Info("ConnectionServer", $"Round trip to device: {roundTripMs} ms");
        }
        catch
        {
            // 応答以外の制御メッセージ
        }
    }

    // ── 端末の名乗り（画面サイズの受け取り） ─────────────────────────────

    /// <summary>
    /// 接続してきた端末が自分の画面の大きさを知らせてくるのを待つ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 仮想ディスプレイは端末の画面に合わせて作りたい。合っていないと、
    /// スマホ側で帯が出たり引き伸ばされたりする。PC 側では端末の大きさを
    /// 知りようがないので、繋がった直後に端末から教えてもらう。
    /// </para>
    /// <para>
    /// 受信の列挙子はセッション中ずっと同じものを使い回す。Wi-Fi の受信は
    /// ソケットから直接読んでいるため、途中で打ち切って別の列挙子を作ると
    /// フレームの途中から読み直すことになり、以降の解釈がすべてずれる。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 端末が知らせてきた解像度（分からなければ null）と、
    /// 待ちの途中で使いかけた読み出し。
    /// </returns>
    /// <remarks>
    /// タイムアウトで打ち切るとき、投げた <c>MoveNextAsync</c> はまだ動いている。
    /// これを放置したまま本ループが次の <c>MoveNextAsync</c> を呼ぶと、
    /// 同じ列挙子への同時呼び出しになり NotSupportedException で
    /// セッションごと落ちる。使いかけの読み出しは呼び出し元へ返し、
    /// 続きから消化してもらう。
    /// </remarks>
    private async Task<(Resolution? Resolution, Task<bool>? Pending)> WaitForDeviceHelloAsync(
        ITransport transport,
        IAsyncEnumerator<(ChannelId Channel, Memory<byte> Data)> receiver,
        Task<bool>? carriedRead,
        CancellationToken ct)
    {
        // こちらから聞きにいく。
        //
        // 端末は繋がった直後に自分から名乗るが、PC がまだ受信を始めていないと
        // その 1 通は届かない。USB では端末が先に動くことが普通にあるため、
        // 待ち始めるこの時点で改めて要求する。
        try
        {
            var request = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"hello_request\"}");
            await transport.SendAsync(request, ChannelId.Control, ct);
        }
        catch (Exception ex)
        {
            _logger.Info("ConnectionServer", $"hello request failed: {ex.Message}");
        }

        var timeout = Task.Delay(HelloTimeout, ct);

        while (!ct.IsCancellationRequested)
        {
            // 前段から引き継いだ読み出しがあれば、それを先に消化する
            var next = carriedRead ?? receiver.MoveNextAsync().AsTask();
            carriedRead = null;

            if (await Task.WhenAny(next, timeout) == timeout)
            {
                _logger.Info("ConnectionServer", "Device did not send hello; using default resolution");
                return (null, next);
            }

            if (!await next) return (null, null);   // 相手が切れた

            var (channel, data) = receiver.Current;

            if (channel != ChannelId.Control) continue;

            // 呼び名も一緒に名乗ってくる。一覧に出すために覚えておく。
            var announcedName = TryParseHelloName(data.Span);
            if (announcedName is not null) _lastDeviceName = announcedName;

            var reported = TryParseHelloResolution(data.Span);
            if (reported is not null)
            {
                _logger.Info("ConnectionServer",
                    $"Device reported screen size: {reported.Width}x{reported.Height}" +
                    (announcedName is null ? "" : $" name={announcedName}"));
                return (reported, null);
            }
        }

        return (null, null);
    }

    /// <summary>端末の名乗りから呼び名を取り出す。無ければ null。</summary>
    private static string? TryParseHelloName(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type)) return null;
            if (type.GetString() != "hello") return null;

            if (!root.TryGetProperty("name", out var name)) return null;

            var value = name.GetString();

            // 空文字で「Android 端末（USB）」を上書きしない
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>一覧に出している端末の呼び名を差し替える。</summary>
    private void RenameUsbCandidate(
        string name, VMonitor.Core.Models.TransportType transportType)
    {
        var existing = _usbCandidate;
        if (existing is null) return;
        if (existing.Name == name) return;

        var renamed = existing with { Name = name };
        _usbCandidate = renamed;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _vm.RemoveCandidate(existing.Id);
            _vm.AddCandidate(renamed, transportType);
        });
    }

    /// <summary>端末の名乗りを待つ上限。</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    // ── 接続の申し込みと承認 ─────────────────────────────────────────────

    /// <summary>相手の承認を待つ上限。</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 接続してよいかを、押した側の反対で確かめる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// PC で「接続」を押したなら、スマホを持っている人に尋ねる。
    /// スマホで押されたなら、PC を触っている人に尋ねる。
    /// ケーブルが挿さっていることと画面を映してよいことは別なので、
    /// 挿しただけでは始めない。
    /// </para>
    /// <para>
    /// 打ち切るときに投げたままの読み出しは呼び出し元へ返す。
    /// 同じ列挙子への同時呼び出しは NotSupportedException になる。
    /// </para>
    /// </remarks>
    private async Task<(bool Approved, Task<bool>? Pending)> NegotiateConnectAsync(
        ITransport                            transport,
        IAsyncEnumerator<(ChannelId Channel, Memory<byte> Data)> receiver,
        DeviceInfo                            device,
        VMonitor.Core.Models.TransportType    transportType,
        CancellationToken                     ct)
    {
        // まず「誰が繋ぎたいのか」を待つ。
        //
        // 以前はここが無く、端末を見つけた時点でいきなり PC 側の確認ダイアログを
        // 出していた。そのため、
        //   ・スマホで「接続」を押しても、PC は既に自分のダイアログを出していて
        //     要求を読んでおらず、何も起きない
        //   ・PC で「接続」を押しても、その前に始まっていたセッションに
        //     追い越されて、スマホに要求が届かない
        // という取りこぼしが起きていた。
        var (trigger, pending) = await WaitForConnectTriggerAsync(
            transport, receiver, transportType, ct);

        if (trigger == ConnectTrigger.Aborted) return (false, pending);

        // 押した側の反対に承認を出す
        if (trigger == ConnectTrigger.Pc)
        {
            // 待ち段階で投げたままの読み出しを必ず引き継ぐ。
            // 渡さずに AskDeviceAsync が新しく MoveNextAsync を呼ぶと、
            // 同じ列挙子への同時呼び出しになり NotSupportedException で
            // セッションごと落ちる。実際にそれが起きていた。
            return await AskDeviceAsync(transport, receiver, pending, transportType, ct);
        }

        SetTransportState(transportType, "この PC で承認を待っています…", connected: false);

        var authResult = await _authManager.RequestAuthorizationAsync(device);
        bool approved  = authResult != AuthResult.Denied;

        // 返事を相手にも伝える。スマホは「PC の承認を待っています」で
        // 止まっているので、伝えないとそのまま待ち続ける。
        await SendConnectResponseAsync(transport, approved, ct);

        return (approved, pending);
    }

    /// <summary>誰が接続を言い出したか。</summary>
    private enum ConnectTrigger
    {
        /// <summary>この PC の「接続」が押された。</summary>
        Pc,

        /// <summary>スマホから接続要求が届いた。</summary>
        Device,

        /// <summary>待っている途中で相手が消えた、または中止された。</summary>
        Aborted,
    }

    /// <summary>
    /// どちらかが「接続」を押すまで待つ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ケーブルが挿さっただけでは何も始めない。この段階では制御チャンネルの
    /// やり取りしかしないので、仮想ディスプレイも画面の取り込みも動かない。
    /// </para>
    /// <para>
    /// 待つのは 2 つ。スマホから届く接続要求と、この PC のボタン。
    /// どちらが先に来るか分からないので、同時に待つ。
    /// </para>
    /// </remarks>
    private async Task<(ConnectTrigger Trigger, Task<bool>? Pending)> WaitForConnectTriggerAsync(
        ITransport                            transport,
        IAsyncEnumerator<(ChannelId Channel, Memory<byte> Data)> receiver,
        VMonitor.Core.Models.TransportType    transportType,
        CancellationToken                     ct)
    {
        // ボタンが既に押されていたなら、待たずに進む
        if (Interlocked.Exchange(ref _pcConnectRequested, 0) == 1)
            return (ConnectTrigger.Pc, null);

        var pressed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Volatile.Write(ref _pcConnectSignal, pressed);

        SetTransportState(transportType,
            "接続待ち — この PC かスマホで「接続」を押してください", connected: false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var next = receiver.MoveNextAsync().AsTask();

                var finished = await Task.WhenAny(next, pressed.Task);

                if (finished == pressed.Task)
                {
                    // ボタンが押された。投げたままの読み出しは呼び出し元へ渡す。
                    return (ConnectTrigger.Pc, next);
                }

                if (!await next) return (ConnectTrigger.Aborted, null);   // 相手が消えた

                var (channel, data) = receiver.Current;
                if (channel != ChannelId.Control) continue;

                // 呼び名を名乗ってくることがある。拾えるうちに拾っておく。
                var announced = TryParseHelloName(data.Span);
                if (announced is not null) _lastDeviceName = announced;

                if (IsConnectRequest(data.Span))
                {
                    _logger.Info("ConnectionServer", "スマホから接続要求が届きました");
                    return (ConnectTrigger.Device, null);
                }
            }

            return (ConnectTrigger.Aborted, null);
        }
        finally
        {
            Volatile.Write(ref _pcConnectSignal, null);
        }
    }

    /// <summary>制御メッセージが接続要求かどうか。</summary>
    private static bool IsConnectRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload.ToArray());

            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.GetString() == "connect_request";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// スマホ側に承認を求め、返事を待つ。
    /// </summary>
    private async Task<(bool Approved, Task<bool>? Pending)> AskDeviceAsync(
        ITransport                            transport,
        IAsyncEnumerator<(ChannelId Channel, Memory<byte> Data)> receiver,
        Task<bool>?                           carriedRead,
        VMonitor.Core.Models.TransportType    transportType,
        CancellationToken                     ct)
    {
        try
        {
            var request = System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"connect_request\",\"initiator\":\"pc\"}");

            await transport.SendAsync(request, ChannelId.Control, ct);
        }
        catch (Exception ex)
        {
            _logger.Warn("ConnectionServer", $"接続の申し込みを送れませんでした: {ex.Message}");
            SetTransportState(transportType, "スマホに要求を送れませんでした", connected: false);
            return (false, null);
        }

        SetTransportState(transportType, "スマホの承認を待っています…", connected: false);
        _logger.Info("ConnectionServer", "スマホの承認を待っています");

        var timeout = Task.Delay(ApprovalTimeout, ct);

        while (!ct.IsCancellationRequested)
        {
            // 前段から引き継いだ読み出しがあれば、それを先に消化する。
            // 同じ列挙子に MoveNextAsync を重ねて呼ぶことはできない。
            var next = carriedRead ?? receiver.MoveNextAsync().AsTask();
            carriedRead = null;

            if (await Task.WhenAny(next, timeout) == timeout)
            {
                _logger.Info("ConnectionServer", "スマホからの返事がありませんでした");
                SetTransportState(transportType, "スマホから返事がありませんでした", connected: false);
                return (false, next);
            }

            if (!await next) return (false, null);   // 相手が切れた

            var (channel, data) = receiver.Current;
            if (channel != ChannelId.Control) continue;

            var accepted = TryParseConnectResponse(data.Span);
            if (accepted is null) continue;

            if (accepted == false)
            {
                _logger.Info("ConnectionServer", "スマホ側で拒否されました");
                SetTransportState(transportType, "スマホ側で拒否されました", connected: false);
            }

            return (accepted.Value, null);
        }

        return (false, null);
    }

    /// <summary>承認・拒否を相手に伝える。</summary>
    private async Task SendConnectResponseAsync(
        ITransport transport, bool accepted, CancellationToken ct)
    {
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"type\":\"connect_response\",\"accepted\":{(accepted ? "true" : "false")}}}");

            await transport.SendAsync(payload, ChannelId.Control, ct);
        }
        catch (Exception ex)
        {
            // 伝えられなくても、こちらの判断は変わらない
            _logger.Info("ConnectionServer", $"承認結果を伝えられませんでした: {ex.Message}");
        }
    }

    /// <summary>制御メッセージが承認の返事なら、その可否を返す。</summary>
    private static bool? TryParseConnectResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type)) return null;
            if (type.GetString() != "connect_response") return null;

            if (!root.TryGetProperty("accepted", out var accepted)) return null;

            return accepted.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return null;   // 別種の制御メッセージ
        }
    }

    /// <summary>経路に応じた状態表示を更新する。</summary>
    private void SetTransportState(
        VMonitor.Core.Models.TransportType transportType, string status, bool connected)
    {
        if (transportType == VMonitor.Core.Models.TransportType.USB)
            SetUsbState(status, connected);
        else
            SetOutboundState(status, connected, busy: !connected);
    }

    /// <summary>
    /// 制御チャンネルの中身から画面サイズを取り出す。
    /// 名乗り以外のメッセージなら null。
    /// </summary>
    private static Resolution? TryParseHelloResolution(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type)) return null;
            if (type.GetString() != "hello") return null;

            if (!root.TryGetProperty("width",  out var w)) return null;
            if (!root.TryGetProperty("height", out var h)) return null;

            int width  = w.GetInt32();
            int height = h.GetInt32();

            // 極端な値でモニターを作らせない。
            //
            // 上限・下限は縦横を区別せずに当てる。対応解像度の定義は横長を
            // 前提にした値（最大 3840x2160）だが、スマホの画面は縦長。
            // 高さを 2160 と比べると、Pixel 6a の 1080x2400 のような
            // ごく普通の端末が「対応外」として弾かれてしまう。
            int smallest = Math.Min(Resolution.MinSupported.Width, Resolution.MinSupported.Height);
            int largest  = Math.Max(Resolution.MaxSupported.Width, Resolution.MaxSupported.Height);

            if (width  < smallest || width  > largest) return null;
            if (height < smallest || height > largest) return null;

            return new Resolution(width, height);
        }
        catch
        {
            // 名乗り以外の制御メッセージ。異常ではない
            return null;
        }
    }

    // ── 取り込み元ディスプレイの特定 ─────────────────────────────────────

    /// <summary>
    /// 仮想ディスプレイに対応する Windows のディスプレイ名を突き止める。
    /// </summary>
    /// <param name="before">仮想ディスプレイを繋ぐ前のディスプレイ名の一覧。</param>
    /// <returns>見つかったディスプレイ名。現れなければ null。</returns>
    /// <remarks>
    /// <para>
    /// 解像度や番号で当てにいくと取り違える。同じ解像度の実ディスプレイが
    /// あるかもしれないし、番号は画面の増減でずれる。
    /// 「繋ぐ前には無くて、繋いだ後にある 1 枚」で特定するのが確実。
    /// </para>
    /// <para>
    /// モニターの到着とデスクトップ構成への組み込みは非同期に進むので、
    /// 現れるまで少し待つ。
    /// </para>
    /// </remarks>
    private async Task<string?> WaitForNewOutputAsync(
        IReadOnlySet<string> before, CancellationToken ct)
    {
        const int PollIntervalMs = 250;
        const int TimeoutMs      = 8000;

        for (int waited = 0; waited < TimeoutMs; waited += PollIntervalMs)
        {
            if (ct.IsCancellationRequested) return null;

            foreach (var output in SafeListOutputsDetailed())
            {
                if (before.Contains(output.DeviceName)) continue;

                // デスクトップに組み込まれるまでは複製を作れない
                if (!output.AttachedToDesktop) continue;

                _logger.Info("ConnectionServer",
                    $"Virtual display output: {output.DeviceName} " +
                    $"{output.Width}x{output.Height} at ({output.Left},{output.Top})");

                return output.DeviceName;
            }

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { return null; }
        }

        return null;
    }

    private static IReadOnlySet<string> SafeListOutputs()
        => SafeListOutputsDetailed().Select(o => o.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<VMonitor.Streamer.Capture.DesktopDuplicationCapture.OutputInfo>
        SafeListOutputsDetailed()
    {
        try
        {
            return VMonitor.Streamer.Capture.DesktopDuplicationCapture.ListOutputs();
        }
        catch
        {
            // 列挙できない状況（GPU の切り替え中など）でも接続処理は続ける
            return Array.Empty<VMonitor.Streamer.Capture.DesktopDuplicationCapture.OutputInfo>();
        }
    }

    /// <summary>
    /// アクセサリーモードの端末が USB から消えるまで待つ。
    /// </summary>
    private static async Task WaitForAccessoryGoneAsync(CancellationToken ct)
    {
        const int PollIntervalMs = 2000;

        while (!ct.IsCancellationRequested)
        {
            bool present;

            try
            {
                present = AoaDevice.ListDevices().Any(d => d.InAccessoryMode);
            }
            catch
            {
                return;   // 列挙できないなら待っても仕方がない
            }

            if (!present) return;

            try { await Task.Delay(PollIntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, X509Certificate2 cert, CancellationToken ct)
    {
        var remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.Info("ConnectionServer", $"Client connected: {remoteEp}");

        try
        {
            // 開発段階: 素 TCP（TLS なし）でトランスポートを確立する
            var transport = new WifiTransport();
            transport.AcceptPlain(tcpClient);

            // デバイス情報を仮作成（実際は制御チャンネルで受け取る）
            var remoteAddr = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            // 繋ぎ直しで一覧が増えないよう、相手のアドレスから識別子を決める
            var device = new DeviceInfo(
                Id: DeviceIdentifier.FromKey($"vmonitor:wifi:{remoteAddr}"),
                Name: $"Android ({remoteAddr})",
                Platform: DevicePlatform.Android,
                PhysicalResolution: new Resolution(1080, 1920),
                PixelDensity: 420f);

            await RunSessionAsync(transport, device, remoteEp,
                                  VMonitor.Core.Models.TransportType.WiFi, ct);
        }
        catch (Exception ex)
        {
            _logger.Warn("ConnectionServer", $"Client handling error: {ex.Message}");
        }
        finally
        {
            tcpClient.Close();
            _logger.Info("ConnectionServer", $"Client disconnected: {remoteEp}");
        }
    }

    /// <summary>
    /// 確立済みのトランスポート 1 本ぶんのセッションを回す。
    /// </summary>
    /// <remarks>
    /// Wi-Fi でも USB 直結 (AOA) でも、繋がってしまえば後の流れは同じ。
    /// 仮想ディスプレイの接続、画面の取り込み、H.264 での配信、
    /// 送り返されてくるタッチの注入まで、ここでまとめて面倒を見る。
    /// 抜けるときは仮想モニターの取り外しと接触の解放も行う。
    /// </remarks>
    /// <summary>セッションがどう終わったか。</summary>
    private enum SessionOutcome
    {
        /// <summary>最後まで動いて切断された。</summary>
        Completed,

        /// <summary>利用者が接続を拒否した。</summary>
        Denied,
    }

    private async Task<SessionOutcome> RunSessionAsync(
        ITransport                            transport,
        DeviceInfo                            device,
        string                                label,
        VMonitor.Core.Models.TransportType    transportType,
        CancellationToken                     ct)
    {
        var outcome = SessionOutcome.Completed;

        var streamer = new VMonitor.Streamer.Streamer();

        // 画面ミラー元とタッチ注入先
        VMonitor.Streamer.Capture.DesktopMirrorSource? mirror = null;
        WindowsInkInjector? injector = null;

        // 仮想ディスプレイドライバへの制御チャンネル。
        // ドライバ未導入（ミラーモードのみ）の場合は null になる。
        VirtualDisplayControl? virtualDisplay = null;

        // 受信の列挙子と、名乗り待ちで使いかけた読み出し。
        // 後始末の順番に決まりがあるので、try の外で持つ。
        IAsyncEnumerator<(ChannelId Channel, Memory<byte> Data)>? receiver = null;
        Task<bool>? pendingRead = null;

        try
        {
            string remoteEp = label;

            // 受信の列挙子はここで 1 つだけ作り、セッション中ずっと使い回す。
            // 名乗りの受け取りと、その後のタッチ・制御の処理で同じものを使う。
            receiver = transport.ReceiveAsync(ct).GetAsyncEnumerator(ct);

            // どちらが言い出したかで、承認を出す先が変わる。
            var (approved, leftover) = await NegotiateConnectAsync(
                transport, receiver, device, transportType, ct);

            pendingRead = leftover;

            if (!approved)
            {
                _logger.Info("ConnectionServer", $"Connection denied: {remoteEp}");
                outcome = SessionOutcome.Denied;
                return outcome;
            }

            // 端末の画面サイズを聞く。仮想ディスプレイをそれに合わせて作る。
            //
            // 承認のやり取りで投げたままの読み出しがあれば引き継ぐ。
            // 同じ列挙子に MoveNextAsync を重ねて呼ぶことはできない。
            var (reported, pending) = await WaitForDeviceHelloAsync(
                transport, receiver, pendingRead, ct);

            pendingRead = pending;

            if (reported is not null)
                device = device with { PhysicalResolution = reported };

            // 端末が名乗ってきたら、一覧の表示をその呼び名に差し替える。
            // 「Android 端末（USB）」のままだと、複数台あるとき見分けが付かない。
            if (_lastDeviceName is not null)
            {
                device = device with { Name = _lastDeviceName };
                RenameUsbCandidate(_lastDeviceName, transportType);
            }

            bool requireVirtualDisplay = _displaySettings.RequireVirtualDisplay;

            // 仮想ディスプレイドライバが入っていれば、この接続の間だけ
            // 仮想モニターを接続状態にする。
            //
            // 常設にすると、スマホを繋いでいない間も Windows からは
            // ディスプレイが 1 枚多く見えたままになり、ウィンドウがそちらへ
            // 飛んだりマウスが画面外へ抜けたりする。
            //
            virtualDisplay = VirtualDisplayControl.TryOpen();

            if (virtualDisplay is null)
                _logger.Info("ConnectionServer", "Virtual display driver not installed");

            // 指定した解像度で取り込みの構成を作り直す。
            //
            // 端末を回すと縦横が入れ替わるので、そのたびにここを通る。
            // 仮想モニターは 1 つの解像度しか名乗らない設計（EDID もそれに
            // 合わせて作る）ため、向きが変わったら作り直すのが素直。
            async Task<bool> BuildCaptureAsync(Resolution requested)
            {
                // 拡大率のぶん解像度を下げる。
                //
                // 映像はスマホ側で画面いっぱいに伸ばされるので、
                // 低い解像度で作れば、そのぶん大きく見える。
                // スマホの画素数そのままだと Windows の文字が細かすぎる。
                var resolution = _displaySettings.ApplyScale(requested);

                if (resolution != requested)
                {
                    _logger.Info("ConnectionServer",
                        $"拡大率 {_displaySettings.SafeScalePercent}%: " +
                        $"{requested.Width}x{requested.Height} → " +
                        $"{resolution.Width}x{resolution.Height}");
                }

                // 動いているものを先に畳む
                await streamer.StopAsync();

                injector?.Dispose();
                injector = null;

                mirror?.Dispose();
                mirror = null;

                // 前の仮想モニターが残っていると「繋いだら 1 枚増える」という
                // 前提が崩れる（既に出ているので増えない）。必ず外してから繋ぐ。
                if (virtualDisplay is not null && virtualDisplay.GetState().Connected)
                {
                    virtualDisplay.Disconnect();
                    await Task.Delay(800, ct);
                }

                // 「どのディスプレイを取り込むか」を決めるため、繋ぐ前の一覧を控える。
                var outputsBefore = SafeListOutputs();

                string? deviceName = null;

                if (virtualDisplay is not null)
                {
                    if (virtualDisplay.Connect(resolution.Width, resolution.Height))
                    {
                        _logger.Info("ConnectionServer",
                            $"Virtual display connected: {resolution.Width}x{resolution.Height}");

                        deviceName = await WaitForNewOutputAsync(outputsBefore, ct);

                        if (deviceName is null)
                        {
                            _logger.Warn("ConnectionServer",
                                "Virtual display did not appear as a capturable output");
                        }
                    }
                    else
                    {
                        _logger.Warn("ConnectionServer", "Virtual display connect failed");
                    }
                }

                // 仮想ディスプレイを使えないときの扱いは設定で決まる。
                //
                // 既定（強制）では諦める。黙って PC 画面のミラーに落ちると、
                // 2 枚目のモニターとして繋いだ利用者には「同じ画面が出てきた」
                // としか見えず、なぜそうなったのかも分からない。
                if (deviceName is null && requireVirtualDisplay)
                {
                    _logger.Error("ConnectionServer",
                        "拡張ディスプレイを用意できませんでした。仮想ディスプレイドライバが導入されているか確認してください。" +
                        "（設定で「仮想ディスプレイを必須にする」を外すと、PC 画面のミラーで接続できます）");
                    return false;
                }

                // 取り込み元を決める。
                //   仮想ディスプレイが使えるなら、その画面そのもの（＝拡張された 2 枚目）
                //   使えないなら PC のメイン画面（ミラー）
                mirror = deviceName is not null
                    ? new VMonitor.Streamer.Capture.DesktopMirrorSource(deviceName, targetFps: 60)
                    : new VMonitor.Streamer.Capture.DesktopMirrorSource(outputIndex: 0, targetFps: 60);

                _logger.Info("ConnectionServer",
                    (deviceName is not null ? "Capturing virtual display" : "Mirroring main display") +
                    $" {deviceName ?? "(primary)"}: " +
                    $"{mirror.Resolution.Width}x{mirror.Resolution.Height} " +
                    $"origin=({mirror.OriginX},{mirror.OriginY})");

                // タッチ注入先を用意し、取り込み元ディスプレイに合わせて座標変換を設定する。
                // ここを合わせておかないと、注入座標が画面外に落ちてタッチが効かない。
                injector = new WindowsInkInjector
                {
                    DisplayOriginX = mirror.OriginX,
                    DisplayOriginY = mirror.OriginY,
                };
                injector.UpdateTransform(mirror.Resolution, Orientation.Portrait);

                // 取り込み元の解像度が変わったら座標変換を追従させる
                var ink = injector;
                mirror.ResolutionUpdated += (_, e) =>
                    ink.UpdateTransform(e.Resolution, Orientation.Portrait);

                return true;
            }

            // 端末が今どの向きなのか。名乗ってきた解像度をそのまま覚えておく。
            var currentResolution = device.PhysicalResolution;

            if (!await BuildCaptureAsync(currentResolution))
                return outcome;

            // セッションを確立する
            var sessionManager = new SessionManager(transport, mirror!, injector!);
            var session = await sessionManager.EstablishSessionAsync(device, ct);
            _logger.Info("ConnectionServer", $"Session established: {session.SessionId}");

            // SessionManager はセッション確立時に「スマホの解像度」で変換を設定する。
            // スマホに映っているのは取り込み元のディスプレイなので、そちらに戻す。
            injector!.UpdateTransform(mirror!.Resolution, Orientation.Portrait);

            // 接続済みとして UI に通知（候補リストに1回だけ追加）
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _vm.SetConnected(device, transportType);
            });

            // 映像ストリーミングを開始する（キャプチャ → H.264 エンコード → 送信）
            async Task StartStreamingAsync()
            {
                streamer.Config = streamer.Config with { TargetResolution = mirror!.Resolution };
                await streamer.StartAsync(session.DisplayHandle, mirror!, transport, ct);

                _logger.Info("ConnectionServer",
                    $"Streaming started: {mirror!.Resolution.Width}x{mirror.Resolution.Height}");
            }

            await StartStreamingAsync();

            _ = LogStreamerHealthAsync(streamer, ct);

            // 送ったものが端末に届くまでどれだけ遅れているかを測る
            _ = SendLatencyProbesAsync(transport, ct);

            // 受信ループ（タッチ・制御チャンネルを処理）。
            // 名乗りを受け取ったのと同じ列挙子の続きから読む。
            while (true)
            {
                // 名乗り待ちで投げたままの読み出しがあれば、先にそれを消化する。
                // 同じ列挙子に MoveNextAsync を重ねて呼ぶことはできない。
                bool hasNext;

                if (pendingRead is not null)
                {
                    hasNext     = await pendingRead;
                    pendingRead = null;
                }
                else
                {
                    hasNext = await receiver.MoveNextAsync();
                }

                if (!hasNext) break;

                var (channel, data) = receiver.Current;

                switch (channel)
                {
                    case ChannelId.Touch:
                        // 「見るだけ」の設定なら、押す・動かすは注入しない。
                        //
                        // ただし「離す」は通す。指を置いたまま設定を切り替えると、
                        // 押しっぱなしの接触が PC に残り、そのままでは
                        // マウス操作もできなくなる。
                        HandleTouchPayload(data.Span, injector!, mirror!.Resolution,
                                           releasesOnly: !_displaySettings.EnableTouch);
                        break;

                    case ChannelId.Control:
                    {
                        // 端末を回すと、新しい向きの画面サイズを名乗ってくる。
                        // 仮想モニターは 1 つの解像度しか持たないので、作り直して
                        // 向きを合わせる。合わせないと縦横比が崩れて帯が出る。
                        // 往復時間の応答なら記録して終わり
                        HandleLatencyPong(data.Span);

                        var announced = TryParseHelloResolution(data.Span);

                        if (announced is null || announced == currentResolution)
                            break;

                        _logger.Info("ConnectionServer",
                            $"Device screen changed: {currentResolution.Width}x{currentResolution.Height} " +
                            $"→ {announced.Width}x{announced.Height}");

                        currentResolution = announced;

                        if (!await BuildCaptureAsync(currentResolution))
                        {
                            _logger.Error("ConnectionServer",
                                "新しい向きで拡張ディスプレイを用意できませんでした。");
                            return outcome;
                        }

                        await StartStreamingAsync();
                        break;
                    }

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常なキャンセル
        }
        catch (Exception ex)
        {
            _logger.Warn("ConnectionServer", $"Session error ({label}): {ex.Message}");
        }
        finally
        {
            // 投げっぱなしの読み出しが残っている状態で列挙子を捨てると
            // NotSupportedException になる。終わるのを待ってから片付ける。
            if (pendingRead is not null)
            {
                try { await Task.WhenAny(pendingRead, Task.Delay(1000)); } catch { }
            }

            if (receiver is not null && (pendingRead is null || pendingRead.IsCompleted))
            {
                try { await receiver.DisposeAsync(); } catch { }
            }

            await streamer.StopAsync();

            // スマホが離れたら仮想モニターも取り外す。
            // 残したままにすると、繋いでいないのにディスプレイが増えたままになる。
            if (virtualDisplay is not null)
            {
                virtualDisplay.Dispose();   // Dispose 内で切断してからハンドルを閉じる
                _logger.Info("ConnectionServer", "Virtual display disconnected");
            }

            // 切断時に押されっぱなしの指が残らないよう、接触をすべて解放する
            injector?.Dispose();
            mirror?.Dispose();

            Application.Current?.Dispatcher.Invoke(() => _vm.SetDisconnected());
            _logger.Info("ConnectionServer", $"Session ended: {label}");
        }

        return outcome;
    }

    /// <summary>
    /// スマホから届いたタッチイベントを復元し、Windows へ注入する。
    /// </summary>
    /// <remarks>
    /// 壊れたパケットはデコーダーが null を返すので、その場合は黙って捨てる。
    /// 不正な入力で接続ごと落とさないため、例外はここで止める。
    /// </remarks>
    /// <summary>受信したタッチイベント数（診断用）。</summary>
    private long _touchEventCount;

    private void HandleTouchPayload(
        ReadOnlySpan<byte> payload, WindowsInkInjector injector, Resolution displayResolution,
        bool releasesOnly = false)
    {
        var touchEvent = TouchEventCodec.Decode(payload);

        if (touchEvent is null)
        {
            _logger.Warn("ConnectionServer", $"Malformed touch packet: {payload.Length} bytes");
            return;
        }

        // 「見るだけ」の設定でも、指を離す知らせだけは通す。
        // 捨ててしまうと、押されたままの接触が残り続ける。
        if (releasesOnly)
        {
            bool allReleased = touchEvent.Points.All(
                p => p.Phase is TouchPhase.Ended or TouchPhase.Cancelled);

            if (!allReleased) return;
        }

        // 最初の 1 件と、以降は 100 件ごとに記録する。
        // 毎回出すとログが映像より速く流れて読めなくなる。
        long count = Interlocked.Increment(ref _touchEventCount);

        try
        {
            // スマホは PC の画面をそのまま全画面表示しているため、
            // 正規化座標はディスプレイ座標に直接対応する。
            // ここで端末の向きに応じた回転を重ねると二重に回ってしまう
            // （レンダラー側が既に端末の向きで描画しているため）。
            injector.InjectTouch(
                touchEvent.Points,
                new DisplayTransform(displayResolution, Orientation.Portrait));

            // 最初の 1 件と、以降は 100 件ごとに記録する。
            // 毎回出すとログが映像より速く流れて読めなくなる。
            if (count == 1 || count % 100 == 0)
            {
                var first = touchEvent.Points[0];
                var pixel = injector.TransformPoint(first.X, first.Y);

                _logger.Info("ConnectionServer",
                    $"Touch #{count}: phase={first.Phase} norm=({first.X:F3},{first.Y:F3}) " +
                    $"→ pixel={pixel} 表示={displayResolution.Width}x{displayResolution.Height} " +
                    $"接触数={injector.ActiveContactCount} " +
                    $"注入成功={injector.LastInjectionSucceeded} " +
                    $"Win32エラー={injector.LastBackendError}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("ConnectionServer", $"Touch injection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// ストリーミングが実際に流れているかを定期的にログへ残す。
    /// </summary>
    private async Task LogStreamerHealthAsync(VMonitor.Streamer.Streamer streamer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);

                var stats = streamer.Stats;
                _logger.Info("ConnectionServer",
                    $"Streamer: encoded={stats.FramesEncoded} sent={stats.FramesSent} " +
                    $"fps={stats.CurrentFps:F1} lastEncodeMs={stats.LastFrameEncodeMs}");

                if (stats.FramesSent == 0)
                {
                    _logger.Warn("ConnectionServer",
                        "映像が 1 フレームも送信されていません。" +
                        $"ネイティブエンコーダー利用可否={VMonitor.Streamer.NativeEncoderBridge.IsAvailable}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>開発用の自己署名証明書を生成する。</summary>
    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "cn=vmonitor-dev",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        return X509Certificate2.CreateFromPem(
            cert.ExportCertificatePem(),
            rsa.ExportRSAPrivateKeyPem());
    }
}
