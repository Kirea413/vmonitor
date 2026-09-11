import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:vmonitor/transport/connect_protocol.dart';

void main() {
  test('接続要求に安定端末IDと表示情報を含められる', () {
    final bytes = ConnectProtocol.request(
      ConnectProtocol.initiatorPhone,
      deviceId: '0123456789abcdef',
      deviceName: 'テスト端末',
      platform: 'ios',
    );

    final json = jsonDecode(utf8.decode(bytes)) as Map<String, dynamic>;
    expect(json['type'], 'connect_request');
    expect(json['initiator'], 'phone');
    expect(json['deviceId'], '0123456789abcdef');
    expect(json['name'], 'テスト端末');
    expect(json['platform'], 'ios');
  });

  test('旧形式の接続要求も維持する', () {
    final bytes = ConnectProtocol.request(ConnectProtocol.initiatorPc);
    final json = jsonDecode(utf8.decode(bytes)) as Map<String, dynamic>;

    expect(json, {'type': 'connect_request', 'initiator': 'pc'});
  });
}
