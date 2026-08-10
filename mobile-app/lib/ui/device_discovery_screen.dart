import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../transport/aoa_transport.dart';
import '../transport/connect_protocol.dart';
import '../transport/transport.dart';
import '../transport/usb_transport.dart';
import '../transport/wifi_listen_transport.dart';
import '../transport/wifi_transport.dart';

/// デバイス探索・接続画面
///
/// 要件:
/// - 2.1: mDNS で同一 Wi-Fi 上の PC を自動検出して候補リストに表示する
/// - 2.2: USB 接続時は自動検出してセッション確立を試みる
/// - 2.3: ユーザーが候補を選択したら 10 秒以内にセッションを確立する
/// - 2.4: タイムアウト時にユーザーへ通知し再試行オプションを表示する
class DeviceDiscoveryScreen extends StatefulWidget {
  /// 接続成功時のコールバック。接続済みトランスポートとデバイス情報を渡す。
  ///
  /// 映像画面を閉じるまで完了しない [Future] を返してもらう。
  /// この画面はそれを合図に待ち受け状態へ戻る。
  final Future<void>? Function(Transport transport, MdnsServiceRecord device)?
      onConnected;

  const DeviceDiscoveryScreen({super.key, this.onConnected});

  @override
  State<DeviceDiscoveryScreen> createState() => _DeviceDiscoveryScreenState();
}

enum _ScreenState {
  discovering,
  idle,
  connecting,

  /// こちらから頼んで、PC 側の承認を待っている。
  waitingApproval,

  timedOut,
  connected,
}

class _DeviceDiscoveryScreenState extends State<DeviceDiscoveryScreen> {
  static const Duration _discoveryTimeout = Duration(seconds: 5);
  static const Duration _connectionTimeout = Duration(seconds: 10);

  _ScreenState _screenState = _ScreenState.idle;
  List<MdnsServiceRecord> _devices = [];
  MdnsServiceRecord? _connectingDevice;

  Timer? _connectionTimer;

  // 手動IP入力フォーム用
  final _ipController = TextEditingController(text: '');
  final _portController = TextEditingController(text: '7979');

  /// USB 直結 (AOA) で PC が繋がっているか。
  bool _usbAttached = false;

  /// この接続で使うトランスポートを作る関数。
  /// USB と Wi-Fi で作り方が違うので、接続を始めるときに決めておく。
  Transport Function() _transportFactory = WifiTransport.new;

  StreamSubscription<({String state, String? detail})>? _usbStateSubscription;

  /// USB の状態を定期的に見に行くためのタイマー。
  ///
  /// 「繋がった」の通知だけに頼ると取りこぼす。先にこのアプリを開いて
  /// あとから PC 側を起動した場合、端末がアクセサリーモードへ切り替わっても
  /// 通知が届かないことがあり、いつまでも繋がらないように見える。
  Timer? _usbPollTimer;

  // 以前あった「自動接続を抑止する札」は要らなくなった。
  // 挿しただけでは映像を始めず、どちらかのボタンと相手側の承認を
  // 待つようにしたため、勝手に繋ぎ直されることがなくなっている。

  // ── PC からの接続を待ち受ける ─────────────────────────────────

  /// PC からの接続を受けるための待ち受け。
  ///
  /// スマホから探しにいく向きだけだと、PC の前にいるときに
  /// いちいちスマホを手に取らないと始められない。
  WifiListenTransport? _listener;

  /// 待ち受けているポート（画面に出す）。
  int? _listeningPort;

  /// この端末の IP アドレス（PC 側で入力してもらう値）。
  List<String> _localAddresses = const [];

  /// 待ち受けを始められなかった理由。
  String? _listenError;

  @override
  void initState() {
    super.initState();

    // 起動時は idle 状態（手動IP入力フォームを表示）
    // ユーザーが「Wi-Fi 検索」ボタンを押したら mDNS 探索を開始する

    _autoConnectUsbIfAttached();

    // ケーブルの抜き差しに追従する。
    // 挿されたら通信路だけ開けて待つ（映像はまだ始めない）。
    _usbStateSubscription = AoaTransport.stateChanges().listen(
      (event) {
        if (event.state == 'attached') {
          _autoConnectUsbIfAttached();
        } else {
          _refreshUsbState();
        }
      },
      onError: (Object _) {},
    );

    // 通知が来なくても気づけるように、自分でも見に行く。
    _usbPollTimer = Timer.periodic(
      const Duration(seconds: 1),
      (_) {
        _autoConnectUsbIfAttached();
        // Wi-Fi に後から繋いだ場合、アドレスはあとから生える
        _refreshLocalAddresses();
        // PC 側の vmonitor が生きているかを見張る
        _probePc();
      },
    );

    // PC から繋いでもらう向きも受けられるようにしておく
    _startListening();
  }

  // ── PC からの接続を待ち受ける ─────────────────────────────────

  /// 待ち受けを始め、PC が繋いできたらそのまま映像画面へ進む。
  ///
  /// 待ち受けは「この画面にいる間」だけ。映像を表示している間は
  /// 相手が決まっているので、別の PC からの割り込みを受ける必要がない。
  Future<void> _startListening() async {
    // 既に待っているなら二重に張らない
    if (_listener != null) return;

    final listener = WifiListenTransport();
    _listener = listener;

    try {
      await listener.startListening();
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _listener = null;
        _listeningPort = null;
        _listenError = '待ち受けを開始できませんでした: $e';
      });
      return;
    }

    final addresses = await WifiListenTransport.localAddresses();

    if (!mounted) {
      await listener.disconnect();
      return;
    }

    setState(() {
      _listeningPort = listener.listeningPort;
      _localAddresses = addresses;
      _listenError = null;
    });

    // 繋がるのを待つ。ここは画面を止めない。
    unawaited(_awaitIncoming(listener));
  }

  /// 自機のアドレス表示を最新にする。
  ///
  /// アプリを開いたあとに Wi-Fi へ繋いだ場合、起動時に読んだ一覧は空のまま。
  /// 表示が空だと「対応していない」と受け取られてしまう。
  Future<void> _refreshLocalAddresses() async {
    if (_listeningPort == null) return;   // 待ち受けていないなら出す必要がない

    final addresses = await WifiListenTransport.localAddresses();

    if (!mounted) return;
    if (addresses.length == _localAddresses.length &&
        addresses.every(_localAddresses.contains)) {
      return;   // 変わっていないなら描き直さない
    }

    setState(() => _localAddresses = addresses);
  }

  Future<void> _awaitIncoming(WifiListenTransport listener) async {
    final remote = await listener.acceptOne();

    // 相手が来ないまま畳んだ（画面を離れた、別の経路で接続が始まった）
    if (remote == null) return;

    if (!mounted) {
      await listener.disconnect();
      return;
    }

    // 別の経路で既に接続処理が始まっていたら、こちらは引き取らない
    if (_screenState != _ScreenState.idle) {
      await listener.disconnect();
      if (_listener == listener) _listener = null;
      _restartListeningSoon();
      return;
    }

    // この待ち受けはもう映像画面のものになる。
    // 画面を離れるときにこちらから切らないよう、手放しておく。
    _listener = null;
    _listeningPort = null;

    final device = MdnsServiceRecord(
      serviceName: 'PC ($remote)',
      hostName: remote,
      port: listener.listeningPort ?? WifiListenTransport.defaultPort,
      ipAddress: remote,
    );

    // 既に繋がっているので、改めて接続しにいく必要はない
    _connect(device, connected: listener);
  }

  /// PC 側の vmonitor が応答しているか。
  ///
  /// ケーブルが挿さっていることと、相手で vmonitor が動いていることは別。
  /// **PC 側のアプリを閉じても、端末はアクセサリーモードのまま残る。**
  /// そのため「アクセサリーが見えている」だけを根拠にすると、
  /// 相手が居ないのに「USB で接続」が押せてしまう。
  bool _pcAlive = false;

  /// 最後に PC から何か届いた時刻。
  DateTime? _lastPcMessage;

  /// これだけ音沙汰が無ければ、PC 側は動いていないとみなす。
  static const Duration _pcSilenceLimit = Duration(seconds: 8);

  /// PC が生きているか確かめる。
  ///
  /// 開いている通信路へ問いかけ、返事があるかを見る。
  Future<void> _probePc() async {
    final link = _usbLink;

    if (link == null) {
      if (_pcAlive && mounted) setState(() => _pcAlive = false);
      return;
    }

    try {
      await link.send(
        Uint8List.fromList(utf8.encode(jsonEncode({'type': 'ping', 't': 0}))),
        ChannelId.control,
      );
    } catch (_) {
      // 送れない＝相手が居ない
      if (_pcAlive && mounted) setState(() => _pcAlive = false);
      return;
    }

    final last = _lastPcMessage;
    final alive = last != null &&
        DateTime.now().difference(last) < _pcSilenceLimit;

    if (alive == _pcAlive) return;
    if (!mounted) return;

    setState(() => _pcAlive = alive);
  }

  /// 待ち受けを畳んで、また張り直す。
  void _restartListeningSoon() {
    Timer(const Duration(milliseconds: 300), () {
      if (!mounted) return;
      if (_screenState != _ScreenState.idle) return;
      _startListening();
    });
  }

  /// 待ち受けを畳む。
  Future<void> _stopListening() async {
    final listener = _listener;
    _listener = null;

    if (!mounted) {
      await listener?.disconnect();
      return;
    }

    setState(() => _listeningPort = null);
    await listener?.disconnect();
  }

  /// USB が繋がっていれば、通信できる状態にして待つ。
  ///
  /// ここで映像まで始めないのが要。ケーブルが挿さっていることと、
  /// 画面を映してよいことは別のできごとで、後者は人が決める。
  ///
  /// ただし通信路は先に開けておく。開いていないと、PC 側で「接続」を
  /// 押されても、その要求がこの端末に届かない。
  Future<void> _autoConnectUsbIfAttached() async {
    // 接続中や表示中に割り込まない
    if (_screenState != _ScreenState.idle) return;

    await _refreshUsbState();

    if (!mounted) return;
    if (!_usbAttached) return;
    if (_screenState != _ScreenState.idle) return;

    await _openUsbLink();
  }

  // ── USB の通信路を開けて待つ ────────────────────────────────

  /// 待機中に開いておく USB の通信路。
  ///
  /// これを開いたままにしておくことで、PC 側で「接続」を押されたときに
  /// その要求を受け取って、この端末に承認を出せる。
  AoaTransport? _usbLink;

  /// 通信路の制御チャンネルの購読。
  StreamSubscription<({ChannelId channel, Uint8List data})>? _usbControlSub;

  /// PC の承認を待っている間の待ち合わせ。
  Completer<bool>? _pendingApproval;

  /// 承認のダイアログを二重に出さないための札。
  bool _approvalDialogOpen = false;

  /// USB の通信路を開き、PC からの要求を聞けるようにする。
  Future<void> _openUsbLink() async {
    if (_usbLink != null) return;   // 既に開いている

    final link = AoaTransport();

    try {
      await link.connect('usb', 0);
    } catch (_) {
      // まだ PC 側が掴んでいないなど。次の巡回でまた試す。
      return;
    }

    if (!mounted) {
      await link.disconnect();
      return;
    }

    _usbLink = link;

    _usbControlSub = link.receive().listen(
      (e) {
        if (e.channel == ChannelId.control) _onUsbControlMessage(e.data);
      },
      onError: (Object _) {},
      onDone: _onUsbLinkClosed,
    );

    // 繋がった時点で名乗る。
    //
    // これまで名乗りは映像画面でしか送っておらず、PC の一覧には
    // 接続するまで「Android 端末」としか出なかった。選ぶ側からすれば、
    // どれが自分の端末なのか分からない。
    unawaited(_announceSelf(link));

    setState(() {});
  }

  /// この端末の呼び名を相手に伝える。
  Future<void> _announceSelf(Transport link) async {
    try {
      final name = await AoaTransport.deviceName();
      if (name == null || name.isEmpty) return;

      await link.send(
        Uint8List.fromList(utf8.encode(jsonEncode({
          'type': 'hello',
          'name': name,
        }))),
        ChannelId.control,
      );
    } catch (_) {
      // 名乗れなくても接続そのものには関係ない
    }
  }

  void _onUsbLinkClosed() {
    if (!mounted) return;

    _usbControlSub?.cancel();
    _usbControlSub = null;
    _usbLink = null;

    // 待っている人がいれば起こす
    final pending = _pendingApproval;
    if (pending != null && !pending.isCompleted) pending.complete(false);
    _pendingApproval = null;

    setState(() {});
  }

  /// 通信路を畳む。映像画面へ引き継ぐときは呼ばない。
  Future<void> _closeUsbLink() async {
    final link = _usbLink;
    _usbLink = null;

    await _usbControlSub?.cancel();
    _usbControlSub = null;

    await link?.disconnect();
  }

  /// PC から届いた制御メッセージを処理する。
  void _onUsbControlMessage(Uint8List data) {
    // 何が届いても、PC が動いている証にはなる。
    _lastPcMessage = DateTime.now();
    if (!_pcAlive && mounted) setState(() => _pcAlive = true);

    final message = ConnectProtocol.parse(data);
    if (message == null) return;

    // PC 側で「接続」が押された。この端末で承認を取る。
    if (message.isRequest && message.fromPc) {
      _askUserToApprove();
      return;
    }

    // こちらから頼んだ結果が返ってきた。
    if (message.isResponse) {
      final pending = _pendingApproval;
      if (pending != null && !pending.isCompleted) {
        pending.complete(message.accepted ?? false);
      }
      _pendingApproval = null;
    }
  }

  /// PC からの接続要求を利用者に見せて、返事を PC へ返す。
  Future<void> _askUserToApprove() async {
    if (!mounted) return;
    if (_approvalDialogOpen) return;       // 二重に出さない
    if (_screenState != _ScreenState.idle) return;

    _approvalDialogOpen = true;

    final approved = await showDialog<bool>(
          context: context,
          barrierDismissible: false,
          builder: (dialogContext) => AlertDialog(
            icon: const Icon(Icons.desktop_windows, size: 32),
            title: const Text('PC から接続の要求'),
            content: const Text(
              'PC がこの端末に画面を映そうとしています。\n許可しますか？',
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.of(dialogContext).pop(false),
                child: const Text('拒否'),
              ),
              FilledButton(
                onPressed: () => Navigator.of(dialogContext).pop(true),
                child: const Text('許可'),
              ),
            ],
          ),
        ) ??
        false;

    _approvalDialogOpen = false;

    final link = _usbLink;
    if (link == null) return;

    try {
      await link.send(
        ConnectProtocol.response(accepted: approved),
        ChannelId.control,
      );
    } catch (_) {
      // 返事が送れないなら、もう繋がっていない
      return;
    }

    if (!approved || !mounted) return;

    // 承認したので、この通信路のまま映像へ進む
    _startSessionOnUsbLink();
  }

  /// 開いてある通信路をそのまま使ってセッションを始める。
  void _startSessionOnUsbLink() {
    final link = _usbLink;
    if (link == null) return;

    // 通信路は映像画面のものになる。こちらからは畳まない。
    _usbControlSub?.cancel();
    _usbControlSub = null;
    _usbLink = null;

    const device = MdnsServiceRecord(
      serviceName: 'PC (USB 直結)',
      hostName: 'usb',
      port: 0,
      ipAddress: 'usb',
    );

    _connect(device, connected: link);
  }

  @override
  void dispose() {
    _connectionTimer?.cancel();
    _usbPollTimer?.cancel();
    _usbStateSubscription?.cancel();

    // 待ち受けたままにするとポートを掴んだままになり、
    // 次に開いたときに待ち受けを始められない。
    final listener = _listener;
    _listener = null;
    unawaited(listener?.disconnect() ?? Future<void>.value());

    // USB も掴んだままにしない
    unawaited(_closeUsbLink());

    _ipController.dispose();
    _portController.dispose();
    super.dispose();
  }

  Future<void> _refreshUsbState() async {
    final attached = await AoaTransport.isAttached();

    if (!mounted) return;
    if (attached == _usbAttached) return;   // 変わっていないなら描き直さない

    setState(() => _usbAttached = attached);
  }

  // ── デバイス探索 ────────────────────────────────────────────

  Future<void> _startDiscovery() async {
    setState(() {
      _screenState = _ScreenState.discovering;
      _devices = [];
    });

    List<MdnsServiceRecord> discovered = [];
    try {
      discovered = await WifiTransport.discoverServices(
        timeout: _discoveryTimeout,
      ).timeout(
        const Duration(seconds: 10),
        onTimeout: () => <MdnsServiceRecord>[],
      );
    } catch (e) {
      // mDNS 探索失敗はクラッシュさせない（SocketException 含む）
      discovered = [];
    }

    if (!mounted) return;

    setState(() {
      _devices = discovered;
      _screenState = _ScreenState.idle;
    });
  }

  // ── USB 接続 ─────────────────────────────────────────────────

  /// USB 直結 (AOA) で接続する。
  ///
  /// PC 側がこの端末をアクセサリーモードへ切り替えると、
  /// バルクエンドポイント越しに直接やり取りできる。
  /// adb も Wi-Fi も要らず、開発者オプションを有効にする必要もない。
  /// この端末から接続を申し込み、PC 側の承認を待つ。
  ///
  /// 押した側の反対で承認を取る。PC を触っている人に断りなく
  /// その画面を持っていかないため。
  Future<void> _connectUsbDirect() async {
    await _openUsbLink();

    final link = _usbLink;

    if (link == null) {
      if (!mounted) return;
      setState(() => _screenState = _ScreenState.timedOut);
      return;
    }

    final pending = Completer<bool>();
    _pendingApproval = pending;

    setState(() => _screenState = _ScreenState.waitingApproval);

    try {
      await link.send(
        ConnectProtocol.request(ConnectProtocol.initiatorPhone),
        ChannelId.control,
      );
    } catch (_) {
      _pendingApproval = null;
      if (!mounted) return;
      setState(() => _screenState = _ScreenState.timedOut);
      return;
    }

    // 待ちっぱなしにしない。PC の前に人がいないこともある。
    bool approved;
    try {
      approved = await pending.future.timeout(_approvalTimeout);
    } catch (_) {
      approved = false;
    }

    _pendingApproval = null;

    if (!mounted) return;

    if (!approved) {
      setState(() => _screenState = _ScreenState.idle);

      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('PC 側で許可されませんでした。'),
        ),
      );
      return;
    }

    setState(() => _screenState = _ScreenState.idle);
    _startSessionOnUsbLink();
  }

  /// 相手の承認を待つ上限。
  static const Duration _approvalTimeout = Duration(seconds: 60);

  /// USB 接続（ADB リバース経由）を試みる。
  ///
  /// AOA が使えない環境向けの逃げ道として残してある。
  ///
  /// PC 側で `adb reverse tcp:7979 tcp:7979` を実行しておくと、
  /// スマホの localhost:7979 が PC の 7979 に転送され、PC に繋がる。
  ///
  /// `adb forward` ではない点に注意。forward は「PC から端末へ」の向きで、
  /// ここで必要なのは「端末から PC へ」なので reverse を使う。
  void _connectUsbAdb() {
    const device = MdnsServiceRecord(
      serviceName: 'PC (USB / ADB)',
      hostName: 'localhost',
      port: 7979,
      ipAddress: '127.0.0.1',
    );

    _transportFactory = UsbTransport.new;
    _connect(device);
  }

  // ── 手動IP接続 ────────────────────────────────────────────────

  void _connectManual() {
    final host = _ipController.text.trim();
    final portStr = _portController.text.trim();
    if (host.isEmpty) return;

    final port = int.tryParse(portStr) ?? 7979;
    final device = MdnsServiceRecord(
      serviceName: 'PC ($host)',
      hostName: host,
      port: port,
      ipAddress: host,
    );

    _transportFactory = WifiTransport.new;
    _connect(device);
  }

  // ── 接続処理 ─────────────────────────────────────────────────

  /// 接続処理へ進む。
  ///
  /// [connected] を渡すと、そのトランスポートは既に繋がっているものとして
  /// 扱う（PC 側から繋いでもらった場合）。
  void _connect(MdnsServiceRecord device, {Transport? connected}) {
    // 接続している間は別の相手を受け付けない
    unawaited(_stopListening());

    setState(() {
      _connectingDevice = device;
      _screenState = _ScreenState.connecting;
    });

    _connectionTimer?.cancel();
    _connectionTimer = Timer(_connectionTimeout, _onConnectionTimeout);

    _doConnect(device, connected: connected);
  }

  Future<void> _doConnect(MdnsServiceRecord device, {Transport? connected}) async {
    try {
      final transport = connected ?? _transportFactory();

      if (connected == null) {
        await transport.connect(device.ipAddress, device.port);
      }

      _connectionTimer?.cancel();

      if (!mounted) return;

      setState(() {
        _screenState = _ScreenState.connected;
      });

      // 接続済みトランスポートを渡す（VideoDisplayScreen が再接続しないようにする）
      await widget.onConnected?.call(transport, device);

      // 映像画面が閉じられた。待ち受けに戻す。
      //
      // ここで戻さないと「接続しました」の表示のまま止まり、
      // ホーム画面の操作が何もできなくなる。
      if (!mounted) return;

      setState(() {
        _screenState = _ScreenState.idle;
        _connectingDevice = null;
      });

      // また PC から繋いでもらえるようにしておく
      await _startListening();
    } catch (e) {
      if (!mounted) return;
      _connectionTimer?.cancel();
      setState(() {
        _screenState = _ScreenState.timedOut;
      });
    }
  }

  void _onConnectionTimeout() {
    if (!mounted) return;
    setState(() {
      _screenState = _ScreenState.timedOut;
    });
  }

  void _retry() {
    final device = _connectingDevice;
    if (device != null) {
      _connect(device);
    } else {
      setState(() {
        _screenState = _ScreenState.idle;
      });
    }
  }

  // ── ビルド ──────────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('vmonitor — PC 接続'),
        actions: [
          if (_screenState == _ScreenState.idle)
            IconButton(
              icon: const Icon(Icons.wifi_find),
              tooltip: 'Wi-Fi で PC を検索',
              onPressed: _startDiscovery,
            ),
        ],
      ),
      body: _buildBody(),
    );
  }

  Widget _buildBody() {
    return switch (_screenState) {
      _ScreenState.discovering => _buildDiscoveringView(),
      _ScreenState.idle => _buildIdleView(),
      _ScreenState.connecting => _buildConnectingView(),
      _ScreenState.waitingApproval => _buildWaitingApprovalView(),
      _ScreenState.timedOut => _buildTimeoutView(),
      _ScreenState.connected => _buildConnectedView(),
    };
  }

  // ── アイドル（接続手段の一覧） ──────────────────────────────

  /// 接続手段を、いま使えるものが上に来るように並べる。
  ///
  /// 以前は 4 枚のカードが同じ重さで並んでいて、どれを使えばよいのか
  /// 分からなかった。USB が挿さっていればそれがいちばん速く確実なので、
  /// 挿さっているときは手前に出す。挿さっていなければ Wi-Fi を手前に出す。
  Widget _buildIdleView() {
    final sections = <Widget>[
      if (_usbAttached) ...[
        _buildUsbCard(),
        _buildListeningCard(),
        _buildWifiCard(),
      ] else ...[
        _buildListeningCard(),
        _buildWifiCard(),
        _buildUsbCard(),
      ],
    ];

    return ListView.separated(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      itemCount: sections.length,
      separatorBuilder: (_, __) => const SizedBox(height: 12),
      itemBuilder: (_, i) => sections[i],
    );
  }

  /// カードの見出し。どのカードも同じ形にして、拾い読みできるようにする。
  Widget _buildCardHeader({
    required IconData icon,
    required String title,
    String? badge,
    bool badgeIsGood = true,
  }) {
    return Row(
      children: [
        Icon(icon, size: 20),
        const SizedBox(width: 8),
        Expanded(
          child: Text(title, style: Theme.of(context).textTheme.titleMedium),
        ),
        if (badge != null)
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
            decoration: BoxDecoration(
              color: badgeIsGood
                  ? Colors.green.withValues(alpha: 0.15)
                  : Colors.grey.withValues(alpha: 0.15),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Text(
              badge,
              style: TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w600,
                color: badgeIsGood ? Colors.green.shade800 : Colors.grey.shade700,
              ),
            ),
          ),
      ],
    );
  }

  Widget _buildCard({required List<Widget> children}) {
    return Card(
      margin: EdgeInsets.zero,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: children,
        ),
      ),
    );
  }

  // ── USB 直結 ────────────────────────────────────────────────

  Widget _buildUsbCard() {
    return _buildCard(
      children: [
        _buildCardHeader(
          icon: Icons.usb,
          title: 'USB 直結',
          // 段階を分けて出す。
          //
          // 「ケーブルが挿さっている」と「相手で vmonitor が動いている」は
          // 別のこと。PC 側のアプリを閉じても端末はアクセサリーモードの
          // まま残るので、挿さっているだけで繋げると思わせてはいけない。
          badge: !_usbAttached
              ? '未接続'
              : _pcAlive
                  ? 'PC と通信できています'
                  : 'PC が応答しません',
          badgeIsGood: _usbAttached && _pcAlive,
        ),
        const SizedBox(height: 4),
        Text(
          !_usbAttached
              ? 'PC とケーブルで繋ぎ、PC 側で vmonitor を起動してください。'
              : _pcAlive
                  ? 'いちばん遅延が少ない繋ぎ方です。'
                  : 'ケーブルは繋がっていますが、PC 側の vmonitor から応答がありません。'
                    'PC で vmonitor を起動してください。',
          style: TextStyle(
            color: _usbAttached && !_pcAlive ? Colors.orange : Colors.grey,
            fontSize: 12,
          ),
        ),
        const SizedBox(height: 12),
        FilledButton.icon(
          icon: const Icon(Icons.usb),
          label: const Text('USB で接続'),
          // 相手が応答しているときだけ押せる。
          onPressed: (_usbAttached && _pcAlive) ? _connectUsbDirect : null,
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(44),
          ),
        ),
        // ADB 経由は AOA が使えない環境向けの逃げ道。
        // 普段は要らないので畳んでおく。
        _buildAdvanced(
          children: [
            const Text(
              'USB 直結が使えないときの代わりです。'
              'PC 側で adb reverse tcp:7979 tcp:7979 を実行しておいてください。',
              style: TextStyle(color: Colors.grey, fontSize: 12),
            ),
            const SizedBox(height: 8),
            OutlinedButton(
              onPressed: _connectUsbAdb,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(40),
              ),
              child: const Text('ADB 経由で接続'),
            ),
          ],
        ),
      ],
    );
  }

  /// 普段は要らない操作を畳んでおく入れ物。
  Widget _buildAdvanced({required List<Widget> children}) {
    return Theme(
      // 区切り線が二重に出るのを抑える
      data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
      child: ExpansionTile(
        tilePadding: EdgeInsets.zero,
        childrenPadding: const EdgeInsets.only(bottom: 8),
        expandedCrossAxisAlignment: CrossAxisAlignment.start,
        title: const Text('詳細', style: TextStyle(fontSize: 13)),
        children: children,
      ),
    );
  }

  // ── Wi-Fi（こちらから PC を探す・指定する） ──────────────────

  Widget _buildWifiCard() {
    return _buildCard(
      children: [
        _buildCardHeader(icon: Icons.wifi, title: 'Wi-Fi で PC に繋ぐ'),
        const SizedBox(height: 4),
        const Text(
          'PC と同じ Wi-Fi に繋がっている必要があります。',
          style: TextStyle(color: Colors.grey, fontSize: 12),
        ),
        const SizedBox(height: 12),

        OutlinedButton.icon(
          icon: const Icon(Icons.wifi_find),
          label: const Text('PC を自動で探す'),
          onPressed: _startDiscovery,
          style: OutlinedButton.styleFrom(
            minimumSize: const Size.fromHeight(44),
          ),
        ),

        // 見つかった PC
        if (_devices.isNotEmpty) ...[
          const SizedBox(height: 12),
          Text('見つかった PC (${_devices.length} 台)',
              style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 4),
          ..._devices.map(
            (device) => ListTile(
              contentPadding: EdgeInsets.zero,
              leading: const Icon(Icons.computer),
              title: Text(device.serviceName),
              subtitle: Text('${device.ipAddress}:${device.port}',
                  style: const TextStyle(fontSize: 12)),
              trailing: FilledButton(
                onPressed: () {
                  _transportFactory = WifiTransport.new;
                  _connect(device);
                },
                child: const Text('接続'),
              ),
            ),
          ),
        ],

        // 自動で見つからないときのための手入力。
        // 普段は要らないので畳んでおく。
        _buildAdvanced(
          children: [
            const Text(
              'PC の IP アドレスを直接入れて繋ぎます（PC 側で ipconfig で確認）。',
              style: TextStyle(color: Colors.grey, fontSize: 12),
            ),
            const SizedBox(height: 12),
            Row(
              children: [
                Expanded(
                  flex: 3,
                  child: TextField(
                    controller: _ipController,
                    decoration: const InputDecoration(
                      labelText: 'IP アドレス',
                      hintText: '例: 192.168.1.10',
                      border: OutlineInputBorder(),
                      isDense: true,
                    ),
                    keyboardType:
                        const TextInputType.numberWithOptions(decimal: true),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    controller: _portController,
                    decoration: const InputDecoration(
                      labelText: 'ポート',
                      border: OutlineInputBorder(),
                      isDense: true,
                    ),
                    keyboardType: TextInputType.number,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            OutlinedButton.icon(
              icon: const Icon(Icons.cable),
              label: const Text('このアドレスに接続'),
              onPressed: _connectManual,
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(40),
              ),
            ),
          ],
        ),
      ],
    );
  }

  // ── PC から繋いでもらう ──────────────────────────────────────

  /// PC 側で入力してもらうための、この端末のアドレスを見せる。
  ///
  /// PC の前にいるときは、スマホを手に取らずに始めたい。
  /// そのために PC が繋いでこられる口を開けてある。
  Widget _buildListeningCard() {
    final port = _listeningPort;
    final error = _listenError;

    return _buildCard(
      children: [
        _buildCardHeader(
          icon: Icons.desktop_windows,
          title: 'PC から接続してもらう',
          badge: port != null ? '待機中' : null,
        ),
        const SizedBox(height: 4),

        if (error != null)
          Text(error, style: const TextStyle(color: Colors.red, fontSize: 12))
        else if (port == null)
          const Text(
            '待ち受けを準備しています…',
            style: TextStyle(color: Colors.grey, fontSize: 12),
          )
        else ...[
          const Text(
            'PC 側の vmonitor に、次を入力してください。',
            style: TextStyle(color: Colors.grey, fontSize: 12),
          ),
          const SizedBox(height: 10),

          if (_localAddresses.isEmpty)
            const Text(
              'Wi-Fi に繋がっていないようです。'
              'Wi-Fi に接続すると、この端末のアドレスがここに出ます。',
              style: TextStyle(color: Colors.orange, fontSize: 12),
            )
          else
            // アドレスは手で PC に打ち込むもの。読み間違えないよう、
            // 等幅で大きく出し、長押しでコピーできるようにする。
            ..._localAddresses.map(
              (address) => Container(
                width: double.infinity,
                margin: const EdgeInsets.only(bottom: 6),
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                decoration: BoxDecoration(
                  color: Theme.of(context).colorScheme.surfaceContainerHighest,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: SelectableText(
                  '$address : $port',
                  style: const TextStyle(
                    fontSize: 18,
                    fontFamily: 'monospace',
                    fontWeight: FontWeight.bold,
                    letterSpacing: 0.5,
                  ),
                ),
              ),
            ),

          const SizedBox(height: 4),
          const Text(
            'PC と同じ Wi-Fi に繋がっている必要があります。',
            style: TextStyle(color: Colors.grey, fontSize: 11),
          ),
        ],
      ],
    );
  }

  // ── PC の承認待ち ────────────────────────────────────────────

  Widget _buildWaitingApprovalView() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const CircularProgressIndicator(),
            const SizedBox(height: 24),
            const Text(
              'PC 側の承認を待っています',
              style: TextStyle(fontSize: 18, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 8),
            const Text(
              'PC の画面に確認が出ています。\n「はい」を押すと映像が始まります。',
              textAlign: TextAlign.center,
              style: TextStyle(color: Colors.grey),
            ),
            const SizedBox(height: 24),
            TextButton(
              onPressed: () {
                final pending = _pendingApproval;
                if (pending != null && !pending.isCompleted) {
                  pending.complete(false);
                }
                _pendingApproval = null;
                setState(() => _screenState = _ScreenState.idle);
              },
              child: const Text('やめる'),
            ),
          ],
        ),
      ),
    );
  }

  // ── 探索中 ──────────────────────────────────────────────────

  Widget _buildDiscoveringView() {
    return const Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          CircularProgressIndicator(),
          SizedBox(height: 24),
          Text(
            'Wi-Fi 上の PC を検索しています…',
            style: TextStyle(fontSize: 16),
          ),
        ],
      ),
    );
  }

  // ── 接続中 ──────────────────────────────────────────────────

  Widget _buildConnectingView() {
    final device = _connectingDevice;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const CircularProgressIndicator(),
          const SizedBox(height: 24),
          Text(
            '${device?.serviceName ?? 'PC'} に接続中…',
            style: const TextStyle(fontSize: 16),
          ),
          const SizedBox(height: 8),
          const Text(
            '最大 10 秒かかることがあります',
            style: TextStyle(color: Colors.grey),
          ),
        ],
      ),
    );
  }

  // ── タイムアウト通知 ─────────────────────────────────────────

  Widget _buildTimeoutView() {
    final device = _connectingDevice;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(Icons.wifi_off, size: 64, color: Colors.orange),
            const SizedBox(height: 16),
            const Text(
              '接続タイムアウト',
              style: TextStyle(fontSize: 20, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 8),
            Text(
              '${device?.serviceName ?? 'PC'} への接続が 10 秒以内に完了しませんでした。\n'
              'PC クライアントが起動しているか、IP アドレスが正しいか確認してください。',
              textAlign: TextAlign.center,
              style: const TextStyle(color: Colors.grey),
            ),
            const SizedBox(height: 24),
            ElevatedButton.icon(
              icon: const Icon(Icons.refresh),
              label: const Text('再試行'),
              onPressed: _retry,
            ),
            const SizedBox(height: 12),
            TextButton(
              onPressed: () {
                setState(() => _screenState = _ScreenState.idle);
                _startListening();
              },
              child: const Text('戻る'),
            ),
          ],
        ),
      ),
    );
  }

  // ── 接続成功 ─────────────────────────────────────────────────

  Widget _buildConnectedView() {
    final device = _connectingDevice;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.check_circle_outline, size: 64, color: Colors.green),
          const SizedBox(height: 16),
          Text(
            '${device?.serviceName ?? 'PC'} に接続しました',
            style: const TextStyle(fontSize: 18, fontWeight: FontWeight.bold),
          ),
          const SizedBox(height: 8),
          const Text('セッションを確立しています…'),
        ],
      ),
    );
  }
}
