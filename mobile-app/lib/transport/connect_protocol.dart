import 'dart:convert';
import 'dart:typed_data';

/// 接続を始めるときのやり取り。
///
/// ## なぜ要るか
///
/// ケーブルが挿さっていることと、画面を映してよいことは別のこと。
/// 挿しただけで勝手に映り始めると、PC を触っている人にもスマホを持っている人にも
/// 「何が起きたのか」が分からない。
///
/// そこで、押した側と反対側に承認を出す。
///
/// | 押した側 | 承認を出す側 |
/// |---|---|
/// | スマホの「USB で接続」 | PC |
/// | PC の「接続」 | スマホ |
///
/// 押した側が [ConnectRequest] を送り、受けた側が利用者に尋ねて
/// [ConnectResponse] を返す。
class ConnectProtocol {
  /// 誰が言い出したか。
  static const String initiatorPc = 'pc';
  static const String initiatorPhone = 'phone';

  static const String _typeRequest = 'connect_request';
  static const String _typeResponse = 'connect_response';

  /// 「繋ぎたい」と伝える。
  static Uint8List request(String initiator) => _encode({
        'type': _typeRequest,
        'initiator': initiator,
      });

  /// 承認・拒否を返す。
  static Uint8List response({required bool accepted}) => _encode({
        'type': _typeResponse,
        'accepted': accepted,
      });

  /// 制御メッセージを読み解く。関係のないものなら null。
  static ConnectMessage? parse(Uint8List data) {
    try {
      final decoded = jsonDecode(utf8.decode(data));
      if (decoded is! Map) return null;

      switch (decoded['type']) {
        case _typeRequest:
          return ConnectMessage.request(
            (decoded['initiator'] as String?) ?? initiatorPc,
          );

        case _typeResponse:
          return ConnectMessage.response(decoded['accepted'] == true);

        default:
          return null;
      }
    } catch (_) {
      // 解釈できないものは黙って捨てる。
      // 制御チャンネルには他の用途のメッセージも流れる。
      return null;
    }
  }

  static Uint8List _encode(Map<String, Object?> body) =>
      Uint8List.fromList(utf8.encode(jsonEncode(body)));
}

/// [ConnectProtocol] で読み解いたメッセージ。
class ConnectMessage {
  /// 要求なら言い出した側、応答なら null。
  final String? initiator;

  /// 応答なら承認されたか、要求なら null。
  final bool? accepted;

  const ConnectMessage._(this.initiator, this.accepted);

  factory ConnectMessage.request(String initiator) =>
      ConnectMessage._(initiator, null);

  factory ConnectMessage.response(bool accepted) =>
      ConnectMessage._(null, accepted);

  bool get isRequest => initiator != null;
  bool get isResponse => accepted != null;

  /// PC が言い出した要求か。
  bool get fromPc => initiator == ConnectProtocol.initiatorPc;
}
