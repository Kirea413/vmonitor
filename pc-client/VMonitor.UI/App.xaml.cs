using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using VMonitor.Driver;
using VMonitor.Session;
using VMonitor.Session.Transport;
using VMonitor.UI.ViewModels;

namespace VMonitor.UI;

/// <summary>
/// vmonitor PC クライアントのアプリケーションエントリーポイント。
/// コンポーネントを DI 的に組み上げてメインウィンドウを表示する。
/// </summary>
public partial class App : Application
{
    private ConnectionServer? _server;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --- コンポーネントの組み立て ---

        // 仮想ディスプレイドライバ（シミュレーション実装）
        var adapter = new IddCxAdapter();
        var vdd = new VirtualDisplayDriver(adapter);

        // 認証マネージャー（許可ダイアログは後でViewModelから提供）
        AuthManager? authManager = null;
        authManager = new AuthManager(device =>
            TrustedDevicesViewModel.ShowAuthorizationDialogAsync(device));

        // セッションマネージャー（Wi-Fi トランスポートは接続ごとに生成するためここでは null）
        // ConnectionServer が接続を受け付けた際に SessionManager を使う
        var logger = new VMonitorLogger();

        // ViewModel
        var sessionManagerAdapter = new SessionManagerAdapter(vdd, authManager, logger);
        var connectionVm = new ConnectionViewModel(sessionManagerAdapter);

        // 設定（%APPDATA%\vmonitor\settings.json）
        var settingsManager = new SettingsManager();
        var settingsVm      = new ErrorLogViewModel(settingsManager);

        // 接続サーバー起動（バックグラウンドで接続待ち）
        _server = new ConnectionServer(connectionVm, vdd, authManager, logger);

        // USB は PC 側から掴みにいく方式なので、繋ぐ・切るを PC からも操作できるようにする
        connectionVm.Usb = new UsbConnectionViewModel(_server);

        // Wi-Fi は本来スマホから繋いでくるが、PC の前にいるときは
        // こちらから繋ぎにいけた方が早い。その向きの操作。
        connectionVm.Device = new DeviceConnectionViewModel(_server);

        // 前回の取り残しを片付ける。
        //
        // セッションの終了時にはモニターを外しているが、アプリが強制終了
        // された場合はその後始末が走らない。ドライバ側は繋いだままなので、
        // 次に起動したとき「繋いでいないのにディスプレイが 1 枚多い」状態が
        // 残る。起動時に必ず外しておく。
        _ = Task.Run(DisconnectLeftoverDisplay);

        // 保存済みのディスプレイ設定を反映してから待ち受けを始める。
        // 既定は「拡張ディスプレイのみ」なので、読めなかった場合もそれで動く。
        _ = LoadDisplaySettingsAsync(settingsManager, _server);

        // 設定画面で保存したら、次のセッションから効くようにする
        settingsVm.DisplaySettingsChanged += (_, settings) => _server?.UpdateDisplaySettings(settings);

        // 起動時に一度だけ更新を確認する。
        //
        // 新版が無いときは黙っている。毎回「最新です」と出ても邪魔なだけ。
        // 未認証の GitHub API は回数に限りがあるので、繰り返し見にいかない。
        _ = settingsVm.Update.CheckAsync(announceNoUpdate: false);

        // 待ち受けは UI スレッドから切り離して始める。
        //
        // ここで素の `_ = _server.StartAsync()` にすると、以降の await が
        // すべて UI スレッドに戻ってくる（WPF の同期コンテキスト）。
        // USB の列挙も仮想ディスプレイの用意も同期処理なので、そのまま
        // 画面を止めてしまう。
        _ = Task.Run(() => _server.StartAsync());

        // ファイアウォールルールを自動追加する（管理者権限で実行済みの場合）。
        //
        // netsh の起動と終了待ちで最大 3 秒かかる。起動直後のいちばん
        // 見られている場面なので、UI スレッドではやらない。
        _ = Task.Run(AddFirewallRule);

        // USB デバイス監視を開始する
        var usbMonitor = new UsbDeviceMonitor();
        var usbListener = new UsbConnectionListener(usbMonitor, sessionManagerAdapter);
        usbMonitor.StartMonitoring();

        // ここで adb を呼んではいけない。
        //
        // USB 直結 (AOA) は端末のインターフェースを直接掴む方式で、
        // adb サーバーが起動しているとそちらが USB を占有してしまい、
        // こちらからは開けなくなる。起動のたびに adb を立ち上げていると
        // USB 直結が常に失敗する。
        //
        // ADB 経由の接続を使う場合は、利用者が自分で次を実行する:
        //   adb reverse tcp:7979 tcp:7979
        // （forward ではなく reverse。必要なのは端末から PC への向き）

        // メインウィンドウ表示
        var mainWindow = new MainWindow(connectionVm, settingsVm);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _server?.Stop();
        base.OnExit(e);
    }

    /// <summary>
    /// 前回残ってしまった仮想ディスプレイを取り外す。
    /// </summary>
    /// <remarks>
    /// ドライバが入っていない環境では何も起きない。
    /// 起動を妨げないよう、失敗しても黙って進む。
    /// </remarks>
    private static void DisconnectLeftoverDisplay()
    {
        try
        {
            using var display = VirtualDisplayControl.TryOpen();

            if (display is null) return;
            if (!display.GetState().Connected) return;

            display.Disconnect();
        }
        catch
        {
            // 掃除できなくても、接続そのものには影響しない
        }
    }

    /// <summary>
    /// 保存済みのディスプレイ設定を読み込んで接続サーバーに反映する。
    /// </summary>
    /// <remarks>
    /// 読み込みに失敗しても既定（拡張ディスプレイのみ）で動くので、
    /// 起動そのものは止めない。
    /// </remarks>
    private static async Task LoadDisplaySettingsAsync(SettingsManager settings, ConnectionServer server)
    {
        try
        {
            var loaded = await settings.LoadAsync();
            server.UpdateDisplaySettings(loaded.DisplayDefaults);
        }
        catch
        {
            // 既定のまま進む
        }
    }

    /// <summary>
    /// Windows Firewall にポート 7979 の受信ルールを追加する。
    /// 既に存在する場合はスキップする。
    /// </summary>
    private static void AddFirewallRule()
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall add rule name=\"vmonitor\" dir=in action=allow protocol=tcp localport=7979",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(3000);
        }
        catch { /* ファイアウォール設定失敗は無視して続行 */ }
    }
}
