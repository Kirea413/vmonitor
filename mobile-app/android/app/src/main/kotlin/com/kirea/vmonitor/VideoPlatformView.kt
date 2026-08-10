package com.kirea.vmonitor

import android.content.Context
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import io.flutter.plugin.common.StandardMessageCodec
import io.flutter.plugin.platform.PlatformView
import io.flutter.plugin.platform.PlatformViewFactory
import java.util.concurrent.ConcurrentHashMap

/**
 * 映像を出すためのネイティブビュー。
 *
 * ## なぜ Flutter のテクスチャを使わないか
 *
 * これまでは `MediaCodec → SurfaceTexture → Flutter の合成 → 画面` という
 * 経路で表示していた。Flutter は `Texture` ウィジェットの中身を自分の描画
 * サイクルに取り込んでから合成するため、そこで待ちが生じる。
 *
 * 実測では、デコード済みの絵を表示に回してからテクスチャへ届くまでに
 * 235〜278ms かかっていた。他の区間（取り込み 40ms・符号化 30〜78ms・
 * 転送 15ms・デコード 8ms）の合計より大きく、体感していた遅れの主因だった。
 *
 * ここでは `SurfaceView` を直接埋め込み、デコーダーが画面用のサーフェスへ
 * そのまま描くようにする。Flutter の合成を経由しないぶん、遅れが消える。
 *
 * ## iOS への移植について
 *
 * iOS では `AVSampleBufferDisplayLayer` を持つ `UIView` を `UiKitView` として
 * 埋め込むのが同じ役割になる。Dart 側から見た形（ビュー種別名・生成後に
 * 渡される ID・`initialize` で ID を伝える手順）は共通にしてあるので、
 * iOS 対応は Swift 側の実装を足すだけで済む。
 */
class VideoPlatformView(context: Context, private val viewId: Int) : PlatformView {

    private val surfaceView = SurfaceView(context)

    init {
        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                VideoSurfaceRegistry.register(viewId, holder.surface)
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                VideoSurfaceRegistry.register(viewId, holder.surface)
            }

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                VideoSurfaceRegistry.unregister(viewId)
            }
        })
    }

    override fun getView(): View = surfaceView

    override fun dispose() {
        VideoSurfaceRegistry.unregister(viewId)
        VideoSurfaceRegistry.cancelWaiters(viewId)
    }
}

/**
 * 生成済みの映像ビューとその描画先を対応づける。
 *
 * Dart はビューを作った直後に ID を受け取り、その ID を添えて
 * `initialize` を呼ぶ。デコーダー側はここから描画先を引く。
 */
object VideoSurfaceRegistry {

    private val surfaces = ConcurrentHashMap<Int, Surface>()

    /**
     * 描画先がまだ無いまま要求してきた相手。
     *
     * ビューが出来たことと、描画先が使えるようになったことは別のできごと。
     * `onPlatformViewCreated` は前者で呼ばれ、`surfaceCreated` は
     * そのあとに来る。Dart はビューが出来た時点で初期化を頼んでくるので、
     * 間に合っていないことが普通にある。
     *
     * ここで待てるようにしないと、その場で失敗して終わりになる。
     * 実機のログで実際にそうなっており、映像が出ないまま
     * PlatformException(SURFACE_NOT_READY) だけが残っていた。
     */
    private val waiters = ConcurrentHashMap<Int, MutableList<(Surface) -> Unit>>()

    fun register(viewId: Int, surface: Surface) {
        surfaces[viewId] = surface

        // 待っている相手に渡す。取り出してから呼ぶ（呼び出し先で
        // また登録されても取りこぼさないようにするため）。
        val pending = waiters.remove(viewId) ?: return
        for (callback in pending) callback(surface)
    }

    fun unregister(viewId: Int) {
        surfaces.remove(viewId)
    }

    fun get(viewId: Int): Surface? = surfaces[viewId]

    /**
     * 描画先を受け取る。既にあれば即座に、無ければ用意でき次第呼び戻す。
     *
     * 呼び戻しは `surfaceCreated` と同じスレッド（メインスレッド）で走る。
     */
    fun await(viewId: Int, onReady: (Surface) -> Unit) {
        val existing = surfaces[viewId]
        if (existing != null) {
            onReady(existing)
            return
        }

        waiters.getOrPut(viewId) { mutableListOf() }.add(onReady)
    }

    /** 待っている相手を諦めさせる（ビューが捨てられたときなど）。 */
    fun cancelWaiters(viewId: Int) {
        waiters.remove(viewId)
    }
}

/** Flutter から映像ビューを作るための入り口。 */
class VideoPlatformViewFactory : PlatformViewFactory(StandardMessageCodec.INSTANCE) {

    override fun create(context: Context, viewId: Int, args: Any?): PlatformView =
        VideoPlatformView(context, viewId)

    companion object {
        /** Dart 側と揃える必要がある。iOS でも同じ名前を使う。 */
        const val VIEW_TYPE = "vmonitor/video"
    }
}
