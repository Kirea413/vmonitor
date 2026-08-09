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
        return super.application(application, didFinishLaunchingWithOptions: launchOptions)
    }
}
