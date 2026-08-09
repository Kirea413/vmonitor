using System.Runtime.Versioning;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Input;
using VMonitor.Session.Transport;
using VMonitor.Streamer;
using VMonitor.Streamer.Capture;

namespace VMonitor.Diagnostics;

/// <summary>
/// vmonitor が動くために必要な各機能を、実際に呼び出して確認する。
/// </summary>
/// <remarks>
/// 「設定を読む」のではなく「実際に叩いてみる」方針にしている。
/// 設定上は問題なくても、ドライバやファイアウォール、権限の都合で
/// 実行時に初めて失敗することが多いため。
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Checks
{
    /// <summary>各チェックは見つけた問題の件数を返す。</summary>

    // ── ポインター注入 ───────────────────────────────────────────────────

    public static int CheckPointerInjection()
    {
        Section("タッチ / ペン入力の注入");

        int problems = 0;

        using var backend = new Win32PointerInjectionBackend();

        bool touchOk = backend.Initialize(PointerInjectionMode.Touch, WindowsInkInjector.MaxContacts);
        Report("タッチ注入 (InjectTouchInput)", touchOk,
               touchOk ? null : $"初期化に失敗しました (エラー {backend.LastError})。");
        if (!touchOk) problems++;

        bool penOk = backend.Initialize(PointerInjectionMode.Pen, 1);
        Report("ペン注入 (Windows Ink)", penOk,
               penOk ? null : "Windows 10 1809 以降が必要です。タッチのみで動作します。");
        if (!penOk) problems++;

        Console.WriteLine("  ※ 入力は注入していません（デバイスの初期化可否のみ確認）。");

        return problems;
    }

    // ── 画面キャプチャ ───────────────────────────────────────────────────

    public static int CheckScreenCapture()
    {
        Section("画面キャプチャ (Desktop Duplication)");

        try
        {
            using var capture = new DesktopDuplicationCapture(0);

            // 画面に変化がないとフレームは返らないので、少し粘る
            VMonitor.Core.Models.VideoFrame? frame = null;
            for (int i = 0; i < 60 && frame is null; i++)
                frame = capture.TryCaptureFrame(timeoutMs: 50);

            if (frame is null)
            {
                Report("フレーム取得", false,
                       "画面に変化がないとフレームは返りません。マウスを動かして再実行してください。");
                return 1;
            }

            // 解像度は実際に届いたフレームの値を出す。
            // プロセスが DPI 非対応だと OS の報告する値は仮想化された論理サイズになり、
            // 物理ピクセル数と食い違うため。
            Console.WriteLine($"  対象ディスプレイ: {frame.Resolution.Width} x {frame.Resolution.Height} " +
                              $"（仮想デスクトップ上の原点 {capture.OriginX}, {capture.OriginY}）");

            // 全画素が同じ色なら、コピーが機能していない可能性が高い
            var span = frame.Data.Span;
            bool uniform = true;
            for (int p = 4; p + 3 < span.Length && uniform; p += 4096)
            {
                if (span[p] != span[0] || span[p + 1] != span[1] || span[p + 2] != span[2])
                    uniform = false;
            }

            Report("フレーム取得", true, $"{frame.Data.Length:N0} バイト / フレーム");

            if (uniform)
            {
                Report("画素の内容", false,
                       "全画素が同じ色でした。画面が単色でないのにこうなる場合は、キャプチャが機能していません。");
                return 1;
            }

            Report("画素の内容", true, null);
            return 0;
        }
        catch (Exception ex)
        {
            Report("Desktop Duplication", false, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ── エンコーダー ─────────────────────────────────────────────────────

    public static int CheckEncoder()
    {
        Section("H.264 エンコード");

        // この PC にどのエンコーダーがあるのかをまず出す。
        // ソフトウェアしか無ければ、1 枚あたり数十ミリ秒は避けられない。
        var encoders = VMonitor.Streamer.EncoderCapabilities.ListH264Encoders();

        if (encoders.Count > 0)
        {
            Console.WriteLine("  利用できるエンコーダー:");
            foreach (var e in encoders)
            {
                Console.WriteLine($"    [{(e.IsHardware ? "ハードウェア" : "ソフトウェア")}] " +
                                  $"{(e.IsAsync ? "非同期" : "同期  ")}  {e.Name}");
            }
            Console.WriteLine();
        }

        if (!NativeEncoderBridge.IsAvailable)
        {
            Report("ネイティブエンコーダー", false,
                   "VMonitor.Encoder.dll が読み込めません。SETUP.md の手順 1 でビルドし、" +
                   "実行ファイルと同じフォルダに置いてください。これがないと映像は送信されません。");
            return 1;
        }

        Report("ネイティブエンコーダー", true, null);

        int problems = 0;
        bool formatChecked = false;

        foreach (var (w, h) in new[] { (640, 480), (1280, 720), (1920, 1080) })
        {
            var buffer = new byte[w * h * 4];

            // 単色だと圧縮が効きすぎて実際より速く見えるため、模様を入れる
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = (byte)((i * 7 + (i / 997)) & 0xFF);

            // 立ち上がりのフレームで出力形式を確かめる。
            //
            // SPS/PPS はストリーム先頭の IDR にしか付かないので、
            // ウォームアップで捨ててしまうと以降のフレームからは見つからない。
            // 形式の確認はここで済ませる。
            bool sawSpsPps = false;
            for (int i = 0; i < 5; i++)
            {
                var nal = NativeEncoderBridge.Encode(buffer, w, h, 8_000_000, 60, i * 16_666L);
                if (nal is { Length: > 0 } && LooksLikeH264(nal)) sawSpsPps = true;
            }

            if (!formatChecked)
            {
                formatChecked = true;
                Report("出力形式 (H.264 SPS/PPS)", sawSpsPps,
                       sawSpsPps ? null : "H.264 の SPS/PPS が見つかりませんでした。受信側でデコードできません。");
                if (!sawSpsPps) problems++;
            }

            const int measured = 30;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0;

            for (int i = 0; i < measured; i++)
            {
                var nal = NativeEncoderBridge.Encode(buffer, w, h, 8_000_000, 60, (5 + i) * 16_666L);
                if (nal is { Length: > 0 }) ok++;
            }

            sw.Stop();

            if (ok == 0)
            {
                Report($"{w}x{h}", false, "1 フレームもエンコードできませんでした。");
                problems++;
                continue;
            }

            double fps = ok / sw.Elapsed.TotalSeconds;

            // 内訳も出す。変換とエンコード本体のどちらが重いかで打ち手が変わる。
            var (convertUs, mftUs) = VMonitor.Streamer.EncoderCapabilities.GetLastFrameTiming();

            Console.WriteLine($"  {w,4}x{h,-4} : {fps,6:F1} fps ({sw.Elapsed.TotalMilliseconds / ok,5:F1} ms/frame)"
                              + $"  内訳: 色変換 {convertUs / 1000.0,4:F1}ms / 符号化 {mftUs / 1000.0,4:F1}ms"
                              + (fps >= 30 ? "" : "  ← 30fps 未満"));
        }

        // ── 入れてから出るまで何枚ぶん遅れるかを測る ─────────────────────
        //
        // H.264 のエンコーダーは内部にフレームを溜めることがある。
        // N 枚ぶん遅れるなら、それはそのまま N/fps 秒の遅延になる。
        // 10fps で 5 枚なら 0.5 秒。体感の遅れの正体になりうるので実測する。
        MeasureEncoderPipelineDelay();

        // どちらのエンコーダーで動いたのか、非同期なら途中で詰まっていないかを出す。
        var diag = VMonitor.Streamer.EncoderCapabilities.GetDiagnostics();

        Console.WriteLine();
        Console.WriteLine($"  実際に使ったのは: {(diag.IsAsync ? "非同期 (ハードウェア)" : "同期 (ソフトウェア)")}");

        if (diag.IsAsync)
        {
            Console.WriteLine($"    イベント数     : {diag.EventsSeen}");
            Console.WriteLine($"    入力要求       : {diag.NeedInputSeen}");
            Console.WriteLine($"    出力通知       : {diag.HaveOutputSeen}");
            Console.WriteLine($"    ProcessInput   : {diag.ProcessInputCalls} 回 (失敗 {diag.ProcessInputFails})");
            Console.WriteLine($"    ProcessOutput  : {diag.ProcessOutputCalls} 回");
            Console.WriteLine($"    最後の HRESULT : 0x{diag.LastHr:X8}");
        }

        Console.WriteLine();
        Console.WriteLine("  環境変数 VMONITOR_ENCODER=hw を付けて実行すると、");
        Console.WriteLine("  ハードウェアエンコーダーを優先して同じ計測ができます。");

        return problems;
    }

    /// <summary>
    /// Annex-B のスタートコードを走査して、SPS(7) と PPS(8) が含まれるか確認する。
    /// これらがないと受信側のデコーダーは映像を組み立てられない。
    /// </summary>
    private static bool LooksLikeH264(byte[] nal)
    {
        bool sps = false, pps = false;

        for (int p = 0; p + 4 < nal.Length; p++)
        {
            int type = -1;

            if (nal[p] == 0 && nal[p + 1] == 0 && nal[p + 2] == 0 && nal[p + 3] == 1 && p + 4 < nal.Length)
                type = nal[p + 4] & 0x1F;
            else if (nal[p] == 0 && nal[p + 1] == 0 && nal[p + 2] == 1)
                type = nal[p + 3] & 0x1F;

            if (type == 7) sps = true;
            if (type == 8) pps = true;
        }

        return sps && pps;
    }

    // ── タッチ注入の実地テスト ───────────────────────────────────────────

    /// <summary>
    /// 実際にタッチを注入して、Windows が受け付けるかを確かめる。
    /// </summary>
    /// <remarks>
    /// スマホを繋がなくてもタッチ注入の可否を切り分けられる。
    /// 画面中央付近に短いドラッグを 1 回入れるだけで、実害はほぼ無い。
    /// </remarks>
    public static int CheckTouchInjection()
    {
        Section("タッチ注入の実地テスト");

        if (!OperatingSystem.IsWindows())
        {
            Report("実行環境", false, "Windows でのみ実行できます。");
            return 1;
        }

        var backend = new Win32PointerInjectionBackend();
        using var injector = new WindowsInkInjector(backend, ownsBackend: true);

        int width  = 1920, height = 1080;
        try
        {
            width  = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width;
            height = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Height;
        }
        catch { /* 取得できなければ既定値を使う */ }

        var resolution = new Resolution(width, height);
        var transform  = new DisplayTransform(resolution, VMonitor.Core.Models.Orientation.Portrait);
        injector.UpdateTransform(resolution, VMonitor.Core.Models.Orientation.Portrait);

        Console.WriteLine($"  対象画面: {width}x{height}");

        // 画面中央付近を少しだけドラッグする
        var steps = new (double X, double Y, TouchPhase Phase)[]
        {
            (0.50, 0.50, TouchPhase.Began),
            (0.51, 0.50, TouchPhase.Moved),
            (0.52, 0.50, TouchPhase.Moved),
            (0.53, 0.50, TouchPhase.Moved),
            (0.53, 0.50, TouchPhase.Ended),
        };

        int failures = 0;

        foreach (var (x, y, phase) in steps)
        {
            injector.InjectTouch(
                new List<TouchPoint> { new() { Id = 0, X = x, Y = y, Pressure = 1.0, Phase = phase } },
                transform);

            var pixel = injector.TransformPoint(x, y);
            bool ok = injector.LastInjectionSucceeded;

            Console.WriteLine(
                $"    {phase,-9} ({pixel.PixelX,5},{pixel.PixelY,5})  " +
                (ok ? "OK" : $"失敗 Win32={injector.LastBackendError}"));

            if (!ok) failures++;

            // 連続で呼びすぎると ERROR_NOT_READY になるため少し間を空ける
            Thread.Sleep(30);
        }

        Report("タッチ注入", failures == 0,
               failures == 0
                   ? "Windows がすべてのフレームを受け付けました。"
                   : $"{failures}/{steps.Length} フレームが拒否されました。");

        return failures == 0 ? 0 : 1;
    }

    // ── 仮想ディスプレイ ─────────────────────────────────────────────────

    /// <summary>
    /// 仮想ディスプレイドライバの接続・切断を実際に試す。
    /// </summary>
    /// <remarks>
    /// スマホを繋がなくても、ドライバの制御経路が通っているかを確認できる。
    /// 接続するとディスプレイが 1 枚増え、切断すると元に戻ることを見る。
    /// </remarks>
    public static int CheckVirtualDisplay()
    {
        Section("仮想ディスプレイドライバ");

        using var control = VMonitor.Driver.VirtualDisplayControl.TryOpen();

        if (control is null)
        {
            Report("ドライバへの接続", false,
                   "仮想ディスプレイドライバが見つかりません。ミラーモードのみ利用できます。" +
                   "拡張モードを使うには VMonitorSetup.exe でドライバを入れてください。");
            return 1;
        }

        Report("ドライバへの接続", true, null);

        int before = ScreenCount();
        Console.WriteLine($"  接続前の画面数: {before}");

        // 実際に仮想モニターを接続してみる
        bool connected = control.Connect(1920, 1080);
        Report("モニターの接続要求", connected,
               connected ? null : $"接続要求が失敗しました。{control.LastError}");

        if (!connected) return 1;

        // ディスプレイの構成変更は即座には反映されないので少し待つ
        int after = before;
        for (int i = 0; i < 20 && after <= before; i++)
        {
            Thread.Sleep(250);
            after = ScreenCount();
        }

        Console.WriteLine($"  接続後の画面数: {after}");

        var state = control.GetState();
        Console.WriteLine($"  ドライバの状態  : 接続={state.Connected} {state.Width}x{state.Height}");
        _ = state.Reachable;

        bool appeared = after > before;
        Report("仮想ディスプレイの出現", appeared,
               appeared ? null : "ドライバは接続を受け付けましたが、画面が増えていません。");

        // 後始末: 必ず切断して元の状態へ戻す
        control.Disconnect();

        int restored = after;
        for (int i = 0; i < 20 && restored >= after; i++)
        {
            Thread.Sleep(250);
            restored = ScreenCount();
        }

        Console.WriteLine($"  切断後の画面数: {restored}");

        bool removed = restored <= before;
        Report("仮想ディスプレイの切断", removed,
               removed ? null : "切断したのに画面が残っています。");

        return (appeared && removed) ? 0 : 1;
    }

    private static int ScreenCount()
    {
        try { return System.Windows.Forms.Screen.AllScreens.Length; }
        catch { return 0; }
    }

    // ── mDNS ─────────────────────────────────────────────────────────────

    public static async Task<int> CheckMdnsAsync()
    {
        Section("デバイス探索 (mDNS)");

        try
        {
            using var advertiser = new MdnsService();
            await advertiser.RegisterServiceAsync(7979, "vmonitor-doctor");
            Report("サービスの登録", true, "_vmonitor._tcp / ポート 7979");

            using var discoverer = new MdnsService();
            var found = await discoverer.DiscoverServicesAsync(timeoutMs: 4000);

            if (found.Count == 0)
            {
                Report("探索", false,
                       "mDNS の応答を受信できませんでした。ファイアウォールが UDP 5353 の受信を " +
                       "塞いでいる可能性があります。スマホから見つからない場合は手動 IP 接続を使ってください。");
                await advertiser.UnregisterServiceAsync();
                return 1;
            }

            Report("探索", true, $"{found.Count} 件検出");
            foreach (var r in found)
                Console.WriteLine($"    {r.ServiceName} → {r.IPAddress}:{r.Port}");

            await advertiser.UnregisterServiceAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Report("mDNS", false, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// エンコーダーが何枚ぶん遅れて出力するかを測る。
    /// </summary>
    /// <remarks>
    /// エンコーダーを作り直してから 1 枚ずつ入れ、
    /// 何回目の呼び出しで最初の出力が出るかを数える。
    /// ここが N 枚なら、配信は常に N/fps 秒だけ遅れる。
    /// </remarks>
    private static void MeasureEncoderPipelineDelay()
    {
        const int Width  = 1280;
        const int Height = 720;
        const int Fps    = 30;

        // 前のテストの状態を引きずらないよう作り直す
        VMonitor.Streamer.EncoderCapabilities.ResetEncoder();

        var buffer = new byte[Width * Height * 4];

        int firstOutputAt = -1;
        int outputs       = 0;
        const int Frames  = 40;

        for (int i = 0; i < Frames; i++)
        {
            // 毎回違う絵にする。同じ絵だと出力が出ないことがある。
            for (int p = 0; p < buffer.Length; p += 997)
                buffer[p] = (byte)(i * 13 + p);

            var nal = NativeEncoderBridge.Encode(
                buffer, Width, Height, 8_000_000, Fps, i * (1_000_000L / Fps));

            if (nal is { Length: > 0 })
            {
                outputs++;
                if (firstOutputAt < 0) firstOutputAt = i;
            }
        }

        Console.WriteLine();

        if (firstOutputAt < 0)
        {
            Report("エンコーダーの遅れ", false, $"{Frames} 枚入れても 1 枚も出ませんでした。");
            return;
        }

        double delayMs = firstOutputAt * 1000.0 / Fps;

        Console.WriteLine($"  入れてから出るまで: {firstOutputAt} 枚ぶん " +
                          $"（{Fps}fps 換算で {delayMs:F0} ms）");
        Console.WriteLine($"  {Frames} 枚入れて {outputs} 枚出力");

        if (firstOutputAt >= 3)
        {
            Console.WriteLine("  ※ エンコーダーが内部にフレームを溜めています。" +
                              "その枚数ぶん、映像は常に遅れます。");
        }
    }

    // ── 仮想ディスプレイからの取り込み ───────────────────────────────────

    /// <summary>
    /// 仮想ディスプレイを接続し、その画面を実際に取り込めるところまで確認する。
    /// </summary>
    /// <remarks>
    /// スマホが無くても、拡張表示の経路まるごとを確かめられる。
    /// <list type="number">
    ///   <item>仮想モニターを接続する</item>
    ///   <item>増えたディスプレイを名前で特定する</item>
    ///   <item>そのディスプレイから Desktop Duplication でフレームを取る</item>
    /// </list>
    /// 3 が通れば、スマホに送る絵が PC のメイン画面ではなく
    /// 仮想ディスプレイのものになっていると言える。
    /// </remarks>
    public static int CheckVirtualDisplayCapture(int requestWidth = 1080, int requestHeight = 1920)
    {
        Section("仮想ディスプレイの取り込み");

        using var control = VMonitor.Driver.VirtualDisplayControl.TryOpen();

        if (control is null)
        {
            Report("ドライバへの接続", false,
                   "仮想ディスプレイドライバが見つかりません。VMonitorSetup.exe で導入してください。");
            return 1;
        }

        // 前回の残りが繋がったままだと「増えた 1 枚」で見分けられない
        if (control.GetState().Connected)
        {
            control.Disconnect();
            Thread.Sleep(1000);
        }

        var before = DesktopDuplicationCapture.ListOutputs()
            .Select(o => o.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"  接続前のディスプレイ: {string.Join(", ", before)}");

        // スマホを想定した縦長の解像度で作る
        int Width  = requestWidth;
        int Height = requestHeight;

        bool connected = control.Connect(Width, Height);
        Report("モニターの接続要求", connected,
               connected ? $"{Width}x{Height}" : $"接続要求が失敗しました。{control.LastError}");

        if (!connected) return 1;

        try
        {
            // 増えたディスプレイを探す
            DesktopDuplicationCapture.OutputInfo? target = null;

            for (int i = 0; i < 32 && target is null; i++)
            {
                Thread.Sleep(250);

                foreach (var output in DesktopDuplicationCapture.ListOutputs())
                {
                    if (before.Contains(output.DeviceName)) continue;
                    if (!output.AttachedToDesktop)          continue;

                    target = output;
                    break;
                }
            }

            if (target is null)
            {
                Report("仮想ディスプレイの出現", false,
                       "モニターは接続されましたが、取り込める画面として現れませんでした。");
                return 1;
            }

            var found = target.Value;
            Report("仮想ディスプレイの出現", true,
                   $"{found.DeviceName} {found.Width}x{found.Height} " +
                   $"（仮想デスクトップ上の原点 {found.Left},{found.Top}）");

            // ここが本題。仮想ディスプレイそのものから取り込めるか。
            using var capture = new DesktopDuplicationCapture(found.DeviceName);

            VMonitor.Core.Models.VideoFrame? frame = null;
            for (int i = 0; i < 80 && frame is null; i++)
                frame = capture.TryCaptureFrame(timeoutMs: 50);

            if (frame is null)
            {
                Report("フレーム取得", false,
                       "仮想ディスプレイからフレームが返りませんでした。\n" +
                       "         ドライバがスワップチェーンを処理できていない可能性があります。");
                return 1;
            }

            Report("フレーム取得", true,
                   $"{frame.Resolution.Width}x{frame.Resolution.Height} / " +
                   $"{frame.Data.Length:N0} バイト");

            // 何も表示していない仮想ディスプレイは真っ黒なので、
            // 単色であること自体は異常ではない。ここでは寸法だけ確かめる。
            bool sizeMatches = frame.Resolution.Width  == found.Width
                            && frame.Resolution.Height == found.Height;

            Report("取り込んだ画面の寸法", sizeMatches,
                   sizeMatches
                       ? null
                       : $"ディスプレイは {found.Width}x{found.Height} なのに " +
                         $"取り込めたのは {frame.Resolution.Width}x{frame.Resolution.Height} でした。");

            SaveBitmap(frame, "vmonitor-virtual-display.bmp");

            return sizeMatches ? 0 : 1;
        }
        catch (Exception ex)
        {
            Report("仮想ディスプレイの取り込み", false, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            // 必ず元に戻す
            control.Disconnect();
        }
    }

    /// <summary>
    /// 画面が変わってから、それを取り込めるまでの時間を測る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// PC の符号化・転送・端末のデコードはいずれも実測済みで、合計しても
    /// 100ms に届かない。それでも体感が 0.5 秒あるなら、残るのは
    /// 「Windows が描いてから Desktop Duplication がそれを返すまで」しかない。
    /// 仮想ディスプレイは実物と違って、ドライバのスワップチェーンや
    /// デスクトップの合成を経由するぶん、余分に遅れる可能性がある。
    /// </para>
    /// <para>
    /// 仮想ディスプレイの上に白黒を切り替える窓を出し、
    /// 切り替えた瞬間から、取り込んだ絵に反映されるまでを測る。
    /// </para>
    /// </remarks>
    public static int CheckCaptureLag()
    {
        Section("画面の変化が取り込めるまでの時間");

        using var control = VMonitor.Driver.VirtualDisplayControl.TryOpen();

        if (control is null)
        {
            Report("ドライバへの接続", false, "仮想ディスプレイドライバが見つかりません。");
            return 1;
        }

        if (control.GetState().Connected)
        {
            control.Disconnect();
            Thread.Sleep(1000);
        }

        var before = DesktopDuplicationCapture.ListOutputs()
            .Select(o => o.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!control.Connect(1080, 1920))
        {
            Report("仮想ディスプレイの接続", false, control.LastError);
            return 1;
        }

        try
        {
            DesktopDuplicationCapture.OutputInfo? target = null;

            for (int i = 0; i < 32 && target is null; i++)
            {
                Thread.Sleep(250);
                foreach (var o in DesktopDuplicationCapture.ListOutputs())
                {
                    if (before.Contains(o.DeviceName) || !o.AttachedToDesktop) continue;
                    target = o;
                    break;
                }
            }

            if (target is null)
            {
                Report("仮想ディスプレイの出現", false, "取り込める画面として現れませんでした。");
                return 1;
            }

            var display = target.Value;
            Report("仮想ディスプレイ", true, $"{display.DeviceName} {display.Width}x{display.Height}");

            // 仮想ディスプレイの上に、色を切り替えるだけの窓を出す
            using var form = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                StartPosition   = System.Windows.Forms.FormStartPosition.Manual,
                Location        = new System.Drawing.Point(display.Left + 40, display.Top + 40),
                Size            = new System.Drawing.Size(400, 400),
                BackColor       = System.Drawing.Color.Black,
                TopMost         = true,
                ShowInTaskbar   = false,
            };

            form.Show();
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(500);

            using var capture = new DesktopDuplicationCapture(display.DeviceName);

            var samples = new List<long>();
            bool white = false;

            for (int trial = 0; trial < 12; trial++)
            {
                // 直前の絵を掃き出しておく
                for (int i = 0; i < 10; i++) capture.TryCaptureFrame(10);

                white = !white;

                var sw = System.Diagnostics.Stopwatch.StartNew();

                form.BackColor = white ? System.Drawing.Color.White : System.Drawing.Color.Black;
                form.Refresh();
                System.Windows.Forms.Application.DoEvents();

                long detectedMs = -1;

                while (sw.ElapsedMilliseconds < 2000)
                {
                    var frame = capture.TryCaptureFrame(10);
                    if (frame is null) continue;

                    if (RegionIsWhite(frame, 100, 100) == white)
                    {
                        detectedMs = sw.ElapsedMilliseconds;
                        break;
                    }
                }

                // 最初の 2 回は暖機として捨てる
                if (trial >= 2 && detectedMs >= 0) samples.Add(detectedMs);
            }

            form.Hide();

            if (samples.Count == 0)
            {
                Report("取り込みまでの時間", false, "変化を検出できませんでした。");
                return 1;
            }

            samples.Sort();

            Console.WriteLine($"  最小 {samples[0]} ms / 中央 {samples[samples.Count / 2]} ms / " +
                              $"最大 {samples[^1]} ms  （{samples.Count} 回）");

            long median = samples[samples.Count / 2];

            Report("取り込みまでの時間", median < 100,
                   median < 100
                       ? null
                       : $"画面が変わってから取り込めるまでに {median} ms かかっています。" +
                         "これがそのまま体感の遅れになります。");

            return median < 100 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Report("計測", false, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            control.Disconnect();
        }
    }

    /// <summary>指定位置の画素が白寄りかどうか。</summary>
    private static bool RegionIsWhite(VMonitor.Core.Models.VideoFrame frame, int x, int y)
    {
        var data = frame.Data.Span;

        int offset = (y * frame.Resolution.Width + x) * 4;
        if (offset + 2 >= data.Length) return false;

        // BGRA。3 色の平均で判断する
        int brightness = (data[offset] + data[offset + 1] + data[offset + 2]) / 3;
        return brightness > 128;
    }

    // ── AOA (USB 直結) ───────────────────────────────────────────────────

    /// <summary>
    /// USB に繋がっている端末を一覧し、AOA で掴めるかを確認する。
    /// </summary>
    /// <param name="performSwitch">
    /// true なら実際にアクセサリーモードへの切り替えまで行う。
    /// 切り替えると adb 接続は切れる（ケーブルを挿し直せば戻る）。
    /// </param>
    public static int CheckAoa(bool performSwitch)
    {
        Section("USB 直結 (AOA)");

        int problems = 0;

        // 1. まず何が見えているかを出す。
        //    ここで端末が「開けません」と出るなら、その先へは進めない。
        IReadOnlyList<AoaDevice.UsbDeviceSummary> devices;

        try
        {
            devices = AoaDevice.ListDevices();
        }
        catch (Exception ex)
        {
            Report("USB バスの列挙", false,
                   $"{ex.GetType().Name}: {ex.Message}（libusb-1.0.dll を読み込めていない可能性があります）");
            return 1;
        }

        Report("USB バスの列挙", true, $"{devices.Count} 台");

        foreach (var d in devices)
        {
            string name = string.IsNullOrEmpty(d.Product) ? "(製品名不明)" : d.Product;
            string state = d.InAccessoryMode ? "アクセサリーモード"
                         : d.Openable        ? "開けます"
                         :                     $"開けません: {d.OpenError}";

            Console.WriteLine($"    VID=0x{d.VendorId:X4} PID=0x{d.ProductId:X4}  {name}  [{state}]");
        }

        // 2. 既にアクセサリーモードなら、そのまま掴めるはず。
        bool alreadyAccessory = devices.Any(d => d.InAccessoryMode);

        if (alreadyAccessory)
        {
            using var opened = AoaDevice.OpenAccessory(out string openError);

            Report("アクセサリーデバイスの確保", opened != null, opened != null
                ? $"PID=0x{opened.ProductId:X4}  IN=0x{opened.InEndpoint:X2} OUT=0x{opened.OutEndpoint:X2}"
                : openError);

            if (opened == null) problems++;
            return problems;
        }

        if (!performSwitch)
        {
            Console.WriteLine();
            Console.WriteLine("    アクセサリーモードの端末はまだありません。");
            Console.WriteLine("    `vmonitor-doctor aoa switch` で実際に切り替えを試せます。");
            Console.WriteLine("    （切り替えると adb 接続は切れます。ケーブルを挿し直せば元に戻ります）");
            return problems;
        }

        // 3. 切り替えを実行する。
        var result = AoaDevice.SwitchToAccessoryMode();

        switch (result.Outcome)
        {
            case AoaDevice.SwitchOutcome.Switched:
                Report("アクセサリーモードへの切り替え", true, result.Detail);
                break;

            case AoaDevice.SwitchOutcome.AlreadyInAccessoryMode:
                Report("アクセサリーモードへの切り替え", true, result.Detail);
                break;

            default:
                Report("アクセサリーモードへの切り替え", false, result.Detail);
                return problems + 1;
        }

        // 4. 端末が別の VID/PID で戻ってくるのを待つ。
        Console.WriteLine("    端末の再接続を待っています...");

        AoaDevice? device = null;
        string     lastError = "タイムアウトしました";

        for (int i = 0; i < 20 && device == null; i++)   // 500ms × 20 = 最大 10 秒
        {
            Thread.Sleep(500);
            device = AoaDevice.OpenAccessory(out lastError);
        }

        using (device)
        {
            Report("アクセサリーデバイスの確保", device != null, device != null
                ? $"PID=0x{device.ProductId:X4}  IN=0x{device.InEndpoint:X2} OUT=0x{device.OutEndpoint:X2}"
                : lastError + "\n         （Windows がアクセサリー用のドライバを割り当てられていない可能性があります。" +
                  "デバイスマネージャーで不明なデバイスが増えていないか確認してください）");

            if (device == null) problems++;
        }

        return problems;
    }

    /// <summary>
    /// AOA で実際にデータが往復するかを確かめる。
    /// スマホ側でアプリを接続状態にしてから実行すること。
    /// </summary>
    public static async Task<int> CheckAoaEchoAsync(int seconds)
    {
        Section($"USB 直結の疎通確認 ({seconds} 秒)");

        var transport = new AoaTransport();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            await transport.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0),
                                         CancellationToken.None);

            Report("接続", true, transport.ConnectionDetail);

            // 受信を先に張ってから送る
            int received = 0;

            var reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var (channel, data) in transport.ReceiveAsync(cts.Token))
                    {
                        received++;
                        Console.WriteLine($"    受信: {channel} {data.Length} バイト");
                    }
                }
                catch (OperationCanceledException) { }
            });

            // 制御チャンネルに小さなフレームを流し続ける。
            // スマホ側が受け取れていれば、アプリのログか画面に現れる。
            int sent = 0;

            while (!cts.IsCancellationRequested)
            {
                var payload = System.Text.Encoding.UTF8.GetBytes($"ping {sent}");

                try
                {
                    await transport.SendAsync(payload, ChannelId.Control, CancellationToken.None);
                    sent++;
                }
                catch (Exception ex)
                {
                    Report("送信", false, $"{ex.GetType().Name}: {ex.Message}");
                    break;
                }

                try { await Task.Delay(500, cts.Token); }
                catch (OperationCanceledException) { break; }
            }

            await reader;

            Report("送信", sent > 0, $"{sent} 件送信しました。");
            Report("受信", received > 0, received > 0
                ? $"{received} 件受信しました。"
                : "スマホから何も返ってきませんでした。アプリが接続状態か確認してください。");

            return received > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Report("USB 直結", false, $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    /// <summary>
    /// 取り込んだフレームを BMP として書き出す（目視確認用）。
    /// </summary>
    /// <remarks>
    /// 「フレームが取れた」だけでは中身が正しいか分からない。
    /// 実際に何が映っていたかを見られるようにしておく。
    /// </remarks>
    private static void SaveBitmap(VMonitor.Core.Models.VideoFrame frame, string fileName)
    {
        try
        {
            int width  = frame.Resolution.Width;
            int height = frame.Resolution.Height;
            int stride = width * 4;

            const int FileHeaderSize = 14;
            const int InfoHeaderSize = 40;

            int pixelBytes = stride * height;
            int fileSize   = FileHeaderSize + InfoHeaderSize + pixelBytes;

            string path = Path.Combine(Path.GetTempPath(), fileName);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            // ファイルヘッダー
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write(0);                                  // 予約
            writer.Write(FileHeaderSize + InfoHeaderSize);    // 画素データの開始位置

            // 情報ヘッダー
            writer.Write(InfoHeaderSize);
            writer.Write(width);
            writer.Write(-height);      // 負の高さ = 上から下へ並んでいる
            writer.Write((short)1);     // プレーン数
            writer.Write((short)32);    // 1 画素あたりのビット数
            writer.Write(0);            // 無圧縮
            writer.Write(pixelBytes);
            writer.Write(0);            // 水平解像度
            writer.Write(0);            // 垂直解像度
            writer.Write(0);            // 使用色数
            writer.Write(0);            // 重要色数

            var data = frame.Data.Span;
            int copy = Math.Min(pixelBytes, data.Length);

            writer.Write(data[..copy]);

            // フレームが足りない場合の埋め（通常は起きない）
            for (int i = copy; i < pixelBytes; i++)
                writer.Write((byte)0);

            Console.WriteLine($"         画面を書き出しました: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"         画面の書き出しに失敗しました: {ex.Message}");
        }
    }

    // ── 表示ヘルパー ─────────────────────────────────────────────────────

    private static void Section(string title)
    {
        Console.WriteLine($"── {title} ──");
    }

    private static void Report(string label, bool ok, string? detail)
    {
        var mark = ok ? "OK  " : "NG  ";
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"  [{mark}] ");
        Console.ResetColor();
        Console.WriteLine(label);

        if (!string.IsNullOrEmpty(detail))
            Console.WriteLine($"         {detail}");
    }
}
