import 'dart:typed_data';

/// トランスポート: Wi-Fi (mDNS + TCP/TLS) と USB の統一インターフェース
abstract class Transport {
  /// トランスポート種別
  TransportType get type;

  /// エンドポイントへ接続する
  Future<void> connect(String host, int port);

  /// 接続を切断する
  Future<void> disconnect();

  /// 指定チャンネルにデータを送信する
  Future<void> send(Uint8List data, ChannelId channel);

  /// 受信データストリーム (チャンネルIDとデータのペア)
  Stream<({ChannelId channel, Uint8List data})> receive();

  /// 推定帯域幅 (bps)
  int get estimatedBandwidthBps;
}

/// トランスポート種別
enum TransportType { wifi, usb }

/// チャンネル識別子
enum ChannelId { video, touch, control }
