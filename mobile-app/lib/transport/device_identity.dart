import 'dart:math';

import 'package:shared_preferences/shared_preferences.dart';

/// This installation's stable pairing identifier.
///
/// It lets the PC recognise a previously approved device after DHCP changes.
/// The value is random, stays inside the app sandbox, and contains no hardware
/// or account information.
class DeviceIdentity {
  static const String _preferenceKey = 'vmonitor.device_identity.v1';

  static Future<String>? _cached;

  static Future<String> loadOrCreate() => _cached ??= _loadOrCreate();

  static Future<String> _loadOrCreate() async {
    final preferences = await SharedPreferences.getInstance();
    final existing = preferences.getString(_preferenceKey);
    if (existing != null && existing.length >= 16 && existing.length <= 128) {
      return existing;
    }

    final random = Random.secure();
    final bytes = List<int>.generate(16, (_) => random.nextInt(256));
    final value =
        bytes.map((byte) => byte.toRadixString(16).padLeft(2, '0')).join();
    await preferences.setString(_preferenceKey, value);
    return value;
  }
}
