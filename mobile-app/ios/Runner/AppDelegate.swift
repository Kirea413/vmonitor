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
