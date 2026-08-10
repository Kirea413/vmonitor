import Flutter
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate {
    override func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
    ) -> Bool {
        GeneratedPluginRegistrant.register(with: self)
        RendererPlugin.register(with: registrar(forPlugin: "RendererPlugin")!)
        registerScreenChannel()
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
