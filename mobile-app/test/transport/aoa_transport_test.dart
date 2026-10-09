import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:vmonitor/transport/aoa_transport.dart';
import 'package:vmonitor/transport/transport.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const method = MethodChannel('vmonitor/aoa');
  const frames = MethodChannel('vmonitor/aoa/frames');
  const states = MethodChannel('vmonitor/aoa/state');
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  test(
      'AOA buffers approval received during native connect before Dart listens',
      () async {
    var subscribed = false;
    messenger.setMockMethodCallHandler(frames, (call) async {
      subscribed = call.method == 'listen';
      return null;
    });
    messenger.setMockMethodCallHandler(states, (_) async => null);
    messenger.setMockMethodCallHandler(method, (call) async {
      if (call.method == 'connect') {
        expect(subscribed, isTrue);
        await messenger.handlePlatformMessage(
          'vmonitor/aoa/frames',
          const StandardMethodCodec().encodeSuccessEnvelope({
            'channel': ChannelId.control.index,
            'data': Uint8List.fromList([42]),
          }),
          (_) {},
        );
        return <String, String>{'manufacturer': 'vmonitor', 'model': 'test'};
      }
      return null;
    });
    final transport = AoaTransport();
    try {
      await transport.connect('usb', 0);
      final frame =
          await transport.receive().first.timeout(const Duration(seconds: 2));
      expect(frame.channel, ChannelId.control);
      expect(frame.data, [42]);
    } finally {
      await transport.disconnect();
      messenger.setMockMethodCallHandler(method, null);
      messenger.setMockMethodCallHandler(frames, null);
      messenger.setMockMethodCallHandler(states, null);
    }
  });
}
