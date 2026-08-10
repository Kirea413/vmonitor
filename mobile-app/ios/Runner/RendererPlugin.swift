import Flutter
import UIKit
import VideoToolbox
import CoreVideo
import AVFoundation

/// vmonitor レンダラープラグイン (iOS)
///
/// Flutter MethodChannel "vmonitor/renderer" を通じて H.264 フレームを受け取り、
/// VideoToolbox でハードウェアデコードして Flutter Texture に描画する。
///
/// MethodChannel プロトコル:
///   initialize  -> textureId (Int64)
///   pushFrame   <- {textureId: Int64, data: FlutterStandardTypedData}
///   dispose     <- {textureId: Int64}
///
/// EventChannel "vmonitor/renderer/stats":
///   {fps: Double, decodeLatencyMs: Int}
@objc class RendererPlugin: NSObject, FlutterPlugin {

    private var methodChannel: FlutterMethodChannel?
    private var statsEventChannel: FlutterEventChannel?
    private var statsEventSink: FlutterEventSink?
    private weak var textureRegistry: FlutterTextureRegistry?

    private var sessions: [Int64: RendererSession] = [:]

    static func register(with registrar: FlutterPluginRegistrar) {
        let plugin = RendererPlugin()
        plugin.textureRegistry = registrar.textures()

        let channel = FlutterMethodChannel(
            name: "vmonitor/renderer",
            binaryMessenger: registrar.messenger()
        )
        registrar.addMethodCallDelegate(plugin, channel: channel)
        plugin.methodChannel = channel

        let statsChannel = FlutterEventChannel(
            name: "vmonitor/renderer/stats",
            binaryMessenger: registrar.messenger()
        )
        statsChannel.setStreamHandler(plugin)
        plugin.statsEventChannel = statsChannel
    }

    func handle(_ call: FlutterMethodCall, result: @escaping FlutterResult) {
        switch call.method {
        case "initialize":
            handleInitialize(result: result)
        case "pushFrame":
            handlePushFrame(call: call, result: result)
        case "dispose":
            handleDispose(call: call, result: result)
        default:
            result(FlutterMethodNotImplemented)
        }
    }

    // MARK: - Handlers

    private func handleInitialize(result: @escaping FlutterResult) {
        guard let registry = textureRegistry else {
            result(FlutterError(code: "NO_REGISTRY", message: "TextureRegistry unavailable", details: nil))
            return
        }

        let session = RendererSession(registry: registry) { [weak self] fps, latencyMs in
            self?.emitStats(fps: fps, latencyMs: latencyMs)
        }

        sessions[session.textureId] = session
        result(session.textureId)
    }

    private func handlePushFrame(call: FlutterMethodCall, result: @escaping FlutterResult) {
        guard let args = call.arguments as? [String: Any],
              let textureId = args["textureId"] as? Int64,
              let flutterData = args["data"] as? FlutterStandardTypedData else {
            result(FlutterError(code: "INVALID_ARG", message: "textureId or data missing", details: nil))
            return
        }

        guard let session = sessions[textureId] else {
            result(FlutterError(code: "NOT_FOUND", message: "textureId \(textureId) not found", details: nil))
            return
        }

        session.pushFrame(data: flutterData.data)
        result(nil)
    }

    private func handleDispose(call: FlutterMethodCall, result: @escaping FlutterResult) {
        guard let args = call.arguments as? [String: Any],
              let textureId = args["textureId"] as? Int64 else {
            result(FlutterError(code: "INVALID_ARG", message: "textureId missing", details: nil))
            return
        }

        sessions.removeValue(forKey: textureId)?.release()
        result(nil)
    }

    private func emitStats(fps: Double, latencyMs: Int) {
        DispatchQueue.main.async { [weak self] in
            self?.statsEventSink?(["fps": fps, "decodeLatencyMs": latencyMs])
        }
    }
}

// MARK: - FlutterStreamHandler

extension RendererPlugin: FlutterStreamHandler {
    func onListen(withArguments arguments: Any?, eventSink events: @escaping FlutterEventSink) -> FlutterError? {
        statsEventSink = events
        return nil
    }

    func onCancel(withArguments arguments: Any?) -> FlutterError? {
        statsEventSink = nil
        return nil
    }
}

// MARK: - RendererSession

/// 1 テクスチャに対応する VideoToolbox デコードセッション。
class RendererSession: NSObject {

    let textureId: Int64

    private let textureEntry: FlutterTextureRegistry
    // 絵の置き場は VMonitorTexture が持つ。
    // ここにも同じものを抱えていたが、Flutter が読むのは向こうなので
    // 誰にも使われないまま「入れてあるのに映らない」原因になっていた。
    private var decompressionSession: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    private let flutterTexture: VMonitorTexture
    private let onStats: (Double, Int) -> Void

    // FPS・レイテンシ計測
    private var frameCount = 0
    private var windowStart = Date()
    private var recentLatencies: [Double] = []

    // SPS/PPS パーサー用バッファ
    private var spsData: Data?
    private var ppsData: Data?
    private var isFormatReady = false

    init(registry: FlutterTextureRegistry, onStats: @escaping (Double, Int) -> Void) {
        self.textureEntry = registry
        self.onStats = onStats
        self.flutterTexture = VMonitorTexture()
        self.textureId = registry.register(flutterTexture)
    }

    deinit { release() }

    func release() {
        if let session = decompressionSession {
            // 処理中のフレームを先に吐き出させる。
            //
            // 非同期でデコードさせているので、Invalidate だけでは
            // まだ途中のものが残る。コールバックには自分を
            // passUnretained で渡してあり、片付けたあとに呼ばれると
            // 消えた相手を触ることになる。deinit からも来る経路なので、
            // そのときは既に解放が始まっている。
            VTDecompressionSessionWaitForAsynchronousFrames(session)

            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }
        textureEntry.unregisterTexture(textureId)
    }

    /// H.264 Annex-B NAL ユニット (0x00000001 スタートコード付き) をデコードする。
    func pushFrame(data: Data) {
        let start = Date()

        // SPS / PPS は毎回見る。
        //
        // 以前は isFormatReady が立つまでしか読んでいなかった。そのため
        // 端末を回して PC が仮想ディスプレイを作り直しても、新しい
        // SPS/PPS を無視して古い形式のまま復号し続け、画面が縦のまま
        // 戻らなくなっていた。
        parseParameterSets(from: data)

        if !isFormatReady { return }

        guard let formatDesc = formatDescription else { return }

        // Annex-B → AVCC 形式に変換して CMSampleBuffer を作成する
        guard let sampleBuffer = makeSampleBuffer(from: data, formatDesc: formatDesc) else {
            return
        }

        // デコンプレッションセッションを必要に応じて作成する
        if decompressionSession == nil {
            createDecompressionSession(formatDesc: formatDesc)
        }

        guard let session = decompressionSession else { return }

        // デコード実行（同期: kVTDecodeFrame_EnableAsynchronousDecompression を付けない）
        var flagsOut = VTDecodeInfoFlags()
        VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [._EnableAsynchronousDecompression],
            frameRefcon: nil,
            infoFlagsOut: &flagsOut
        )

        let latencyMs = Date().timeIntervalSince(start) * 1000
        updateStats(latencyMs: latencyMs)
    }

    // MARK: - Private

    /// Annex-B ストリームから SPS (0x67) / PPS (0x68) を探して
    /// CMVideoFormatDescription を作成する。
    private func parseParameterSets(from data: Data) {
        let bytes = [UInt8](data)

        var foundSps = false
        var foundPps = false

        for range in Self.nalRanges(bytes) {
            switch bytes[range.lowerBound] & 0x1F {
            case 7:   // SPS
                spsData = Data(bytes[range])
                foundSps = true
            case 8:   // PPS
                ppsData = Data(bytes[range])
                foundPps = true
            default:
                break
            }
        }

        guard foundSps, foundPps,
              let sps = spsData, let pps = ppsData else { return }

        // 中身が変わっていなければ何もしない。毎フレーム作り直すと重い。
        if isFormatReady, sps == lastSps, pps == lastPps { return }

        lastSps = sps
        lastPps = pps

        // 解像度が変わった。前のセッションは古い形式のままなので捨てる。
        // 残したまま新しい絵を流し込むと、復号できないか、前の寸法の
        // ままの絵が出てくる。
        if let session = decompressionSession {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }

        isFormatReady = false

        createFormatDescription(sps: sps, pps: pps)
    }

    /// 直近に採用したパラメータセット。作り直しの要否を見るために持つ。
    private var lastSps: Data?
    private var lastPps: Data?

    /// Annex-B の中身を NAL 単位に切り分ける。
    ///
    /// 以前はここが 2 か所（パラメータセットの取り出しと AVCC への変換）に
    /// 別々に書かれていて、どちらにも同じ 2 つの誤りがあった。
    ///
    /// 1. 末尾が 3 バイト切れていた
    ///
    ///    `while end + 3 < bytes.count` で次のスタートコードを探すため、
    ///    見つからなかった場合の end は `bytes.count - 3` で止まる。
    ///    後ろに `if end == bytes.count { end = bytes.count }` が
    ///    書いてあったが、この条件は決して成立しない。
    ///    結果、パケット末尾の NAL は必ず 3 バイト短くなっていた。
    ///
    /// 2. 3 バイトのスタートコードを見ていなかった
    ///
    ///    Annex-B のスタートコードは 00 00 01 と 00 00 00 01 の
    ///    どちらもありうる。前者を読み飛ばすと NAL の切れ目がずれ、
    ///    長さが全部狂う。
    ///
    /// どちらもデコーダは何も言わずに黙り、画面が真っ暗になるだけだった。
    private static func nalRanges(_ bytes: [UInt8]) -> [Range<Int>] {
        // (NAL 本体の開始位置, スタートコード自体の開始位置)
        var found: [(body: Int, code: Int)] = []

        var i = 0
        while i + 2 < bytes.count {
            guard bytes[i] == 0, bytes[i + 1] == 0 else {
                i += 1
                continue
            }

            if bytes[i + 2] == 1 {
                found.append((body: i + 3, code: i))
                i += 3
            } else if i + 3 < bytes.count, bytes[i + 2] == 0, bytes[i + 3] == 1 {
                found.append((body: i + 4, code: i))
                i += 4
            } else {
                i += 1
            }
        }

        var ranges: [Range<Int>] = []

        for (index, item) in found.enumerated() {
            // 最後の NAL はデータの終わりまで。ここを縮めない。
            let end = index + 1 < found.count ? found[index + 1].code : bytes.count

            if item.body < end { ranges.append(item.body..<end) }
        }

        return ranges
    }

    private func createFormatDescription(sps: Data, pps: Data) {
        let spsBytes = [UInt8](sps)
        let ppsBytes = [UInt8](pps)

        var desc: CMVideoFormatDescription?

        // withUnsafeBufferPointer が渡すポインタは、閉じ括弧までしか有効でない。
        //
        // 以前はここで取り出したポインタを配列に詰めて外へ持ち出していた。
        // 括弧を抜けた時点で指し先の保証が切れるので、SPS/PPS として
        // 何が読まれるか分からない。使う場所まで入れ子にして生かす。
        let status = spsBytes.withUnsafeBufferPointer { spsBuffer -> OSStatus in
            guard let spsBase = spsBuffer.baseAddress else { return -1 }

            return ppsBytes.withUnsafeBufferPointer { ppsBuffer -> OSStatus in
                guard let ppsBase = ppsBuffer.baseAddress else { return -1 }

                let pointers: [UnsafePointer<UInt8>] = [spsBase, ppsBase]
                let sizes:    [Int]                  = [spsBytes.count, ppsBytes.count]

                return pointers.withUnsafeBufferPointer { pointerBuffer in
                    sizes.withUnsafeBufferPointer { sizeBuffer in
                        CMVideoFormatDescriptionCreateFromH264ParameterSets(
                            allocator:            kCFAllocatorDefault,
                            parameterSetCount:    2,
                            parameterSetPointers: pointerBuffer.baseAddress!,
                            parameterSetSizes:    sizeBuffer.baseAddress!,
                            nalUnitHeaderLength:  4,
                            formatDescriptionOut: &desc
                        )
                    }
                }
            }
        }

        if status == noErr, let desc = desc {
            formatDescription = desc
            isFormatReady = true
        }
    }

    /// Annex-B → AVCC 変換 + CMSampleBuffer 生成。
    private func makeSampleBuffer(from data: Data, formatDesc: CMVideoFormatDescription) -> CMSampleBuffer? {
        // Annex-B スタートコードを AVCC 4バイト長に置換する
        let avcc = convertAnnexBToAVCC(data: data)
        guard !avcc.isEmpty else { return nil }

        let avccBytes = [UInt8](avcc)

        // 中身を持つブロックバッファを作らせる。
        //
        // 以前は blockAllocator に kCFAllocatorNull を渡し、ローカル配列の
        // メモリをそのまま指させていた。あれは「複製しない」という指定で、
        // この関数を抜けた瞬間に指し先が無くなる。返した CMSampleBuffer は
        // 解放済みの領域を読むことになり、何が映るか（落ちるかどうかも）
        // 分からない。
        //
        // memoryBlock を nil にすると、必要な長さをバッファ側が確保する。
        // そこへ中身を写せば、持ち主はバッファになる。
        var blockBuffer: CMBlockBuffer?

        var status1 = CMBlockBufferCreateWithMemoryBlock(
            allocator:         kCFAllocatorDefault,
            memoryBlock:       nil,
            blockLength:       avccBytes.count,
            blockAllocator:    kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData:      0,
            dataLength:        avccBytes.count,
            flags:             0,
            blockBufferOut:    &blockBuffer
        )

        guard status1 == noErr, let blockBuf = blockBuffer else { return nil }

        status1 = CMBlockBufferAssureBlockMemory(blockBuf)
        guard status1 == noErr else { return nil }

        status1 = avccBytes.withUnsafeBufferPointer { pointer in
            guard let base = pointer.baseAddress else { return OSStatus(-1) }

            return CMBlockBufferReplaceDataBytes(
                with:                  base,
                blockBuffer:           blockBuf,
                offsetIntoDestination: 0,
                dataLength:            avccBytes.count
            )
        }

        guard status1 == noErr else { return nil }

        var sampleBuffer: CMSampleBuffer?
        let status2 = CMSampleBufferCreate(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuf,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDesc,
            sampleCount: 1,
            sampleTimingEntryCount: 0,
            sampleTimingArray: nil,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer
        )
        return status2 == noErr ? sampleBuffer : nil
    }

    private func convertAnnexBToAVCC(data: Data) -> Data {
        let bytes = [UInt8](data)

        var result = Data()

        for range in Self.nalRanges(bytes) {
            // AVCC: 4 バイトの長さ (big-endian) + NAL 本体
            var lengthBE = UInt32(range.count).bigEndian

            withUnsafeBytes(of: &lengthBE) { result.append(contentsOf: $0) }
            result.append(contentsOf: bytes[range])
        }

        return result
    }

    private func createDecompressionSession(formatDesc: CMVideoFormatDescription) {
        let attrs: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:]
        ]

        var outputCallback = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: { refcon, _, status, _, imageBuffer, presentationTimeStamp, duration in
                guard status == noErr, let imgBuf = imageBuffer else { return }
                // RendererSession への参照を取り出してピクセルバッファを更新する
                if let sessionRef = refcon {
                    let session = Unmanaged<RendererSession>.fromOpaque(sessionRef).takeUnretainedValue()
                    session.onDecodedFrame(imageBuffer: imgBuf)
                }
            },
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        var session: VTDecompressionSession?
        VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: formatDesc,
            decoderSpecification: nil,
            imageBufferAttributes: attrs as CFDictionary,
            outputCallback: &outputCallback,
            decompressionSessionOut: &session
        )
        decompressionSession = session
    }

    func onDecodedFrame(imageBuffer: CVImageBuffer) {
        // デコードできた絵を、Flutter が読みに来る先へ渡す。
        //
        // ここが抜けていた。以前は RendererSession 自身の変数へ入れて
        // いたが、Flutter が呼ぶのは VMonitorTexture.copyPixelBuffer の
        // ほうで、そちらは何も渡されないまま常に nil を返していた。
        // デコードも通知も動いているのに、画面だけ真っ暗になっていた。
        flutterTexture.update(pixelBuffer: imageBuffer)

        // 新しい絵があることをエンジンに伝える。
        // これを呼ばないと copyPixelBuffer は読みに来ない。
        textureEntry.textureFrameAvailable(textureId)
    }

    private func updateStats(latencyMs: Double) {
        recentLatencies.append(latencyMs)
        if recentLatencies.count > 30 { recentLatencies.removeFirst() }
        let avgLatency = Int(recentLatencies.reduce(0, +) / Double(recentLatencies.count))

        frameCount += 1
        let elapsed = Date().timeIntervalSince(windowStart)
        if elapsed >= 1.0 {
            let fps = Double(frameCount) / elapsed
            frameCount = 0
            windowStart = Date()
            onStats(fps, avgLatency)
        }
    }
}

// MARK: - VMonitorTexture (FlutterTexture)

/// FlutterTexture プロトコルの実装。
/// デコード済み CVPixelBuffer を Flutter エンジンに渡す。
class VMonitorTexture: NSObject, FlutterTexture {
    private var pixelBuffer: CVPixelBuffer?
    private let lock = NSLock()

    func update(pixelBuffer: CVPixelBuffer?) {
        lock.lock()
        self.pixelBuffer = pixelBuffer
        lock.unlock()
    }

    func copyPixelBuffer() -> Unmanaged<CVPixelBuffer>? {
        lock.lock()
        defer { lock.unlock() }
        guard let buf = pixelBuffer else { return nil }
        return Unmanaged.passRetained(buf)
    }
}
