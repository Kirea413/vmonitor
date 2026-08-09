package com.example.vmonitor

import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine

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
    }
}
