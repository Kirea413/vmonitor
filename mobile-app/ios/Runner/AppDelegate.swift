import Flutter
import UIKit

/// 画面の端のスワイプを、一度目では効かせないようにした FlutterViewController。
///
/// 画面いっぱいが PC の入力面なので、下端を触る操作は日常的に起きる。
/// 既定のままだと、そのたびにホーム画面へ戻ってしまう。
///
/// `preferredScreenEdgesDeferringSystemGestures` に下端を指定すると、
/// 一度目のスワイプではホームインジケータが出るだけで、二度目で初めて
/// ホームへ戻る。誤操作は防ぎつつ、抜け道は塞がない。
///
/// 新しいファイルにすると Xcode プロジェクトへの登録も手で書くことになり、
/// 実際それで一度ビルドを落としている。登録済みのこのファイルに置く。
class VMonitorViewController: FlutterViewController {

    override var preferredScreenEdgesDeferringSystemGestures: UIRectEdge {
        // 下端 = ホームインジケータ。上端 = コントロールセンター / 通知。
        // どちらも映像の上を触っているだけで出てしまう。
        return [.bottom, .top]
    }

    /// 操作が無い間はホームインジケータを消す。
    ///
    /// 白い横棒が映像に重なったままになるのを避ける。触れば戻ってくる。
    override var prefersHomeIndicatorAutoHidden: Bool {
        return true
    }

    override var prefersStatusBarHidden: Bool {
        return true
    }
}

/// Bonjour で PC を探す。
///
/// Dart 側の multicast_dns は 224.0.0.251 へ生の UDP を投げる作りで、
/// iOS ではこれに `com.apple.developer.networking.multicast` の
/// entitlement が要る。Apple へ個別に申請して得るもので、サイドロードでは
/// そもそも使えない。結果、iOS だけ PC を 1 台も見つけられなかった。
///
/// NetServiceBrowser のような高レベルの API は、この entitlement なしで
/// 使える（内部のマルチキャストは OS が面倒を見る）。Info.plist の
/// NSBonjourServices とローカルネットワークの許可だけで足りる。
class BonjourBrowser: NSObject, NetServiceBrowserDelegate, NetServiceDelegate {

    private let browser = NetServiceBrowser()

    /// 解決待ちの間、強い参照を保つ。手放すと解決前に消える。
    private var resolving: [NetService] = []

    private var found: [[String: Any]] = []
    private var completion: (([[String: Any]]) -> Void)?
    private var deadline: Timer?

    func search(type: String, timeout: TimeInterval,
                completion: @escaping ([[String: Any]]) -> Void) {
        self.completion = completion

        browser.delegate = self
        browser.searchForServices(ofType: type, inDomain: "local.")

        deadline = Timer.scheduledTimer(withTimeInterval: timeout, repeats: false) { [weak self] _ in
            self?.finish()
        }
    }

    private func finish() {
        guard let completion else { return }
        self.completion = nil

        deadline?.invalidate()
        deadline = nil

        browser.stop()
        resolving.removeAll()

        completion(found)
    }

    // MARK: - NetServiceBrowserDelegate

    func netServiceBrowser(_ browser: NetServiceBrowser,
                           didFind service: NetService,
                           moreComing: Bool) {
        service.delegate = self
        resolving.append(service)

        // ここで名前からアドレスまで引く。引かないとポートも分からない。
        service.resolve(withTimeout: 5.0)
    }

    // MARK: - NetServiceDelegate

    func netServiceDidResolveAddress(_ service: NetService) {
        guard let addresses = service.addresses else { return }

        // IPv4 だけ拾う。PC 側の待ち受けも IPv4 で、
        // IPv6 のアドレスを渡すと繋ぎに行って失敗する。
        for data in addresses {
            guard let ip = Self.ipv4(from: data) else { continue }

            found.append([
                "name": service.name,
                "host": service.hostName ?? ip,
                "port": service.port,
                "ip":   ip,
            ])
            break
        }
    }

    func netService(_ service: NetService,
                    didNotResolve errorDict: [String: NSNumber]) {
        // 引けなかった 1 台は諦める。他の相手の探索は続ける。
    }

    /// sockaddr の入った Data から IPv4 アドレスの文字列を取り出す。
    private static func ipv4(from data: Data) -> String? {
        return data.withUnsafeBytes { raw -> String? in
            guard let base = raw.baseAddress else { return nil }

            let addr = base.assumingMemoryBound(to: sockaddr.self)
            guard addr.pointee.sa_family == UInt8(AF_INET) else { return nil }

            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))

            let ok = getnameinfo(addr, socklen_t(data.count),
                                 &host, socklen_t(host.count),
                                 nil, 0, NI_NUMERICHOST) == 0

            return ok ? String(cString: host) : nil
        }
    }
}

@main
@objc class AppDelegate: FlutterAppDelegate {
    override func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
    ) -> Bool {
        GeneratedPluginRegistrant.register(with: self)
        RendererPlugin.register(with: registrar(forPlugin: "RendererPlugin")!)
        registerScreenChannel()
        registerDiscoveryChannel()
        return super.application(application, didFinishLaunchingWithOptions: launchOptions)
    }

    // MARK: - 画面を消させない / 暗くしない
    //
    // Android 側 (MainActivity.kt) の vmonitor/screen と対になる。
    // これが無いと、映している最中に 30 秒ほどで自動ロックがかかり、
    // 2 枚目のモニターとして成立しない。
    //
    // 新しいファイルを足すと Xcode プロジェクトへの登録も要るので、
    // 既に登録されているこのファイルの中に置く。

    /// 明るさを固定する前の値。戻すために覚えておく。
    private var savedBrightness: CGFloat?

    /// 探索中のブラウザ。終わるまで手放さない。
    private var browser: BonjourBrowser?

    /// Bonjour での探索をひとつだけ受け付ける口。
    private func registerDiscoveryChannel() {
        guard let messenger = registrar(forPlugin: "DiscoveryChannel")?.messenger() else { return }

        let channel = FlutterMethodChannel(
            name: "vmonitor/discovery",
            binaryMessenger: messenger
        )

        channel.setMethodCallHandler { [weak self] call, result in
            guard call.method == "discover" else {
                result(FlutterMethodNotImplemented)
                return
            }

            let args = call.arguments as? [String: Any]
            let type = (args?["type"] as? String) ?? "_vmonitor._tcp."
            let ms   = (args?["timeoutMs"] as? Int) ?? 5000

            let browser = BonjourBrowser()
            self?.browser = browser

            browser.search(type: type, timeout: Double(ms) / 1000.0) { [weak self] services in
                self?.browser = nil
                result(services)
            }
        }
    }

    private func registerScreenChannel() {
        guard let messenger = registrar(forPlugin: "ScreenChannel")?.messenger() else { return }

        let channel = FlutterMethodChannel(
            name: "vmonitor/screen",
            binaryMessenger: messenger
        )

        channel.setMethodCallHandler { [weak self] call, result in
            switch call.method {
            case "keepAwake":
                // 自動ロックを止める。アプリが背面に回れば iOS 側で無効になる。
                UIApplication.shared.isIdleTimerDisabled = (call.arguments as? Bool) ?? false
                result(nil)

            case "setBrightness":
                self?.setBrightness(call.arguments as? Double)
                result(nil)

            case "deviceName":
                // PC の一覧に出す呼び名。
                //
                // 名乗らないと、複数台あるときにどれが自分のものか
                // 分からない。Android では機種名を送っている。
                //
                // iOS 16 以降、UIDevice.name は権限が無いと機種名
                // ("iPhone" など) を返す。それでも無いよりはよい。
                result(UIDevice.current.name)

            default:
                result(FlutterMethodNotImplemented)
            }
        }
    }

    /// 画面の明るさを固定する。nil なら元の明るさへ戻す。
    ///
    /// iOS の `UIScreen.brightness` は端末全体の設定を書き換える。
    /// Android のようにウィンドウ単位では効かないので、変える前の値を
    /// 覚えておいて、やめるときに必ず戻す。
    private func setBrightness(_ level: Double?) {
        guard let level else {
            if let saved = savedBrightness {
                UIScreen.main.brightness = saved
                savedBrightness = nil
            }
            return
        }

        if savedBrightness == nil {
            savedBrightness = UIScreen.main.brightness
        }

        UIScreen.main.brightness = CGFloat(min(max(level, 0.05), 1.0))
    }

    override func applicationWillTerminate(_ application: UIApplication) {
        // 明るさを変えたまま終わると、端末の設定が変わったままになる
        setBrightness(nil)
        UIApplication.shared.isIdleTimerDisabled = false
        super.applicationWillTerminate(application)
    }
}
