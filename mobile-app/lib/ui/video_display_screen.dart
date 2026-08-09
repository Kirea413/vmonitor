import 'dart:async';
import 'dart:convert';
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../renderer/renderer.dart';
import '../renderer/renderer_view.dart';
import '../transport/aoa_transport.dart';
// Orientation / Resolution が Flutter の同名型と衝突するため接頭辞を付ける
import '../touch/touch_input_proxy.dart' as touch;
import '../transport/transport.dart';
import '../transport/wifi_transport.dart';
import 'display_preferences.dart';
import 'draggable_settings_button.dart';
import 'settings_screen.dart';

/// 全画面映像表示画面。
///
/// 接続済みの [Transport] を受け取り、映像フレームを [RendererView] に流す。
/// Wi-Fi でも USB 直結でも扱いは同じなので、具体的な種類は問わない。
/// 再接続は行わない（既に接続済み）。
class VideoDisplayScreen extends StatefulWidget {
  /// 接続先のデバイス情報。
  final MdnsServiceRecord device;

  /// 接続済みのトランスポート（DeviceDiscoveryScreen で確立済み）。
  final Transport transport;

  const VideoDisplayScreen({
    super.key,
    required this.device,
    required this.transport,
  });

  @override
  State<VideoDisplayScreen> createState() => _VideoDisplayScreenState();
}

class _VideoDisplayScreenState extends State<VideoDisplayScreen>
    with WidgetsBindingObserver {
  StreamController<Uint8List>? _videoStreamController;
  StreamSubscription<({ChannelId channel, Uint8List data})>? _receiveSubscription;

  bool _connecting = true;
  String? _errorMessage;

  /// 直前に PC へ知らせた画面サイズ。同じ内容を送り直さないための控え。
  Size? _lastReportedSize;

  /// 回転直後の細かな変化で何度も送らないための待ち合わせ。
  Timer? _metricsDebounce;

  /// この端末の呼び名。一度引ければ変わらないので覚えておく。
  String? _deviceName;

  /// タッチ入力を PC へ転送するプロキシ。
  final touch.FlutterTouchInputProxy _touchProxy = touch.FlutterTouchInputProxy();

  // デバッグ用カウンター
  int _totalFrames = 0;
  int _videoFrames = 0;
  int _lastFrameSize = 0;
  String _lastChannel = '-';

  // デコーダーから報告される状況（USB 接続ではこちらが実態を表す）
  double _decoderFps      = 0;
  int    _decodeLatencyMs = 0;

  @override
  void initState() {
    super.initState();
    _videoStreamController = StreamController<Uint8List>.broadcast();

    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);

    // 画面の向きが変わったことを受け取るために登録する
    WidgetsBinding.instance.addObserver(this);

    // 設定画面で余白や表示の切り替えを変えたら、その場で反映する
    displayPreferences.addListener(_onPreferencesChanged);

    // タッチイベントを PC へ送れるようトランスポートを繋ぐ
    _touchProxy.attach(widget.transport);

    _startStreaming();
  }

  @override
  void dispose() {
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);

    WidgetsBinding.instance.removeObserver(this);
    displayPreferences.removeListener(_onPreferencesChanged);
    _metricsDebounce?.cancel();

    _touchProxy.detach();
    _touchProxy.dispose();

    _receiveSubscription?.cancel();
    _videoStreamController?.close();
    widget.transport.disconnect();
    super.dispose();
  }

  void _onPreferencesChanged() {
    if (mounted) setState(() {});
  }

  /// 切断されたときに見せる説明を組み立てる。
  ///
  /// 「切断されました」だけだと、ケーブルが抜けたのか PC 側が終了したのか
  /// 分からず、次に何をすればよいか判断できない。分かる範囲で理由を添える。
  String _describeDisconnect() {
    final transport = widget.transport;

    if (transport is AoaTransport) {
      final reason = transport.disconnectReason;

      return reason == null || reason.isEmpty
          ? 'USB 接続が切断されました。\nケーブルを挿し直すと再接続できます。'
          : 'USB 接続が切断されました。\n$reason';
    }

    return '接続が切断されました。\nPC 側の vmonitor が動いているか確認してください。';
  }

  /// デコーダーの状況を受け取る。
  ///
  /// USB 接続では映像が Dart を経由しないため、こちらの受信カウンターは
  /// 増えない。実際に映っているかどうかはここでしか分からない。
  void _onRendererStats(RendererStats stats) {
    if (!mounted) return;
    if (!displayPreferences.showDebugOverlay) {
      // 表示していないなら描き直す必要はない
      _decoderFps      = stats.fps;
      _decodeLatencyMs = stats.decodeLatencyMs;
      return;
    }

    setState(() {
      _decoderFps      = stats.fps;
      _decodeLatencyMs = stats.decodeLatencyMs;
    });
  }

  /// 画面の寸法が変わったときに呼ばれる（端末の回転など）。
  ///
  /// PC 側の仮想ディスプレイは 1 つの解像度しか持たないので、
  /// 向きが変わったら知らせて作り直してもらう。
  /// 知らせないと縦横比が合わず、映像に帯が出る。
  @override
  void didChangeMetrics() {
    super.didChangeMetrics();

    // 回転の途中は寸法が何度も変わる。落ち着いてから 1 回だけ送る。
    _metricsDebounce?.cancel();
    _metricsDebounce = Timer(const Duration(milliseconds: 400), _sendHello);
  }

  /// 既に接続済みのトランスポートから映像ストリームを開始する。
  Future<void> _startStreaming() async {
    // ignore: avoid_print
    print('[VideoDisplay] _startStreaming called');
    try {
      _receiveSubscription = widget.transport
          .receive()
          .listen(
            (e) {
              // PC から名乗りを求められたら答える。
              // 繋がった直後に自分から送った 1 通は、PC がまだ受信を
              // 始めていないと届かないことがあるため。
              if (e.channel == ChannelId.control) {
                _handleControlMessage(e.data);
              }

              if (mounted) {
                setState(() {
                  _lastChannel = e.channel.name;
                  _totalFrames++;
                  if (e.channel == ChannelId.video) {
                    _videoFrames++;
                    _lastFrameSize = e.data.length;
                    _videoStreamController?.add(e.data);
                    if (_videoFrames == 1) {
                      // ignore: avoid_print
                      print('[VideoDisplay] first video frame: ${e.data.length} bytes');
                    }
                  }
                });
              }
            },
            onError: (Object err) {
              if (mounted) {
                setState(() => _errorMessage = 'ストリームエラー: $err');
              }
            },
            onDone: () {
              if (mounted) {
                setState(() => _errorMessage = _describeDisconnect());
              }
            },
          );

      // 受信を張ってから名乗る。
      // PC はこれを受けて、この端末の画面に合わせた仮想ディスプレイを作る。
      await _sendHello();

      if (mounted) {
        setState(() => _connecting = false);
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          _connecting = false;
          _errorMessage = '映像ストリームの開始に失敗しました: $e';
        });
      }
    }
  }

  /// 制御チャンネルのメッセージを処理する。
  void _handleControlMessage(Uint8List data) {
    try {
      final message = jsonDecode(utf8.decode(data));
      if (message is! Map) return;

      if (message['type'] == 'hello_request') {
        // 求められたときは、前と同じ大きさでも必ず答える
        _sendHello(force: true);
        return;
      }

      // 往復時間の計測。受け取ったらそのまま返す。
      // 制御メッセージは映像と同じ経路を通るので、映像が詰まっていれば
      // これも同じだけ遅れて届く。PC 側でその差を測っている。
      if (message['type'] == 'ping') {
        final t = message['t'];
        widget.transport.send(
          Uint8List.fromList(utf8.encode(jsonEncode({'type': 'pong', 't': t}))),
          ChannelId.control,
        );
        return;
      }
    } catch (_) {
      // 解釈できない制御メッセージは無視する
    }
  }

  /// この端末の画面の大きさを PC へ知らせる。
  ///
  /// PC 側は仮想ディスプレイを作る前にこれを待っている。
  /// 送らないと、PC は既定の解像度で作ってしまい、
  /// 映像に帯が出たり引き伸ばされたりする。
  ///
  /// 送るのは論理ピクセルではなく実ピクセル。
  /// 仮想ディスプレイはそのまま Windows の画面サイズになるため、
  /// 端末の実際の画素数と合っている必要がある。
  Future<void> _sendHello({bool force = false}) async {
    try {
      final view = PlatformDispatcher.instance.views.first;
      final size = view.physicalSize;

      // 同じ大きさを繰り返し伝えない。
      // PC 側は知らせを受けるたびに仮想ディスプレイを作り直すので、
      // 中身が変わっていないのに送ると画面が無駄に一瞬消える。
      if (!force && _lastReportedSize == size) return;
      _lastReportedSize = size;

      // 端末の呼び名も添える。PC 側の一覧に出すため。
      // 取れなくても接続には差し支えないので、待たずに使えるぶんだけ使う。
      _deviceName ??= await AoaTransport.deviceName();

      final payload = jsonEncode({
        'type': 'hello',
        'width': size.width.round(),
        'height': size.height.round(),
        'devicePixelRatio': view.devicePixelRatio,
        if (_deviceName != null) 'name': _deviceName,
      });

      await widget.transport.send(
        Uint8List.fromList(utf8.encode(payload)),
        ChannelId.control,
      );

      // ignore: avoid_print
      print('[VideoDisplay] hello sent: ${size.width.round()}x${size.height.round()}');
    } catch (e) {
      // 名乗れなくても映像は出る（PC 側は既定の解像度を使う）
      // ignore: avoid_print
      print('[VideoDisplay] hello failed: $e');
    }
  }

  void _openSettings() {
    Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => SettingsScreen(
          transport: widget.transport,
          // 設定画面ごと閉じてから映像画面を閉じる。
          // 設定画面だけ閉じると、戻る手が無い映像画面に取り残される。
          onExit: () {
            Navigator.of(context)
              ..pop()   // 設定画面
              ..pop();  // 映像画面
          },
        ),
      ),
    );
  }

  /// 切断してよいか尋ねる。
  ///
  /// ジェスチャーは意図せず出ることがある。確認を挟まないと、
  /// 作業中に画面が消えて理由が分からない、ということになる。
  bool _disconnectAsked = false;

  Future<void> _confirmDisconnect() async {
    if (!mounted) return;
    if (_disconnectAsked) return;   // 二重に出さない

    _disconnectAsked = true;

    final leave = await showDialog<bool>(
          context: context,
          barrierDismissible: true,
          builder: (dialogContext) => AlertDialog(
            icon: const Icon(Icons.link_off, size: 32),
            title: const Text('接続を切りますか？'),
            content: const Text('PC との接続を切ってホーム画面に戻ります。'),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(dialogContext).pop(false),
                child: const Text('続ける'),
              ),
              FilledButton(
                onPressed: () => Navigator.of(dialogContext).pop(true),
                child: const Text('切断'),
              ),
            ],
          ),
        ) ??
        false;

    _disconnectAsked = false;

    if (leave) _goHome();
  }

  /// ホーム（探索画面）へ戻る。
  ///
  /// 映像画面は全画面で戻るボタンが無く、ジェスチャーの戻るも
  /// 映像の上のタッチ領域と取り合いになる。押せる形で置いておく。
  void _goHome() {
    if (!mounted) return;
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      // 設定ボタンは映像の上に重ねる。
      // 映像を隠してしまうので、ドラッグで動かせて、長押しで隠せるようにしてある。
      body: Stack(
        children: [
          _buildBody(),
          if (!_connecting && _errorMessage == null)
            DraggableSettingsButton(onPressed: _openSettings),
        ],
      ),
    );
  }

  Widget _buildBody() {
    if (_connecting) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const CircularProgressIndicator(color: Colors.white),
            const SizedBox(height: 16),
            const Text('映像ストリームを開始しています…',
                style: TextStyle(color: Colors.white)),
            const SizedBox(height: 24),
            // ここで止まったままになることがある（PC 側が応答しないなど）。
            // 抜ける手を用意しておかないとアプリを終了するしかなくなる。
            TextButton(
              onPressed: _goHome,
              child: const Text('ホームに戻る',
                  style: TextStyle(color: Colors.white70)),
            ),
          ],
        ),
      );
    }

    final error = _errorMessage;
    if (error != null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.error_outline, size: 64, color: Colors.red),
              const SizedBox(height: 16),
              Text(error,
                  textAlign: TextAlign.center,
                  style: const TextStyle(color: Colors.white)),
              const SizedBox(height: 24),
              ElevatedButton.icon(
                icon: const Icon(Icons.home_outlined),
                label: const Text('ホームに戻る'),
                onPressed: _goHome,
              ),
            ],
          ),
        ),
      );
    }

    return OrientationBuilder(
      builder: (context, orientation) {
        // 端末の向きをタッチプロキシへ伝える（イベントに向きを載せるため）
        _touchProxy.updateOrientation(
          orientation == Orientation.portrait
              ? touch.Orientation.portrait
              : touch.Orientation.landscape,
        );

        return Stack(
          children: [
            // 映像とタッチ領域をまとめて内側に寄せる。
            //
            // 画面の縁（丸みのある角、ジェスチャー操作に使われる帯）は
            // 狙って触りにくい。余白を取ってそこを避ける。
            //
            // 触れる範囲だけ狭めるのではなく映像ごと寄せるのは、
            // そうしないと PC 画面の端に永遠に届かなくなるため。
            // タッチは表示領域の大きさで正規化されるので、
            // 寄せた範囲がそのまま PC 画面全体に対応する。
            Padding(
              padding: displayPreferences.insets,
              child: touch.TouchInputView(
                proxy: _touchProxy,
                // 3 本指で下へ払うと切断。
                // 画面いっぱいが PC の入力面なので、ボタンを置くと必ず邪魔になる。
                onDisconnectGesture: _confirmDisconnect,
                gesture: displayPreferences.disconnectGesture,
                child: RendererView(
                  encodedFrames: _videoStreamController!.stream,
                  placeholder: const Center(
                    child: CircularProgressIndicator(color: Colors.white),
                  ),
                  // USB 接続では映像が Dart を経由しないので、
                  // 映っているかどうかはデコーダーの状況でしか分からない。
                  onStats: _onRendererStats,
                ),
              ),
            ),

            // 切り分け用の情報。表示するかどうかは設定だけで決まる。
            if (displayPreferences.showDebugOverlay)
              Positioned(
                top: 8,
                left: 8,
                child: Container(
                  padding: const EdgeInsets.all(8),
                  color: Colors.black54,
                  child: Text(
                    _decoderFps > 0
                        ? '表示中 ${_decoderFps.toStringAsFixed(1)} fps  '
                          'デコード ${_decodeLatencyMs}ms\n'
                          '受信: $_totalFrames pkt  最終ch: $_lastChannel'
                        : '映像を待機中\n'
                          '受信: $_totalFrames pkt  映像: $_videoFrames frm\n'
                          '最終ch: $_lastChannel  サイズ: $_lastFrameSize B',
                    style: const TextStyle(color: Colors.white, fontSize: 11),
                  ),
                ),
              ),
          ],
        );
      },
    );
  }
}
