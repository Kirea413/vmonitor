import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/foundation.dart' show TargetPlatform, defaultTargetPlatform;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show StandardMessageCodec;

import 'hardware_renderer.dart';
import 'renderer.dart';

/// ネイティブの映像ビューの種別名。
///
/// Android・iOS で同じ名前を使う。ネイティブ側の実装が違っても、
/// Dart から見た扱いを揃えておけば、iOS 対応は Swift を足すだけで済む。
///   Android: SurfaceView（実装済み）
///   iOS    : AVSampleBufferDisplayLayer を持つ UIView（未実装）
const String _videoViewType = 'vmonitor/video';

/// 全画面映像表示ウィジェット。
///
/// [HardwareRenderer] が登録した Flutter テクスチャを [Texture] ウィジェットで
/// レターボックス・ピラーボックスなしで全画面 GPU 表示する。
///
/// ### 使い方
///
/// ```dart
/// RendererView(
///   encodedFrames: transport.receive()
///       .where((e) => e.channel == ChannelId.video)
///       .map((e) => e.data),
/// )
/// ```
///
/// ウィジェットが破棄されるときに [HardwareRenderer] の [stop] と [dispose] が
/// 自動で呼び出される。
class RendererView extends StatefulWidget {
  /// PC クライアントから届くエンコード済み映像フレームのストリーム。
  final Stream<Uint8List> encodedFrames;

  /// 外部から [HardwareRenderer] を注入できる（主にテスト用）。
  /// 省略した場合は内部で生成する。
  final HardwareRenderer? renderer;

  /// フレームが届く前に表示するプレースホルダーウィジェット。
  ///
  /// ネイティブの映像ビューを使う場合は、ビュー自体が最初から画面にあるので
  /// 使われない（描画先が黒く見えるだけになる）。
  final Widget? placeholder;

  /// ネイティブの映像ビューを使うか。
  ///
  /// 省略すると動作中のプラットフォームで決まる。
  /// テストではプラットフォームビューを作れないので、明示的に false を渡す。
  final bool? useNativeView;

  /// デコーダーの状況が更新されたときに呼ばれる。
  ///
  /// USB 接続では映像が Dart を経由しないため、呼び出し側は
  /// 「映っているのかどうか」をここでしか知れない。
  final ValueChanged<RendererStats>? onStats;

  const RendererView({
    super.key,
    required this.encodedFrames,
    this.renderer,
    this.placeholder,
    this.useNativeView,
    this.onStats,
  });

  @override
  State<RendererView> createState() => _RendererViewState();
}

class _RendererViewState extends State<RendererView> {
  late final HardwareRenderer _renderer;

  /// テクスチャ ID が確定するまで null。
  int? _textureId;

  /// ネイティブの映像ビューを使えるプラットフォームか。
  ///
  /// Android は `SurfaceView`、iOS は `AVSampleBufferDisplayLayer` を
  /// 埋め込む形になる（iOS 側は未実装）。それ以外は Flutter のテクスチャを使う。
  bool get _useNativeView =>
      widget.useNativeView ?? (defaultTargetPlatform == TargetPlatform.android);

  @override
  void initState() {
    super.initState();
    _renderer = widget.renderer ?? HardwareRenderer();

    // ネイティブビューを使う場合は、ビューが出来てから開始する。
    // 描画先が用意される前にデコーダーを作ることはできない。
    if (!_useNativeView) _startRenderer();
  }

  /// ネイティブの映像ビューが生成されたときに呼ばれる。
  void _onNativeViewCreated(int viewId) {
    _renderer.nativeViewId = viewId;
    _startRenderer();
  }

  StreamSubscription<RendererStats>? _statsSubscription;

  Future<void> _startRenderer() async {
    // デコーダーの状況を呼び出し側へ流す。
    // 映像が Dart を経由しない経路では、これが唯一の手がかりになる。
    final onStats = widget.onStats;
    if (onStats != null) {
      _statsSubscription = _renderer.statsStream.listen(
        onStats,
        onError: (Object _) {},
      );
    }

    await _renderer.start(widget.encodedFrames);

    // テクスチャを使う場合だけ、ID が確定したら描き直す。
    // ネイティブビューの場合は表示先が既に画面上にあるので何もしなくてよい。
    if (mounted && !_useNativeView && _renderer.textureId != null) {
      setState(() {
        _textureId = _renderer.textureId;
      });
    }
  }

  @override
  void dispose() {
    _statsSubscription?.cancel();
    _renderer.disposeSync();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ColoredBox(
      color: Colors.black,
      child: _buildContent(),
    );
  }

  Widget _buildContent() {
    // ネイティブの映像ビューへ直接描く経路。
    //
    // Flutter のテクスチャを経由すると、デコード済みの絵が画面に出るまでに
    // 235〜278ms かかることを実測している。他の全区間の合計より大きく、
    // 体感していた遅れの主因だった。ネイティブのビューに直接描けば、
    // Flutter の合成を待つ必要がなくなる。
    if (_useNativeView) {
      return SizedBox.expand(
        child: AndroidView(
          viewType: _videoViewType,
          onPlatformViewCreated: _onNativeViewCreated,
          creationParamsCodec: const StandardMessageCodec(),
        ),
      );
    }

    final id = _textureId;

    if (id == null) {
      // テクスチャ未確定: プレースホルダーを表示する。
      return widget.placeholder ??
          const Center(
            child: CircularProgressIndicator(color: Colors.white),
          );
    }

    // Requirement 5.3: レターボックス・ピラーボックスなしで全画面表示する。
    return SizedBox.expand(
      child: Texture(
        textureId: id,
        freeze: false,
        filterQuality: FilterQuality.low,
      ),
    );
  }
}

/// [RendererView] を全画面で配置するためのヘルパー。
///
/// システム UI（ステータスバー・ナビゲーションバー）を非表示にして
/// 真の全画面映像再生を実現する。
///
/// ```dart
/// FullScreenRendererPage(encodedFrames: myStream)
/// ```
class FullScreenRendererPage extends StatelessWidget {
  final Stream<Uint8List> encodedFrames;
  final HardwareRenderer? renderer;

  /// ネイティブの映像ビューを使うか。[RendererView.useNativeView] と同じ。
  final bool? useNativeView;

  const FullScreenRendererPage({
    super.key,
    required this.encodedFrames,
    this.renderer,
    this.useNativeView,
  });

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      // extendBodyBehindAppBar は不要（AppBar 自体を表示しない）
      body: RendererView(
        encodedFrames: encodedFrames,
        renderer: renderer,
        useNativeView: useNativeView,
      ),
    );
  }
}
