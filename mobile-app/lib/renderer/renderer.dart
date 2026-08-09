import 'dart:typed_data';

/// レンダラー: エンコード済みフレームを受信・デコードして全画面表示する
///
/// iOS: VideoToolbox ハードウェアデコーダー
/// Android: MediaCodec ハードウェアデコーダー
abstract class Renderer {
  /// エンコード済みフレームストリームの受信を開始してデコード・表示する
  Future<void> start(Stream<Uint8List> encodedFrames);

  /// レンダリングを停止する
  Future<void> stop();

  /// fps・デコード遅延などの統計ストリーム
  Stream<RendererStats> get statsStream;
}

/// レンダラーの統計情報
class RendererStats {
  final double fps;
  final int decodeLatencyMs;

  const RendererStats({
    required this.fps,
    required this.decodeLatencyMs,
  });
}
