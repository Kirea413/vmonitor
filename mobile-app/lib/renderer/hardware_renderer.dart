import 'dart:async';

import 'package:flutter/services.dart';

import 'renderer.dart';

/// プラットフォームチャンネル名 (iOS / Android 共通)。
///
/// iOS:     VideoToolbox ハードウェアデコーダーを呼び出す Swift/ObjC 実装に対応する。
/// Android: MediaCodec ハードウェアデコーダーを呼び出す Kotlin/Java 実装に対応する。
const String _kChannelName = 'vmonitor/renderer';

/// Flutter `Texture` ウィジェットでデコード済みフレームを GPU 表示する
/// ハードウェアレンダラーの実装。
///
/// ### テクスチャの登録フロー
///
/// テクスチャの登録はネイティブ側（iOS: FlutterTextureRegistry、
/// Android: TextureRegistry）で行われる。Dart 側はネイティブが返す
/// `textureId` を `Texture` ウィジェットに渡すだけでよい。
///
/// ### MethodChannel プロトコル
///
/// | メソッド           | 引数                                          | 戻り値           |
/// |--------------------|-----------------------------------------------|------------------|
/// | `initialize`       | なし                                          | `int` textureId  |
/// | `pushFrame`        | `{'textureId': int, 'data': Uint8List}`       | `null`           |
/// | `dispose`          | `{'textureId': int}`                          | `null`           |
///
/// ### EventChannel プロトコル (`vmonitor/renderer/stats`)
///
/// ネイティブ側から以下の Map が流れる（省略可）:
/// ```json
/// { "fps": 30.0, "decodeLatencyMs": 12 }
/// ```
class HardwareRenderer implements Renderer {
  /// ネイティブ側を呼び出すメソッドチャンネル。
  final MethodChannel _methodChannel;

  /// ネイティブ側の統計ストリームを受け取るイベントチャンネル。
  final EventChannel _statsEventChannel;

  /// ネイティブ側から割り当てられたテクスチャ ID（[start] 後に有効）。
  int? _textureId;

  /// エンコード済みフレームストリームの購読。
  StreamSubscription<Uint8List>? _frameSubscription;

  /// 統計ストリームのブロードキャストコントローラ。
  final StreamController<RendererStats> _statsController =
      StreamController<RendererStats>.broadcast();

  /// ネイティブ EventChannel の統計購読。
  StreamSubscription<dynamic>? _nativeStatsSubscription;

  /// 現在動作中かどうか。
  bool _running = false;

  // ── デコード統計の集計用フィールド ─────────────────────────────────

  /// 直近 N フレームのデコード開始〜完了時間 (ms) のリング。
  final List<int> _recentLatencies = [];

  /// fps 計算用: 直近 1 秒間のフレームカウント。
  int _frameCount = 0;

  /// fps 計算用: 現在の秒バケット。
  int _currentSecond = 0;

  /// 公開する fps の直近推定値。
  double _fps = 0.0;

  // ───────────────────────────────────────────────────────────────────

  HardwareRenderer({
    MethodChannel? methodChannel,
    EventChannel? statsEventChannel,
    bool enableNativeStats = false,
  })  : _methodChannel = methodChannel ??
            const MethodChannel(_kChannelName),
        _statsEventChannel = statsEventChannel ??
            const EventChannel('$_kChannelName/stats'),
        _enableNativeStats = enableNativeStats;

  /// ネイティブ統計イベントチャンネルを有効にするかどうか。
  /// テスト環境では EventChannel が使用できないため false に設定可能。
  final bool _enableNativeStats;

  // ─── Renderer インターフェース実装 ──────────────────────────────────

  @override
  Stream<RendererStats> get statsStream => _statsController.stream;

  /// エンコード済みフレームストリームの受信を開始してデコード・表示する。
  ///
  /// 1. ネイティブ側を `initialize` メソッドで起動し、textureId を受け取る。
  /// 2. フレームが来るたびに `pushFrame` でネイティブデコーダーへ転送する。
  /// 3. ネイティブ側の統計イベントを購読して [statsStream] に流す。
  /// ネイティブの映像ビューへ直接描く場合の、そのビューの ID。
  ///
  /// 指定するとデコーダーはそのビューへ直接描く。Flutter の合成を
  /// 経由しないぶん、表示までの遅れが大きく減る。
  /// 指定しない場合は Flutter のテクスチャへ描く（テスト用の控え）。
  int? nativeViewId;

  @override
  Future<void> start(Stream<Uint8List> encodedFrames) async {
    if (_running) return;
    _running = true;

    // デコーダーを初期化する。
    //
    // ネイティブの映像ビューがあればその ID を渡し、そこへ直接描かせる。
    // 無ければ Flutter のテクスチャを作ってもらい、その ID が返る。
    final id = await _methodChannel.invokeMethod<int>(
      'initialize',
      nativeViewId == null ? null : {'viewId': nativeViewId},
    );
    _textureId = id;
    // ignore: avoid_print
    print('[HardwareRenderer] initialized textureId=$id');

    // ネイティブ統計ストリームを購読する（利用可能な場合のみ）。
    if (_enableNativeStats) {
      try {
        _nativeStatsSubscription = _statsEventChannel
            .receiveBroadcastStream()
            .handleError(_onNativeStatsError)
            .listen(_onNativeStats, cancelOnError: false);
      } on MissingPluginException {
        // テスト環境では EventChannel が登録されていない場合があるため無視する。
      } catch (_) {
        // その他のエラー（プラットフォームチャンネル未登録など）は無視する。
      }
    }

    // フレームストリームを購読してデコーダーへ転送する。
    _frameSubscription = encodedFrames.listen(
      _onEncodedFrame,
      onError: _onFrameError,
      onDone: _onFrameDone,
    );
  }

  /// レンダリングを停止する。
  @override
  Future<void> stop() async {
    if (!_running) return;
    _running = false;

    await _frameSubscription?.cancel();
    _frameSubscription = null;

    // EventChannel の購読キャンセルは await しない。
    // テスト環境ではプラットフォームチャンネルのレスポンスが返らない場合があるため、
    // fire-and-forget で実行する。
    unawaited(_nativeStatsSubscription?.cancel() ?? Future<void>.value());
    _nativeStatsSubscription = null;

    final id = _textureId;
    _textureId = null;

    if (id != null) {
      try {
        await _methodChannel.invokeMethod<void>('dispose', {'textureId': id});
      } on MissingPluginException {
        // テスト環境では無視する。
      }
    }

    _recentLatencies.clear();
    _frameCount = 0;
    _fps = 0.0;
  }

  /// レンダラーを完全に破棄する。[stop] を内部で呼び出す。
  Future<void> dispose() async {
    await stop();
    if (!_statsController.isClosed) {
      await _statsController.close();
    }
  }

  /// Flutter の `State.dispose()` から呼び出すための同期的な後片付けメソッド。
  ///
  /// フレームストリームの購読を同期的にキャンセルし、
  /// 残りの非同期後片付けは [dispose] に委ねる。
  /// Flutter の `dispose()` は非同期を待てないため、このメソッドを使用する。
  ///
  /// EventChannel の cancel は非同期（プラットフォームチャンネル経由）のため、
  /// テスト環境でのデッドロックを避けるためここでは呼び出さない。
  void disposeSync() {
    if (!_running && _statsController.isClosed) return;

    _running = false;

    // フレームストリーム購読の参照を破棄する（cancel は非同期のため呼ばない）。
    // StreamController は GC 時に自動的にクリーンアップされる。
    _frameSubscription = null;

    // EventChannel 購読は cancel() を呼ばずに参照を破棄する。
    _nativeStatsSubscription = null;

    _textureId = null;
    _recentLatencies.clear();
    _frameCount = 0;
    _fps = 0.0;

    // statsController は close() が非同期のため、ここでは呼ばない。
    // dispose() で非同期にクリーンアップされる。
  }

  // ─── 内部ハンドラー ──────────────────────────────────────────────────

  /// エンコード済みフレームを受け取りネイティブデコーダーへ転送する。
  Future<void> _onEncodedFrame(Uint8List data) async {
    final id = _textureId;
    if (id == null) {
      // ignore: avoid_print
      print('[HardwareRenderer] _textureId is null, skipping frame');
      return;
    }

    // ignore: avoid_print
    if (data.length > 20) {
      // 最初の有効フレームだけログ
      print('[HardwareRenderer] pushFrame id=$id size=${data.length}');
    }

    final decodeStart = DateTime.now().millisecondsSinceEpoch;

    try {
      await _methodChannel.invokeMethod<void>('pushFrame', {
        'textureId': id,
        'data': data,
      });
    } on PlatformException catch (e) {
      // デコードエラーはスキップして継続する（フレーム欠落は許容）。
      if (!_statsController.isClosed) {
        _statsController.addError(e);
      }
      return;
    } on MissingPluginException {
      // テスト環境では無視する。
      return;
    }

    final decodeEnd = DateTime.now().millisecondsSinceEpoch;
    final latencyMs = decodeEnd - decodeStart;
    _updateStats(latencyMs);
  }

  /// ネイティブ側から配信される統計イベントを処理する。
  void _onNativeStats(dynamic event) {
    if (event is Map && !_statsController.isClosed) {
      final fps = (event['fps'] as num?)?.toDouble() ?? _fps;
      final latencyMs = (event['decodeLatencyMs'] as num?)?.toInt() ?? 0;
      _fps = fps;
      _statsController.add(RendererStats(fps: fps, decodeLatencyMs: latencyMs));
    }
  }

  void _onNativeStatsError(Object error) {
    // 統計エラーは無視してレンダリングを継続する。
  }

  void _onFrameError(Object error) {
    if (!_statsController.isClosed) {
      _statsController.addError(error);
    }
  }

  void _onFrameDone() {
    // フレームストリーム終了 → 停止処理。
    stop();
  }

  /// Dart 側でフレームレートとデコード遅延を集計して [statsStream] に流す。
  ///
  /// ネイティブ統計が利用できない環境（テスト・シミュレーター）でも
  /// 統計値を提供できるようにする。
  void _updateStats(int latencyMs) {
    // デコード遅延の移動平均（最大 30 サンプル）。
    _recentLatencies.add(latencyMs);
    if (_recentLatencies.length > 30) {
      _recentLatencies.removeAt(0);
    }
    final avgLatency = _recentLatencies.isEmpty
        ? 0
        : (_recentLatencies.reduce((a, b) => a + b) ~/ _recentLatencies.length);

    // fps: 1 秒バケットでカウントする。
    final nowMs = DateTime.now().millisecondsSinceEpoch;
    final nowSecond = nowMs ~/ 1000;
    if (nowSecond != _currentSecond) {
      _fps = _frameCount.toDouble();
      _frameCount = 0;
      _currentSecond = nowSecond;
    }
    _frameCount++;

    if (!_statsController.isClosed) {
      _statsController.add(RendererStats(
        fps: _fps,
        decodeLatencyMs: avgLatency,
      ));
    }
  }

  /// 現在登録されているテクスチャ ID を返す。
  /// [start] 呼び出し前または [stop] 後は `null`。
  int? get textureId => _textureId;
}
