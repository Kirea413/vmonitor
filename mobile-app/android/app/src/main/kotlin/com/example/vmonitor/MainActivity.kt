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

    override fun onDestroy() {
        // 消し忘れない。次に開いたとき点きっぱなしになると困る。
        setKeepAwake(false)
        super.onDestroy()
    }

    companion object {
        const val SCREEN_CHANNEL = "vmonitor/screen"
    }
}
