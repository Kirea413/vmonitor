package com.example.vmonitor

import android.view.WindowManager
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        flutterEngine.plugins.add(RendererPlugin())
        flutterEngine.plugins.add(AoaPlugin())

        // 映像は Flutter のテクスチャを経由せず、ネイティブのビューへ直接描く。
        // 経由すると表示までに 235〜278ms かかることを実測している
        // （VideoPlatformView のコメント参照）。
        flutterEngine.platformViewsController.registry.registerViewFactory(
            VideoPlatformViewFactory.VIEW_TYPE,
            VideoPlatformViewFactory()
        )

        // 画面を消させないための入口。
        //
        // 2 枚目のモニターとして使っている最中に、スマホ側の都合で
        // 画面が消えるとモニターとして成立しない。触っていなくても
        // 消えないようにする必要がある。
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, SCREEN_CHANNEL)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "keepAwake" -> {
                        val keep = call.arguments as? Boolean ?: false
                        setKeepAwake(keep)
                        result.success(null)
                    }
                    "setBrightness" -> {
                        // null なら端末の設定に戻す
                        setBrightness(call.arguments as? Double)
                        result.success(null)
                    }
                    else -> result.notImplemented()
                }
            }
    }

    /**
     * 画面を点けたままにするかどうか。
     *
     * ウィンドウのフラグで行う。端末の電源管理に任せるので、
     * WakeLock のように解除し忘れて電池を食い続ける心配がない。
     * アプリが背面に回れば自動で効かなくなる。
     */
    private fun setKeepAwake(keep: Boolean) {
        runOnUiThread {
            if (keep) {
                window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            } else {
                window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            }
        }
    }

    /**
     * 画面の明るさをこのウィンドウの間だけ固定する。
     *
     * FLAG_KEEP_SCREEN_ON は画面を消させないだけで、明るさまでは
     * 抑えてくれない。触らない時間が続くと端末側が段階的に暗くする
     * （自動調整や「見ていないとき」の減光）。モニターとして置いて
     * 眺めているだけの使い方では、まさにそれが起きる。
     *
     * ウィンドウ属性の screenBrightness を指定すると、端末の設定より
     * こちらが優先される。効くのはこのウィンドウが前面にある間だけで、
     * 端末全体の明るさ設定は書き換えない（WRITE_SETTINGS も要らない）。
     *
     * @param level 0.0〜1.0。null なら端末の設定に従う。
     */
    private fun setBrightness(level: Double?) {
        runOnUiThread {
            val attributes = window.attributes

            // 0 は「画面が消えたのと変わらない」暗さになる端末があるため、
            // 下限を少し上げておく。
            attributes.screenBrightness = level?.toFloat()?.coerceIn(0.05f, 1.0f)
                ?: WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE

            window.attributes = attributes
        }
    }

    override fun onDestroy() {
        // 消し忘れない。次に開いたとき点きっぱなしになると困る。
        setKeepAwake(false)
        setBrightness(null)
        super.onDestroy()
    }

    companion object {
        const val SCREEN_CHANNEL = "vmonitor/screen"
    }
}
