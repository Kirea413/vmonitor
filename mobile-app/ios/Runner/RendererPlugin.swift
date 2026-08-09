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
    private var pixelBuffer: CVPixelBuffer?
    private let pixelBufferLock = NSLock()
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
            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }
        textureEntry.unregisterTexture(textureId)
    }

    /// H.264 Annex-B NAL ユニット (0x00000001 スタートコード付き) をデコードする。
    func pushFrame(data: Data) {
        let start = Date()

        // SPS / PPS を抽出・フォーマット記述を準備する
        if !isFormatReady {
            parseParameterSets(from: data)
            if !isFormatReady { return }
        }

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
        var i = 0
        var foundSps = false
        var foundPps = false

        while i + 4 < bytes.count {
            // スタートコード 0x00000001 を探す
            if bytes[i] == 0 && bytes[i+1] == 0 && bytes[i+2] == 0 && bytes[i+3] == 1 {
                let nalStart = i + 4
                if nalStart >= bytes.count { break }

                let nalType = bytes[nalStart] & 0x1F
                // 次のスタートコードまでの長さを計算する
                var end = nalStart + 1
                while end + 3 < bytes.count {
                    if bytes[end] == 0 && bytes[end+1] == 0 && bytes[end+2] == 0 && bytes[end+3] == 1 { break }
                    end += 1
                }

                let nalBytes = Array(bytes[nalStart..<end])

                if nalType == 7 { // SPS
                    spsData = Data(nalBytes)
                    foundSps = true
                } else if nalType == 8 { // PPS
                    ppsData = Data(nalBytes)
                    foundPps = true
                }

                i = end
            } else {
                i += 1
            }
        }

        if foundSps, foundPps,
           let sps = spsData, let pps = ppsData {
            createFormatDescription(sps: sps, pps: pps)
        }
    }

    private func createFormatDescription(sps: Data, pps: Data) {
        var spsBytes = [UInt8](sps)
        var ppsBytes = [UInt8](pps)

        let parameterSetPointers: [UnsafePointer<UInt8>?] = [
            spsBytes.withUnsafeBufferPointer { $0.baseAddress },
            ppsBytes.withUnsafeBufferPointer { $0.baseAddress }
        ]
        let parameterSetSizes: [Int] = [spsBytes.count, ppsBytes.count]

        var desc: CMVideoFormatDescription?
        let status = parameterSetPointers.withUnsafeBufferPointer { ptrPtr in
            parameterSetSizes.withUnsafeBufferPointer { sizPtr in
                CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: ptrPtr.baseAddress!,
                    parameterSetSizes: sizPtr.baseAddress!,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &desc
                )
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

        var blockBuffer: CMBlockBuffer?
        let avccBytes = [UInt8](avcc)
        let status1 = avccBytes.withUnsafeBufferPointer { ptr in
            CMBlockBufferCreateWithMemoryBlock(
                allocator: kCFAllocatorDefault,
                memoryBlock: UnsafeMutableRawPointer(mutating: ptr.baseAddress!),
                blockLength: avcc.count,
                blockAllocator: kCFAllocatorNull,
                customBlockSource: nil,
                offsetToData: 0,
                dataLength: avcc.count,
                flags: 0,
                blockBufferOut: &blockBuffer
            )
        }
        guard status1 == noErr, let blockBuf = blockBuffer else { return nil }

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
        var result = Data()
        let bytes = [UInt8](data)
        var i = 0

        while i + 4 < bytes.count {
            // スタートコード 0x00000001
            if bytes[i] == 0 && bytes[i+1] == 0 && bytes[i+2] == 0 && bytes[i+3] == 1 {
                let nalStart = i + 4
                // 次のスタートコードを探す
                var end = nalStart
                while end + 3 < bytes.count {
                    if bytes[end] == 0 && bytes[end+1] == 0 && bytes[end+2] == 0 && bytes[end+3] == 1 { break }
                    end += 1
                }
                if end == bytes.count { end = bytes.count }

                let nalLength = end - nalStart
                // AVCC: 4バイトの長さ (big-endian) + NAL データ
                var lengthBE = UInt32(nalLength).bigEndian
                result.append(contentsOf: withUnsafeBytes(of: &lengthBE) { Array($0) })
                result.append(contentsOf: bytes[nalStart..<end])
                i = end
            } else {
                i += 1
            }
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
        pixelBufferLock.lock()
        pixelBuffer = imageBuffer as? CVPixelBuffer
        pixelBufferLock.unlock()

        // Flutter エンジンにテクスチャの更新を通知する
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
