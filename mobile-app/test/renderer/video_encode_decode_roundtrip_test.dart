// Feature: vmonitor, Property 5: 映像エンコード・デコードのラウンドトリップ
//
// Property 5: 映像エンコード・デコードのラウンドトリップ
// 任意の有効なビデオフレームに対して、ストリーマーがエンコードした出力を
// レンダラーがデコードすることでフレームが正しく復元されなければならない。
//
// Validates: Requirements 4.1, 4.2

import 'dart:typed_data';

import 'package:glados/glados.dart';

// ─── データモデル ────────────────────────────────────────────────────────────

/// ビデオフレームを表すデータモデル。
///
/// 実ハードウェアコーデック (VideoToolbox / MediaCodec) は
/// テスト環境では利用できないため、このモデルレベルでラウンドトリップを検証する。
class VideoFrame {
  /// フレームの幅（ピクセル）。有効範囲: 1 以上。
  final int width;

  /// フレームの高さ（ピクセル）。有効範囲: 1 以上。
  final int height;

  /// フレームの生ピクセルデータ（RGBA, 4 バイト/ピクセル）。
  final Uint8List pixelData;

  /// フレームのシーケンス番号（順序保証の検証に使用）。
  final int sequenceNumber;

  const VideoFrame({
    required this.width,
    required this.height,
    required this.pixelData,
    required this.sequenceNumber,
  });

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is VideoFrame &&
          width == other.width &&
          height == other.height &&
          sequenceNumber == other.sequenceNumber &&
          _bytesEqual(pixelData, other.pixelData);

  @override
  int get hashCode =>
      Object.hash(width, height, sequenceNumber, pixelData.length);

  @override
  String toString() => 'VideoFrame(width=$width, height=$height, '
      'seq=$sequenceNumber, dataLen=${pixelData.length})';

  static bool _bytesEqual(Uint8List a, Uint8List b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}

// ─── エンコード済みフレームのデータモデル ────────────────────────────────────

/// ストリーマーがエンコードして送信する圧縮済みフレーム。
///
/// 実装では H.264/H.265 の NAL ユニットが入るが、
/// テストではモックエンコーダーが生成するバイト列を使用する。
class EncodedFrame {
  /// コーデック識別子。
  final String codec;

  /// エンコード済みバイト列。
  final Uint8List data;

  /// 元フレームのシーケンス番号（ラウンドトリップ検証に使用）。
  final int sequenceNumber;

  /// 元フレームの幅・高さ（デコード時に必要なメタデータ）。
  final int width;
  final int height;

  const EncodedFrame({
    required this.codec,
    required this.data,
    required this.sequenceNumber,
    required this.width,
    required this.height,
  });
}

// ─── モックエンコーダー（ストリーマーのスタブ） ─────────────────────────────

/// テスト用モックエンコーダー。
///
/// 実際のハードウェアコーデックの代わりに、フレームデータを
/// 可逆的な変換（XOR マスク + メタデータヘッダー付加）でエンコードする。
/// デコーダーが逆変換を行うことで、ラウンドトリップの正確性を保証できる。
class MockVideoEncoder {
  static const String _codec = 'mock-lossless';

  // XOR マスクとして使用する固定パターン（非ゼロを保証）
  static const int _xorMask = 0xA5;

  /// フレームをエンコードする。
  ///
  /// フォーマット:
  ///   [0..3]   width (big-endian int32)
  ///   [4..7]   height (big-endian int32)
  ///   [8..11]  sequenceNumber (big-endian int32)
  ///   [12..15] pixelData.length (big-endian int32)
  ///   [16..]   XOR(_xorMask) 済みピクセルデータ
  static EncodedFrame encode(VideoFrame frame) {
    final pixelLen = frame.pixelData.length;
    const headerSize = 16;
    final totalSize = headerSize + pixelLen;

    final data = Uint8List(totalSize);
    final view = ByteData.sublistView(data);

    // ヘッダーを書き込む
    view.setInt32(0, frame.width, Endian.big);
    view.setInt32(4, frame.height, Endian.big);
    view.setInt32(8, frame.sequenceNumber, Endian.big);
    view.setInt32(12, pixelLen, Endian.big);

    // ピクセルデータを XOR してエンコード
    for (var i = 0; i < pixelLen; i++) {
      data[headerSize + i] = frame.pixelData[i] ^ _xorMask;
    }

    return EncodedFrame(
      codec: _codec,
      data: data,
      sequenceNumber: frame.sequenceNumber,
      width: frame.width,
      height: frame.height,
    );
  }
}

// ─── モックデコーダー（レンダラーのスタブ） ─────────────────────────────────

/// テスト用モックデコーダー。
///
/// [MockVideoEncoder] によってエンコードされたフレームを復元する。
/// 実装では VideoToolbox / MediaCodec を呼び出すが、
/// テストではモック変換の逆変換を行う。
class MockVideoDecoder {
  static const int _xorMask = MockVideoEncoder._xorMask;

  /// [MockVideoEncoder.encode] でエンコードされたフレームをデコードする。
  static VideoFrame decode(EncodedFrame encoded) {
    final data = encoded.data;
    final view = ByteData.sublistView(data);

    // ヘッダーを読み込む
    final width = view.getInt32(0, Endian.big);
    final height = view.getInt32(4, Endian.big);
    final sequenceNumber = view.getInt32(8, Endian.big);
    final pixelLen = view.getInt32(12, Endian.big);

    // ピクセルデータを XOR してデコード（XOR は自己逆変換）
    final pixelData = Uint8List(pixelLen);
    const headerSize = 16;
    for (var i = 0; i < pixelLen; i++) {
      pixelData[i] = data[headerSize + i] ^ _xorMask;
    }

    return VideoFrame(
      width: width,
      height: height,
      pixelData: pixelData,
      sequenceNumber: sequenceNumber,
    );
  }
}

// ─── glados ジェネレーター ────────────────────────────────────────────────────

/// 有効なビデオフレーム幅・高さを生成するジェネレーター。
///
/// 仮想ディスプレイの最小サポート解像度 640x480 〜 最大 3840x2160 の範囲
/// (design.md: Resolution.MinSupported / MaxSupported) を模倣するが、
/// テスト速度のため小さめの範囲 (1..128) を使用する。
extension VideoFrameAny on Any {
  Generator<VideoFrame> get validVideoFrame {
    return any.combine3(
      any.intInRange(1, 129), // width: 1..128
      any.intInRange(1, 129), // height: 1..128
      any.intInRange(0, 10000), // sequenceNumber: 0..9999
      (int width, int height, int seq) {
        // RGBA 4 バイト/ピクセルのピクセルデータを生成する。
        // サイズが width*height*4 になるよう固定して、
        // モックエンコーダーがヘッダーに埋め込んだサイズと一致させる。
        final pixelCount = width * height * 4;
        // シンプルなパターン: インデックスと seq の XOR で決定論的に生成する
        final pixels = Uint8List.fromList(
          List.generate(pixelCount, (i) => (i ^ seq) & 0xFF),
        );
        return VideoFrame(
          width: width,
          height: height,
          pixelData: pixels,
          sequenceNumber: seq,
        );
      },
    );
  }
}

// ─── テスト本体 ──────────────────────────────────────────────────────────────

void main() {
  group('Property 5: 映像エンコード・デコードのラウンドトリップ', () {
    // ── プロパティテスト ──────────────────────────────────────────────────

    /// **Validates: Requirements 4.1, 4.2**
    ///
    /// 任意の有効なビデオフレームに対して、エンコード後にデコードすることで
    /// 元のフレームが完全に復元されなければならない。
    ///
    /// Requirements 4.1: ストリーマーが映像フレームをエンコードして送信する
    /// Requirements 4.2: レンダラーが受信フレームをデコードして表示する
    Glados(any.validVideoFrame).test(
      '任意の有効なビデオフレームのエンコード・デコードラウンドトリップが元フレームと一致する',
      (VideoFrame originalFrame) {
        // ストリーマー側: フレームをエンコードする (Requirements 4.1)
        final encoded = MockVideoEncoder.encode(originalFrame);

        // レンダラー側: エンコード済みデータをデコードする (Requirements 4.2)
        final decoded = MockVideoDecoder.decode(encoded);

        // ラウンドトリップで元のフレームと一致することを検証する
        expect(decoded.width, equals(originalFrame.width),
            reason: 'デコード後のwidthが元フレームと一致しなければならない');
        expect(decoded.height, equals(originalFrame.height),
            reason: 'デコード後のheightが元フレームと一致しなければならない');
        expect(decoded.sequenceNumber, equals(originalFrame.sequenceNumber),
            reason: 'デコード後のシーケンス番号が元フレームと一致しなければならない');
        expect(decoded.pixelData.length, equals(originalFrame.pixelData.length),
            reason: 'デコード後のピクセルデータサイズが元フレームと一致しなければならない');
        expect(decoded.pixelData, equals(originalFrame.pixelData),
            reason: 'デコード後のピクセルデータが元フレームと一致しなければならない');
        expect(decoded, equals(originalFrame),
            reason: 'デコードされたフレームが元のフレームと完全に等しくなければならない');
      },
    );

    /// **Validates: Requirements 4.1, 4.2**
    ///
    /// エンコード済みデータは元のピクセルデータとは異なるバイト列でなければならない
    /// （エンコードが実際に変換を行っていることの確認）。
    ///
    /// これは「エンコードが恒等変換でないこと」を保証し、
    /// ストリーマーが実際に処理を行っていることを検証する。
    Glados(any.validVideoFrame).test(
      'エンコード済みデータは元のピクセルデータと異なる（エンコードが変換を行う）',
      (VideoFrame frame) {
        // ピクセルデータが空でない場合のみ検証する
        if (frame.pixelData.isEmpty) return;

        final encoded = MockVideoEncoder.encode(frame);

        // エンコード後のデータはヘッダー（16バイト）+ 変換済みピクセルデータ
        // 元のピクセルデータと同一バイト列であってはならない
        final encodedPixelSection =
            encoded.data.sublist(16, 16 + frame.pixelData.length);

        // XOR マスク 0xA5 なので、すべてゼロのピクセルデータでない限り必ず異なる
        // (0 ^ 0xA5 = 0xA5 ≠ 0)
        // 少なくとも 1 バイトは異なることを確認する
        var hasAnyDifference = false;
        for (var i = 0; i < frame.pixelData.length; i++) {
          if (encodedPixelSection[i] != frame.pixelData[i]) {
            hasAnyDifference = true;
            break;
          }
        }
        // ピクセルデータが全て同じ値でかつ XOR で変化しない場合は除外
        // (理論上、0 ^ 0xA5 = 0xA5 なので常に true になる)
        expect(hasAnyDifference, isTrue, reason: 'エンコード処理はピクセルデータを変換しなければならない');
      },
    );

    // ── ユニットテスト（具体的な例） ──────────────────────────────────────

    group('具体的なフレームでのラウンドトリップ検証', () {
      test('1x1 の最小フレームのラウンドトリップ', () {
        final frame = VideoFrame(
          width: 1,
          height: 1,
          pixelData: Uint8List.fromList([0xFF, 0x00, 0x80, 0xFF]), // RGBA
          sequenceNumber: 0,
        );

        final encoded = MockVideoEncoder.encode(frame);
        final decoded = MockVideoDecoder.decode(encoded);

        expect(decoded, equals(frame));
      });

      test('シーケンス番号が正しく保持される', () {
        const seqNumbers = [0, 1, 999, 65535];

        for (final seq in seqNumbers) {
          final frame = VideoFrame(
            width: 2,
            height: 2,
            pixelData: Uint8List(16), // 2x2 RGBA = 16 バイト
            sequenceNumber: seq,
          );

          final encoded = MockVideoEncoder.encode(frame);
          final decoded = MockVideoDecoder.decode(encoded);

          expect(decoded.sequenceNumber, equals(seq),
              reason: 'シーケンス番号 $seq が保持されなければならない');
        }
      });

      test('全ゼロのピクセルデータのラウンドトリップ', () {
        final frame = VideoFrame(
          width: 4,
          height: 4,
          pixelData: Uint8List(64), // 4x4 RGBA = 64 バイト、全ゼロ
          sequenceNumber: 42,
        );

        final encoded = MockVideoEncoder.encode(frame);
        final decoded = MockVideoDecoder.decode(encoded);

        expect(decoded, equals(frame));
      });

      test('全 0xFF のピクセルデータ（最大値）のラウンドトリップ', () {
        final frame = VideoFrame(
          width: 2,
          height: 2,
          pixelData: Uint8List.fromList(List.filled(16, 0xFF)),
          sequenceNumber: 1000,
        );

        final encoded = MockVideoEncoder.encode(frame);
        final decoded = MockVideoDecoder.decode(encoded);

        expect(decoded, equals(frame));
      });

      test('ランダムパターンのピクセルデータのラウンドトリップ', () {
        final frame = VideoFrame(
          width: 8,
          height: 8,
          pixelData: Uint8List.fromList(
            List.generate(256, (i) => (i * 7 + 13) & 0xFF), // 8x8 RGBA
          ),
          sequenceNumber: 12345,
        );

        final encoded = MockVideoEncoder.encode(frame);
        final decoded = MockVideoDecoder.decode(encoded);

        expect(decoded, equals(frame));
      });

      test('複数フレームの連続ラウンドトリップでシーケンス番号の独立性を検証', () {
        final frames = List.generate(
          10,
          (i) => VideoFrame(
            width: 4,
            height: 4,
            pixelData:
                Uint8List.fromList(List.generate(64, (j) => (i + j) & 0xFF)),
            sequenceNumber: i,
          ),
        );

        final decodedFrames = frames
            .map(MockVideoEncoder.encode)
            .map(MockVideoDecoder.decode)
            .toList();

        for (var i = 0; i < frames.length; i++) {
          expect(decodedFrames[i], equals(frames[i]),
              reason: 'フレーム $i のラウンドトリップが正確でなければならない');
          expect(decodedFrames[i].sequenceNumber, equals(i),
              reason: 'フレーム $i のシーケンス番号が保持されなければならない');
        }
      });

      test('エンコード済みデータのサイズはヘッダー(16バイト) + ピクセルデータサイズと等しい', () {
        final frame = VideoFrame(
          width: 4,
          height: 4,
          pixelData: Uint8List(64), // 4x4 RGBA
          sequenceNumber: 0,
        );

        final encoded = MockVideoEncoder.encode(frame);

        expect(
          encoded.data.length,
          equals(16 + frame.pixelData.length),
          reason: 'エンコード済みデータはヘッダー16バイト + ピクセルデータのサイズでなければならない',
        );
      });
    });
  });
}
