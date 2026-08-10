package com.kirea.vmonitor

import java.util.concurrent.atomic.AtomicLong

/**
 * 受け取った映像フレームをデコーダーへ直接渡すための受け渡し口。
 *
 * ## なぜ必要か
 *
 * もともと映像は USB から Dart へ上げ、Dart からまた Kotlin のデコーダーへ
 * 戻していた。1 枚につきプラットフォームチャンネルを 2 回渡ることになり、
 * どちらもメインスレッドで直列化される。さらにどの段にも上限が無く、
 * Dart 側の転送は完了を待たずに次を受け付けていた。
 *
 * USB は速いので取りこぼしは起きない。その代わり、デコードが追いつかないと
 * フレームがどこかに溜まり続け、遅延が積み上がる。実際に 0.5 秒ほど遅れていた。
 *
 * ## どう直すか
 *
 * 映像だけは Dart を経由せず、USB の読み出しスレッドから直接デコーダーへ渡す。
 * デコーダーが詰まれば読み出しも止まり、USB の受け口が埋まって
 * PC 側の送信が待たされる。こうして送り手まで背圧が伝わり、
 * 溜まる場所が無くなる。
 *
 * タッチや制御は量が少なく、UI の状態にも関わるので今まで通り Dart へ渡す。
 */
object VideoFrameRouter {

    /**
     * デコーダーへの受け渡し先。
     * レンダラーが用意できたときに設定され、破棄されると null に戻る。
     */
    @Volatile
    private var sink: ((ByteArray) -> Unit)? = null

    /** USB から受け取った映像フレーム数（表示用）。 */
    val framesReceived = AtomicLong(0)

    /** デコーダーへ渡した映像フレーム数（表示用）。 */
    val framesDelivered = AtomicLong(0)

    fun setSink(callback: ((ByteArray) -> Unit)?) {
        sink = callback
    }

    /**
     * 映像フレームを渡す。
     *
     * @return 直接渡せたら true。受け口が無ければ false（呼び出し元が Dart へ回す）。
     */
    fun deliver(data: ByteArray): Boolean {
        framesReceived.incrementAndGet()

        val target = sink ?: return false

        // ここは USB の読み出しスレッド。あえて同期で呼ぶ。
        // デコーダーが待たされれば読み出しも待たされ、それが背圧になる。
        target(data)

        framesDelivered.incrementAndGet()
        return true
    }
}
