package com.example.vmonitor

import android.app.Activity
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.ParcelFileDescriptor
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.embedding.engine.plugins.activity.ActivityAware
import io.flutter.embedding.engine.plugins.activity.ActivityPluginBinding
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugin.common.PluginRegistry
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * AOA (Android Open Accessory) 受け口。
 *
 * PC 側がこの端末をアクセサリーモードへ切り替えると、端末は USB デバイス側、
 * PC は USB ホスト側になる。以降は adb も Wi-Fi も介さず、
 * バルクエンドポイント上でバイト列を直接やり取りする。
 *
 * Flutter からは UsbAccessory を触れないため、ここで面倒を見て
 * プラットフォームチャンネル越しに Dart へ渡す。
 *
 * MethodChannel "vmonitor/aoa":
 *   isSupported   -> Boolean    端末が AOA に対応しているか
 *   isAttached    -> Boolean    アクセサリーが繋がっているか
 *   connect       -> Map?       接続する。成功すると相手の情報を返す
 *   send          <- {channel: Int, data: ByteArray}
 *   disconnect    -> null
 *
 * EventChannel "vmonitor/aoa/frames":
 *   {channel: Int, data: ByteArray}  受信したフレーム
 *
 * EventChannel "vmonitor/aoa/state":
 *   {state: String, detail: String?}  attached / connected / detached / error
 */
class AoaPlugin : FlutterPlugin, MethodChannel.MethodCallHandler, ActivityAware,
    PluginRegistry.NewIntentListener {

    private lateinit var context: Context
    private lateinit var methodChannel: MethodChannel
    private lateinit var frameChannel: EventChannel
    private lateinit var stateChannel: EventChannel

    private var frameSink: EventChannel.EventSink? = null
    private var stateSink: EventChannel.EventSink? = null

    private var activity: Activity? = null

    private val mainHandler = Handler(Looper.getMainLooper())

    private var connection: AccessoryConnection? = null

    /** 権限ダイアログの結果を受け取る。openAccessory はこれを待ってから呼ぶ。 */
    private var permissionReceiver: BroadcastReceiver? = null

    // ── FlutterPlugin ────────────────────────────────────────────────

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        context = binding.applicationContext

        methodChannel = MethodChannel(binding.binaryMessenger, CHANNEL_NAME)
        methodChannel.setMethodCallHandler(this)

        frameChannel = EventChannel(binding.binaryMessenger, FRAME_CHANNEL)
        frameChannel.setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                frameSink = events
            }

            override fun onCancel(arguments: Any?) {
                frameSink = null
            }
        })

        stateChannel = EventChannel(binding.binaryMessenger, STATE_CHANNEL)
        stateChannel.setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                stateSink = events
            }

            override fun onCancel(arguments: Any?) {
                stateSink = null
            }
        })

        registerDetachReceiver()
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        methodChannel.setMethodCallHandler(null)
        frameChannel.setStreamHandler(null)
        stateChannel.setStreamHandler(null)

        closeConnection()
        unregisterReceivers()
    }

    // ── ActivityAware ────────────────────────────────────────────────
    //
    // アクセサリーが繋がるとインテントフィルター経由でアプリが起動する。
    // その起動インテントには相手の UsbAccessory が入っており、
    // このとき権限は既に与えられている（ダイアログが要らない）。

    override fun onAttachedToActivity(binding: ActivityPluginBinding) {
        activity = binding.activity
        binding.addOnNewIntentListener(this)
        handleAttachIntent(binding.activity.intent)
    }

    override fun onReattachedToActivityForConfigChanges(binding: ActivityPluginBinding) {
        onAttachedToActivity(binding)
    }

    override fun onDetachedFromActivity() {
        activity = null
    }

    override fun onDetachedFromActivityForConfigChanges() {
        activity = null
    }

    override fun onNewIntent(intent: Intent): Boolean {
        handleAttachIntent(intent)
        return false
    }

    private fun handleAttachIntent(intent: Intent?) {
        if (intent?.action != UsbManager.ACTION_USB_ACCESSORY_ATTACHED) return

        val accessory = getAccessoryExtra(intent) ?: return
        emitState("attached", describe(accessory))
    }

    // ── MethodCallHandler ────────────────────────────────────────────

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "isSupported"  -> result.success(isAccessorySupported())
            "isAttached"   -> result.success(findAccessory() != null)
            "deviceName"   -> result.success(readableDeviceName())
            "connect"      -> handleConnect(result)
            "send"         -> handleSend(call, result)
            "disconnect"   -> { closeConnection(); result.success(null) }
            else           -> result.notImplemented()
        }
    }

    /**
     * PC の一覧に出すための端末名。
     *
     * 「Android (USB)」では、複数台つないだときにどれがどれだか分からない。
     * 利用者が普段目にしている呼び名（Pixel 6a など）に寄せる。
     *
     * MODEL には既に製造元名が入っていることがある（"Galaxy S23" など）。
     * その場合に製造元を足すと "Samsung Galaxy S23" のように重ならないよう見る。
     */
    private fun readableDeviceName(): String {
        val model = android.os.Build.MODEL?.trim().orEmpty()
        val maker = android.os.Build.MANUFACTURER?.trim().orEmpty()

        if (model.isEmpty()) return maker.ifEmpty { "Android 端末" }
        if (maker.isEmpty()) return model

        if (model.startsWith(maker, ignoreCase = true)) return model

        return "$maker $model"
    }

    private fun isAccessorySupported(): Boolean =
        context.packageManager.hasSystemFeature("android.hardware.usb.accessory")

    private fun usbManager(): UsbManager =
        context.getSystemService(Context.USB_SERVICE) as UsbManager

    private fun findAccessory(): UsbAccessory? =
        usbManager().accessoryList?.firstOrNull()

    /**
     * アクセサリーを開いて読み書きを始める。
     *
     * 権限が無ければダイアログを出し、利用者の返事を待ってから開く。
     * ダイアログの結果は非同期に届くので、[result] はその時点で返す。
     */
    private fun handleConnect(result: MethodChannel.Result) {
        if (connection != null) {
            result.success(connection!!.info())
            return
        }

        val accessory = findAccessory()
        if (accessory == null) {
            result.error("NOT_ATTACHED", "アクセサリーが接続されていません。", null)
            return
        }

        val manager = usbManager()

        if (manager.hasPermission(accessory)) {
            openAccessory(accessory, result)
            return
        }

        requestPermission(accessory) { granted ->
            if (granted) {
                openAccessory(accessory, result)
            } else {
                emitState("error", "USB アクセサリーの利用が許可されませんでした。")
                result.error("PERMISSION_DENIED", "USB アクセサリーの利用が許可されませんでした。", null)
            }
        }
    }

    private fun requestPermission(accessory: UsbAccessory, onResult: (Boolean) -> Unit) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                if (intent.action != ACTION_USB_PERMISSION) return

                try {
                    context.unregisterReceiver(this)
                } catch (_: IllegalArgumentException) {
                }
                permissionReceiver = null

                val granted = intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)
                onResult(granted)
            }
        }

        permissionReceiver = receiver

        val filter = IntentFilter(ACTION_USB_PERMISSION)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            // Android 13 以降は公開範囲の明示が必須。
            // この通知は自分で作った PendingIntent 経由で自分にだけ届くため
            // 公開しない。
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }

        // Android 12 以降は可変・不変の指定が必須。
        // 許可結果は OS が extras に書き込んで返すため、可変でなければならない。
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            PendingIntent.FLAG_MUTABLE
        } else {
            0
        }

        val intent = Intent(ACTION_USB_PERMISSION).setPackage(context.packageName)
        val pending = PendingIntent.getBroadcast(context, 0, intent, flags)

        usbManager().requestPermission(accessory, pending)
    }

    private fun openAccessory(accessory: UsbAccessory, result: MethodChannel.Result) {
        val descriptor: ParcelFileDescriptor? = try {
            usbManager().openAccessory(accessory)
        } catch (e: Exception) {
            null
        }

        if (descriptor == null) {
            emitState("error", "アクセサリーを開けませんでした。")
            result.error("OPEN_FAILED", "アクセサリーを開けませんでした。", null)
            return
        }

        val conn = AccessoryConnection(
            accessory = accessory,
            descriptor = descriptor,
            onFrame = { channel, data -> emitFrame(channel, data) },
            onClosed = { detail ->
                connection = null
                emitState("detached", detail)
            }
        )

        connection = conn
        conn.start()

        emitState("connected", describe(accessory))
        result.success(conn.info())
    }

    private fun handleSend(call: MethodCall, result: MethodChannel.Result) {
        val conn = connection
        if (conn == null) {
            result.error("NOT_CONNECTED", "アクセサリーに接続していません。", null)
            return
        }

        val channel = (call.argument<Any>("channel") as? Number)?.toInt()
        val data = call.argument<ByteArray>("data")

        if (channel == null || data == null) {
            result.error("INVALID_ARG", "channel と data が必要です。", null)
            return
        }

        conn.send(channel, data)
        result.success(null)
    }

    private fun closeConnection() {
        connection?.close()
        connection = null
    }

    // ── 抜かれたことの検知 ────────────────────────────────────────────

    private var detachReceiver: BroadcastReceiver? = null

    private fun registerDetachReceiver() {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                if (intent.action != UsbManager.ACTION_USB_ACCESSORY_DETACHED) return
                closeConnection()
                emitState("detached", "ケーブルが抜かれました。")
            }
        }

        detachReceiver = receiver

        val filter = IntentFilter(UsbManager.ACTION_USB_ACCESSORY_DETACHED)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            // OS が発行するブロードキャストなので受け取りを許可する
            context.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }
    }

    private fun unregisterReceivers() {
        detachReceiver?.let {
            try { context.unregisterReceiver(it) } catch (_: IllegalArgumentException) {}
        }
        detachReceiver = null

        permissionReceiver?.let {
            try { context.unregisterReceiver(it) } catch (_: IllegalArgumentException) {}
        }
        permissionReceiver = null
    }

    // ── Dart への通知 ────────────────────────────────────────────────
    //
    // EventSink はメインスレッドからしか呼べない。
    // 読み出しは専用スレッドで回しているので、必ず載せ替える。

    private fun emitFrame(channel: Int, data: ByteArray) {
        mainHandler.post {
            frameSink?.success(mapOf("channel" to channel, "data" to data))
        }
    }

    private fun emitState(state: String, detail: String?) {
        mainHandler.post {
            stateSink?.success(mapOf("state" to state, "detail" to detail))
        }
    }

    private fun describe(accessory: UsbAccessory): String =
        "${accessory.manufacturer ?: "?"} / ${accessory.model ?: "?"}"

    @Suppress("DEPRECATION")
    private fun getAccessoryExtra(intent: Intent): UsbAccessory? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(UsbManager.EXTRA_ACCESSORY, UsbAccessory::class.java)
        } else {
            intent.getParcelableExtra(UsbManager.EXTRA_ACCESSORY)
        }

    companion object {
        const val CHANNEL_NAME  = "vmonitor/aoa"
        const val FRAME_CHANNEL = "vmonitor/aoa/frames"
        const val STATE_CHANNEL = "vmonitor/aoa/state"

        private const val ACTION_USB_PERMISSION = "com.example.vmonitor.USB_PERMISSION"
    }
}

/**
 * 開いたアクセサリー 1 本ぶんの読み書き。
 *
 * 読みと書きをそれぞれ専用スレッドで回す。
 * `FileInputStream.read` も `FileOutputStream.write` も相手待ちで止まるため、
 * Flutter のプラットフォームスレッドの上では動かせない。
 */
private class AccessoryConnection(
    private val accessory: UsbAccessory,
    private val descriptor: ParcelFileDescriptor,
    private val onFrame: (channel: Int, data: ByteArray) -> Unit,
    private val onClosed: (detail: String) -> Unit
) {
    companion object {
        /** フレームヘッダー: ChannelId 1 バイト + 長さ 4 バイト (ビッグエンディアン)。 */
        private const val FRAME_HEADER_SIZE = 5

        /**
         * 読み出しバッファ。PC 側の 1 回あたりの転送量と揃えてある。
         * 相手の転送より小さいと、溢れた分が捨てられる端末がある。
         */
        private const val READ_BUFFER_SIZE = 16 * 1024

        /** 壊れたフレームででたらめな長さを確保しないための上限。 */
        private const val MAX_PAYLOAD_SIZE = 32 * 1024 * 1024

        /** 映像チャンネルの番号（Dart の ChannelId.video と対応）。 */
        private const val CHANNEL_VIDEO = 0

        private const val TAG = "AoaPlugin"
    }

    private val input = FileInputStream(descriptor.fileDescriptor)
    private val output = FileOutputStream(descriptor.fileDescriptor)

    private val running = AtomicBoolean(false)

    /** 送信待ちのフレーム。書き込みスレッドが順に吐き出す。 */
    private val sendQueue = LinkedBlockingQueue<ByteArray>(256)

    private var readThread: Thread? = null
    private var writeThread: Thread? = null

    fun info(): Map<String, Any?> = mapOf(
        "manufacturer" to accessory.manufacturer,
        "model" to accessory.model,
        "version" to accessory.version,
        "serial" to accessory.serial
    )

    fun start() {
        if (!running.compareAndSet(false, true)) return

        readThread = Thread({ readLoop() }, "vmonitor-aoa-read").apply {
            isDaemon = true
            start()
        }

        writeThread = Thread({ writeLoop() }, "vmonitor-aoa-write").apply {
            isDaemon = true
            start()
        }
    }

    fun send(channel: Int, payload: ByteArray) {
        if (!running.get()) return

        val frame = ByteArray(FRAME_HEADER_SIZE + payload.size)
        frame[0] = channel.toByte()
        frame[1] = (payload.size ushr 24).toByte()
        frame[2] = (payload.size ushr 16).toByte()
        frame[3] = (payload.size ushr 8).toByte()
        frame[4] = payload.size.toByte()
        payload.copyInto(frame, FRAME_HEADER_SIZE)

        // 送信が詰まっているときに UI ごと止めない。
        // タッチは次のイベントで上書きされるので、捨てても破綻しない。
        if (!sendQueue.offer(frame)) {
            android.util.Log.w(TAG, "送信キューが一杯のためフレームを捨てました")
        }
    }

    fun close() {
        if (!running.compareAndSet(true, false)) return

        // 待ちに入っているスレッドを起こすためにストリームを先に閉じる
        try { input.close() } catch (_: IOException) {}
        try { output.close() } catch (_: IOException) {}
        try { descriptor.close() } catch (_: IOException) {}

        readThread?.interrupt()
        writeThread?.interrupt()
        readThread = null
        writeThread = null
    }

    // ── 受信 ─────────────────────────────────────────────────────────

    private fun readLoop() {
        val chunk = ByteArray(READ_BUFFER_SIZE)

        // USB のバルク転送はこちらの都合と無関係な切れ目で届くので、
        // 溜めてからフレーム単位に切り出す。
        var pending = ByteArray(READ_BUFFER_SIZE * 4)
        var pendingCount = 0

        var reason = "接続が終了しました。"

        try {
            while (running.get()) {
                val read = input.read(chunk)
                if (read < 0) break
                if (read == 0) continue

                if (pendingCount + read > pending.size) {
                    var size = pending.size
                    while (size < pendingCount + read) size *= 2
                    pending = pending.copyOf(size)
                }

                System.arraycopy(chunk, 0, pending, pendingCount, read)
                pendingCount += read

                pendingCount = drainFrames(pending, pendingCount)
            }
        } catch (e: IOException) {
            reason = e.message ?: "USB の読み出しに失敗しました。"
        } catch (e: Exception) {
            reason = e.message ?: "USB の読み出しに失敗しました。"
        }

        if (running.get()) {
            close()
            onClosed(reason)
        }
    }

    /**
     * 溜まったバイト列から、揃っているフレームをすべて取り出す。
     * @return 取り出した後に残ったバイト数。
     */
    private fun drainFrames(buffer: ByteArray, count: Int): Int {
        var offset = 0

        while (count - offset >= FRAME_HEADER_SIZE) {
            val channel = buffer[offset].toInt() and 0xFF

            val length = ((buffer[offset + 1].toInt() and 0xFF) shl 24) or
                         ((buffer[offset + 2].toInt() and 0xFF) shl 16) or
                         ((buffer[offset + 3].toInt() and 0xFF) shl 8) or
                         (buffer[offset + 4].toInt() and 0xFF)

            if (length < 0 || length > MAX_PAYLOAD_SIZE) {
                // 同期がずれている。これ以上は解釈できないので捨てて仕切り直す。
                android.util.Log.w(TAG, "フレーム長が不正です ($length)。バッファを捨てます。")
                return 0
            }

            val total = FRAME_HEADER_SIZE + length
            if (count - offset < total) break   // まだ届ききっていない

            val payload = buffer.copyOfRange(offset + FRAME_HEADER_SIZE, offset + total)
            offset += total

            // 映像はここから直接デコーダーへ渡す。
            //
            // Dart を経由させると 1 枚につきプラットフォームチャンネルを
            // 2 回渡ることになり、しかも待ち合わせが無いので
            // デコードが追いつかないぶんが溜まって遅延が積み上がる。
            // 同期で渡すことで、詰まったときにこの読み出しも止まり、
            // USB 越しに送り手まで背圧が伝わる。
            if (channel == CHANNEL_VIDEO && VideoFrameRouter.deliver(payload))
                continue

            onFrame(channel, payload)
        }

        val remaining = count - offset

        if (remaining > 0 && offset > 0) {
            System.arraycopy(buffer, offset, buffer, 0, remaining)
        }

        return remaining
    }

    // ── 送信 ─────────────────────────────────────────────────────────

    private fun writeLoop() {
        try {
            while (running.get()) {
                val frame = sendQueue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                output.write(frame)
                output.flush()
            }
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        } catch (e: IOException) {
            if (running.get()) {
                close()
                onClosed(e.message ?: "USB の書き込みに失敗しました。")
            }
        }
    }
}
