package com.kirea.vmonitor

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugin.common.MethodChannel.MethodCallHandler
import io.flutter.view.TextureRegistry
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

/**
 * vmonitor レンダラープラグイン (Android)
 *
 * Flutter MethodChannel "vmonitor/renderer" を通じて H.264 フレームを受け取り、
 * MediaCodec でハードウェアデコードして Flutter Texture に描画する。
 *
 * MethodChannel プロトコル:
 *   initialize  -> textureId (Long)
 *   pushFrame   <- {textureId: Long, data: ByteArray}
 *   dispose     <- {textureId: Long}
 *
 * EventChannel "vmonitor/renderer/stats":
 *   {fps: Double, decodeLatencyMs: Int}
 */
class RendererPlugin : FlutterPlugin, MethodCallHandler {

    private lateinit var methodChannel: MethodChannel
    private lateinit var statsEventChannel: EventChannel
    private lateinit var textureRegistry: TextureRegistry
    private var statsEventSink: EventChannel.EventSink? = null

    /** textureId -> RendererSession */
    private val sessions = ConcurrentHashMap<Long, RendererSession>()

    private val statsExecutor = Executors.newSingleThreadExecutor()

    // ── FlutterPlugin ────────────────────────────────────────────────

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        textureRegistry = binding.textureRegistry

        methodChannel = MethodChannel(binding.binaryMessenger, CHANNEL_NAME)
        methodChannel.setMethodCallHandler(this)

        statsEventChannel = EventChannel(binding.binaryMessenger, STATS_CHANNEL)
        statsEventChannel.setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                statsEventSink = events
            }
            override fun onCancel(arguments: Any?) {
                statsEventSink = null
            }
        })
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        methodChannel.setMethodCallHandler(null)
        VideoFrameRouter.setSink(null)
        sessions.values.forEach { it.release() }
        sessions.clear()
        statsExecutor.shutdown()
    }

    // ── MethodCallHandler ────────────────────────────────────────────

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "initialize" -> handleInitialize(call, result)
            "pushFrame"  -> handlePushFrame(call, result)
            "dispose"    -> handleDispose(call, result)
            else         -> result.notImplemented()
        }
    }

    /**
     * デコーダーを用意する。
     *
     * 引数に `viewId` があれば、そのネイティブビューへ直接描く。
     * 無ければ従来どおり Flutter のテクスチャへ描く（テスト用の控え）。
     * ネイティブビューを使う理由は [VideoPlatformView] のコメントを参照。
     */
    private fun handleInitialize(call: MethodCall, result: MethodChannel.Result) {
        try {
            val viewId = (call.argument<Any>("viewId") as? Number)?.toInt()

            if (viewId != null) {
                // 描画先が出来るのを待つ。
                //
                // ビューが出来たこと (onPlatformViewCreated) と、描画先が
                // 使えるようになったこと (surfaceCreated) は別のできごとで、
                // 後者はあとから来る。Dart は前者の時点でここを呼ぶので、
                // 間に合っていないのが普通。
                //
                // 以前はその場で失敗を返していた。やり直す仕組みが無いため、
                // タイミング次第で映像が最後まで出ないままになっていた。
                // 待ちっぱなしにはしない。来ないときは、来ないと伝える。
                // 黙って止まると、画面はぐるぐる回ったまま何も起きない。
                var settled = false

                val timeout = Runnable {
                    if (settled) return@Runnable
                    settled = true

                    VideoSurfaceRegistry.cancelWaiters(viewId)
                    result.error("SURFACE_NOT_READY",
                                 "映像ビュー ($viewId) の描画先が用意されませんでした。", null)
                }

                val handler = android.os.Handler(android.os.Looper.getMainLooper())
                handler.postDelayed(timeout, SURFACE_WAIT_TIMEOUT_MS)

                VideoSurfaceRegistry.await(viewId) { nativeSurface ->
                    if (settled) return@await
                    settled = true
                    handler.removeCallbacks(timeout)

                    val id = viewId.toLong()
                    val nativeSession = RendererSession(id, nativeSurface, null) { fps, latencyMs ->
                        emitStats(fps, latencyMs)
                    }

                    sessions[id] = nativeSession
                    VideoFrameRouter.setSink { data -> nativeSession.pushFrame(data) }

                    result.success(id)
                }
                return
            }

            val entry = textureRegistry.createSurfaceTexture()
            val textureId = entry.id()
            val surfaceTexture = entry.surfaceTexture()
            val surface = Surface(surfaceTexture)
            val session = RendererSession(textureId, surface, entry) { fps, latencyMs ->
                emitStats(fps, latencyMs)
            }

            // 表示へ回した絵が実際にテクスチャへ届いた時刻を拾う。
            // ここまでの各区間は実測済みなので、残りの遅れを切り分けるために使う。
            surfaceTexture.setOnFrameAvailableListener { session.onTextureFrameAvailable() }
            sessions[textureId] = session

            // USB から届いた映像を Dart を通さずここへ直接流してもらう。
            // 経路を短くするだけでなく、詰まったときに読み出しが止まって
            // 送り手まで背圧が伝わるようになる（VideoFrameRouter 参照）。
            VideoFrameRouter.setSink { data -> session.pushFrame(data) }

            result.success(textureId)
        } catch (e: Exception) {
            result.error("INIT_ERROR", e.message, null)
        }
    }

    private fun handlePushFrame(call: MethodCall, result: MethodChannel.Result) {
        val textureId = (call.argument<Any>("textureId") as? Number)?.toLong()
            ?: return result.error("INVALID_ARG", "textureId missing", null)
        val data = call.argument<ByteArray>("data")
            ?: return result.error("INVALID_ARG", "data missing", null)

        val session = sessions[textureId]
            ?: return result.error("NOT_FOUND", "textureId $textureId not found", null)

        session.pushFrame(data)
        result.success(null)
    }

    private fun handleDispose(call: MethodCall, result: MethodChannel.Result) {
        val textureId = (call.argument<Any>("textureId") as? Number)?.toLong()
            ?: return result.error("INVALID_ARG", "textureId missing", null)

        VideoFrameRouter.setSink(null)

        sessions.remove(textureId)?.release()
        result.success(null)
    }

    private fun emitStats(fps: Double, latencyMs: Int) {
        statsExecutor.submit {
            statsEventSink?.success(mapOf("fps" to fps, "decodeLatencyMs" to latencyMs))
        }
    }

    companion object {
        const val CHANNEL_NAME  = "vmonitor/renderer"
        const val STATS_CHANNEL = "vmonitor/renderer/stats"

        /**
         * 描画先が用意されるのを待つ上限（ミリ秒）。
         *
         * 普段は数十ミリ秒で来る。長めに取っているのは、
         * 待ち足りずに諦めて映像が出ないほうが困るため。
         */
        const val SURFACE_WAIT_TIMEOUT_MS = 5_000L
    }
}

/**
 * 1 テクスチャに対応するデコードセッション。
 * MediaCodec を同期モードで動かし、
 * デコード済みフレームを Surface (SurfaceTexture) に直接描画する。
 */
private class RendererSession(
    private val textureId: Long,
    private val surface: Surface,
    /**
     * Flutter のテクスチャを使う場合の登録。
     * ネイティブビューへ直接描くときは null で、描画先はビューの持ち物になる。
     */
    private val textureEntry: TextureRegistry.SurfaceTextureEntry?,
    private val onStats: (fps: Double, latencyMs: Int) -> Unit
) {
    private companion object {
        const val TAG = "RendererPlugin"

        /**
         * 入力バッファが空くのを待つ上限（マイクロ秒）。
         *
         * 短くして諦めるとフレームを落とすことになり、H.264 では
         * 次のキーフレームまで絵が崩れる。待って送り手を減速させるほうがよい。
         */
        const val INPUT_TIMEOUT_US = 500_000L
    }

    private var codec: MediaCodec? = null
    private var isConfigured = false
    private val pendingData = mutableListOf<ByteArray>()

    // デコードした絵が実際にテクスチャへ乗るまでの時間。
    //
    // releaseOutputBuffer を呼んでから、SurfaceTexture に新しい絵が
    // 届いたと通知されるまでを測る。ここが長いと、デコードが速くても
    // 画面に出るのは遅れる。PC 側と転送はすべて実測済みで合計 100ms に
    // 満たないため、体感との差はこの区間に出ているはず。
    private var lastRenderAtNs = 0L
    private val textureLatencies = ArrayDeque<Long>()

    // デコーダーに入れた枚数と、表示に回した枚数。
    // 差がデコーダー内部に溜まっている枚数で、そのまま遅れになる。
    private var inputsQueued   = 0L
    private var outputsRendered = 0L

    // FPS・レイテンシ計測
    private val frameCount = AtomicLong(0)
    private var windowStart = System.currentTimeMillis()
    private val recentLatencies = ArrayDeque<Long>(30)

    /**
     * H.264 NAL ユニットをデコーダーへ送る。
     * Annex B フォーマット (0x00 0x00 0x00 0x01 ...) を期待する。
     */
    /**
     * H.264 のフレームをデコーダーへ渡し、出来た絵を表示する。
     *
     * USB 接続では、USB の読み出しスレッドからそのまま呼ばれる。
     * ここで待たされることが、そのまま送り手への背圧になる
     * （[VideoFrameRouter] 参照）。したがって「詰まったら捨てる」のではなく
     * 「詰まったら待つ」のが正しい。H.264 は途中のフレームを落とすと
     * 次のキーフレームまで絵が崩れるため、捨てる選択肢は取れない。
     */
    @Synchronized
    fun pushFrame(data: ByteArray) {
        val decodeStart = System.currentTimeMillis()

        // SPS NAL ユニット (0x67) を含むフレームでコーデックを初期化
        if (!isConfigured) {
            if (containsSps(data)) {
                initCodec(data)
            } else {
                // SPS が来るまでデータを保留
                return
            }
        }

        val c = codec ?: return

        try {
            // 先に出来上がっている絵を掃き出す。
            // 入力バッファは出力を返して初めて空くので、
            // 入れる前に片付けておかないと無駄に待つことになる。
            drainOutput(c)

            // 入力バッファを取る。
            //
            // 空くまで待つ。ここで諦めて捨てると、H.264 の参照が途切れて
            // 次のキーフレームまで絵が壊れる。待てば送り手が減速するだけで済む。
            val inputIndex = c.dequeueInputBuffer(INPUT_TIMEOUT_US)

            if (inputIndex >= 0) {
                val inputBuffer: ByteBuffer = c.getInputBuffer(inputIndex)!!
                inputBuffer.clear()

                // バッファサイズを超えないようにクリップ
                val writeSize = minOf(data.size, inputBuffer.capacity())
                inputBuffer.put(data, 0, writeSize)

                c.queueInputBuffer(
                    inputIndex,
                    0,
                    writeSize,
                    System.nanoTime() / 1000L,
                    0
                )
                inputsQueued++
            } else {
                android.util.Log.w(TAG, "入力バッファが空かず、フレームを 1 枚落としました")
            }

            // 入れた結果できた絵を出す
            drainOutput(c)
        } catch (e: Exception) {
            // デコードエラーは無視して継続
            android.util.Log.w(TAG, "pushFrame error: ${e.message}")
        }

        val latencyMs = System.currentTimeMillis() - decodeStart
        updateStats(latencyMs)
    }

    /**
     * 出来上がった絵をすべて表示に回す。
     *
     * 溜まっていたぶんは最後の 1 枚しか見えないが、
     * 出力バッファを返さないとデコーダーが進めないので必ず全部返す。
     */
    private fun drainOutput(c: MediaCodec) {
        val bufferInfo = MediaCodec.BufferInfo()
        var outputIndex = c.dequeueOutputBuffer(bufferInfo, 0L)

        while (outputIndex >= 0) {
            c.releaseOutputBuffer(outputIndex, bufferInfo.size > 0)
            outputsRendered++
            lastRenderAtNs = System.nanoTime()
            outputIndex = c.dequeueOutputBuffer(bufferInfo, 0L)
        }
    }

    /**
     * テクスチャに新しい絵が届いたときに呼ぶ。
     * 表示へ回してから何ミリ秒かかったかを控える。
     */
    fun onTextureFrameAvailable() {
        val renderedAt = lastRenderAtNs
        if (renderedAt == 0L) return

        val elapsedMs = (System.nanoTime() - renderedAt) / 1_000_000

        synchronized(textureLatencies) {
            textureLatencies.addLast(elapsedMs)
            if (textureLatencies.size > 60) textureLatencies.removeFirst()
        }
    }

    @Synchronized
    fun release() {
        try {
            codec?.stop()
            codec?.release()
        } catch (_: Exception) {}
        codec = null

        // 描画先がネイティブビューのものなら、こちらで解放してはいけない。
        // ビューが自分で管理しており、外から閉じるとビューごと壊れる。
        if (textureEntry != null) {
            surface.release()
            textureEntry.release()
        }
    }

    /**
     * SPS NAL ユニット (NAL type 7 = 0x67) が含まれているか確認する。
     */
    private fun containsSps(data: ByteArray): Boolean {
        // Annex B: 00 00 00 01 67 ... (SPS)
        for (i in 0 until data.size - 4) {
            if (data[i] == 0x00.toByte() &&
                data[i+1] == 0x00.toByte() &&
                data[i+2] == 0x00.toByte() &&
                data[i+3] == 0x01.toByte()) {
                if (i + 4 < data.size) {
                    val nalType = data[i+4].toInt() and 0x1F
                    if (nalType == 7) return true // SPS
                }
            }
        }
        // 3バイトのスタートコード 00 00 01 67 も確認
        for (i in 0 until data.size - 3) {
            if (data[i] == 0x00.toByte() &&
                data[i+1] == 0x00.toByte() &&
                data[i+2] == 0x01.toByte()) {
                if (i + 3 < data.size) {
                    val nalType = data[i+3].toInt() and 0x1F
                    if (nalType == 7) return true
                }
            }
        }
        return false
    }

    /**
     * MediaCodec を初期化する。
     * SPS/PPS を含む最初のフレームデータを使って configure する。
     */
    private fun initCodec(firstData: ByteArray) {
        try {
            val mimeType = MediaFormat.MIMETYPE_VIDEO_AVC
            val format = MediaFormat.createVideoFormat(mimeType, 1920, 1080).apply {
                setInteger(
                    MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface
                )
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 2 * 1024 * 1024) // 2 MB

                // 低遅延で復号するよう指示する。
                //
                // 指定しないと、デコーダーは表示を滑らかにするために
                // 数フレームぶん溜めてから出す。放送や動画再生では正しいが、
                // 画面を見ながら操作する用途では、その溜めがそのまま遅れになる。
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                }

                // Annex B フォーマットをそのまま受け付ける
                // Android は自動的に SPS/PPS を解析する
            }

            codec = MediaCodec.createDecoderByType(mimeType).also { c ->
                c.configure(format, surface, null, 0)
                c.start()
            }
            isConfigured = true
            android.util.Log.i("RendererPlugin", "MediaCodec initialized with SPS frame")
        } catch (e: Exception) {
            android.util.Log.e("RendererPlugin", "initCodec failed: ${e.message}")
        }
    }

    private fun updateStats(latencyMs: Long) {
        recentLatencies.addLast(latencyMs)
        if (recentLatencies.size > 30) recentLatencies.removeFirst()
        val avgLatency = recentLatencies.average().toInt()

        frameCount.incrementAndGet()
        val now = System.currentTimeMillis()
        val elapsed = now - windowStart
        if (elapsed >= 1000L) {
            val fps = frameCount.getAndSet(0) * 1000.0 / elapsed
            windowStart = now
            onStats(fps, avgLatency)

            // デコーダーの中に何枚溜まっているかを出す。
            //
            // 入れた枚数と出た枚数の差が、そのままデコーダー内部の遅れになる。
            // 18fps で 8 枚なら 440ms。遅延の出どころを数字で確かめるために記録する。
            val inFlight = inputsQueued - outputsRendered

            val textureMs = synchronized(textureLatencies) {
                if (textureLatencies.isEmpty()) -1.0 else textureLatencies.average()
            }

            android.util.Log.i(
                TAG,
                "decode: in=$inputsQueued out=$outputsRendered inflight=$inFlight " +
                "fps=%.1f push=%dms texture=%.0fms".format(fps, avgLatency, textureMs)
            )
        }
    }
}
