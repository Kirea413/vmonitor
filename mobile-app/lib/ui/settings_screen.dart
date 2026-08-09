import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../transport/transport.dart';
import 'display_preferences.dart';

/// スマホアプリの設定画面。
///
/// フレームレートとビットレートの設定を変更し、
/// 制御チャンネル経由で PC クライアントへ即時反映する。
///
/// 要件:
/// - 7.4: スマホアプリ SHALL フレームレートおよびビットレートの設定を変更できる画面を提供する
class SettingsScreen extends StatefulWidget {
  /// アクティブなトランスポート（制御メッセージの送信に使用する）
  final Transport transport;

  /// 初期フレームレート（fps）。デフォルト 60fps。
  final int initialFramerate;

  /// 初期ビットレート（Mbps）。デフォルト 10Mbps。
  final int initialBitrateMbps;

  /// 接続を切ってホーム画面へ戻る操作。
  ///
  /// 映像画面には戻る導線が無い（全画面表示のため）。
  /// 設定はいつでも開けるので、ここに逃げ道を置く。
  /// 映像画面以外から開いたときは null。
  final VoidCallback? onExit;

  const SettingsScreen({
    super.key,
    required this.transport,
    this.initialFramerate = 60,
    this.initialBitrateMbps = 10,
    this.onExit,
  });

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  /// 選択可能なフレームレート候補（fps）
  static const List<int> _framerateOptions = [30, 45, 60];

  late int _selectedFramerate;
  late int _selectedBitrateMbps;

  bool _isSending = false;
  String? _statusMessage;

  @override
  void initState() {
    super.initState();
    _selectedFramerate = widget.initialFramerate;
    _selectedBitrateMbps = widget.initialBitrateMbps;
  }

  // ── 設定送信 ────────────────────────────────────────────────

  /// 現在の設定を制御チャンネル経由で PC クライアントへ送信する。
  ///
  /// 制御メッセージのフォーマット:
  /// ```json
  /// {
  ///   "type": "update_streaming_settings",
  ///   "maxFps": 60,
  ///   "bitrateBps": 10000000
  /// }
  /// ```
  Future<void> _applySettings() async {
    setState(() {
      _isSending = true;
      _statusMessage = null;
    });

    try {
      final payload = _buildControlMessage(
        maxFps: _selectedFramerate,
        bitrateBps: _selectedBitrateMbps * 1000 * 1000,
      );
      await widget.transport.send(payload, ChannelId.control);

      if (!mounted) return;
      setState(() {
        _statusMessage = '設定を適用しました';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _statusMessage = '送信に失敗しました: $e';
      });
    } finally {
      if (mounted) {
        setState(() {
          _isSending = false;
        });
      }
    }
  }

  /// 制御メッセージを JSON エンコードした [Uint8List] に変換する。
  static Uint8List _buildControlMessage({
    required int maxFps,
    required int bitrateBps,
  }) {
    final json = jsonEncode({
      'type': 'update_streaming_settings',
      'maxFps': maxFps,
      'bitrateBps': bitrateBps,
    });
    return Uint8List.fromList(utf8.encode(json));
  }

  // ── ビルド ──────────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('ストリーミング設定'),
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (widget.onExit != null) ...[
            _buildExitButton(),
            const SizedBox(height: 24),
            const Divider(),
            const SizedBox(height: 16),
          ],
          _buildFramerateSection(),
          const SizedBox(height: 24),
          _buildBitrateSection(),
          const SizedBox(height: 32),
          _buildApplyButton(),
          if (_statusMessage != null) ...[
            const SizedBox(height: 16),
            _buildStatusMessage(),
          ],
          const SizedBox(height: 32),
          const Divider(),
          const SizedBox(height: 16),
          _buildViewportSection(),
        ],
      ),
    );
  }

  // ── ホームへ戻る ─────────────────────────────────────────────

  /// 接続を切ってホーム画面へ戻る。
  ///
  /// 映像画面は全画面で、戻るための表示が何も出ていない。
  /// ここを塞ぐと、繋いだあとアプリを終了する以外に抜ける手が無くなる。
  Widget _buildExitButton() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        OutlinedButton.icon(
          icon: const Icon(Icons.home_outlined),
          label: const Text('接続を切ってホームに戻る'),
          onPressed: widget.onExit,
          style: OutlinedButton.styleFrom(
            minimumSize: const Size.fromHeight(48),
          ),
        ),
        const SizedBox(height: 16),
        Text('切断の操作', style: Theme.of(context).textTheme.titleSmall),
        const SizedBox(height: 4),
        Text(
          '映像を見ている間に切断するための操作です。'
          '画面いっぱいが PC の入力面なので、ボタンではなく操作で抜けます。',
          style: TextStyle(fontSize: 12, color: Colors.grey.shade600),
        ),
        const SizedBox(height: 8),

        // 選べるようにしてある。指の本数で見分ける操作は、
        // PC 側で同じ本数を使いたい人には邪魔になるため。
        RadioGroup<DisconnectGesture>(
          groupValue: displayPreferences.disconnectGesture,
          onChanged: (value) {
            if (value == null) return;
            displayPreferences.setDisconnectGesture(value);
            setState(() {});
          },
          child: Column(
            children: [
              for (final option in DisconnectGesture.values)
                RadioListTile<DisconnectGesture>(
                  contentPadding: EdgeInsets.zero,
                  dense: true,
                  value: option,
                  title:
                      Text(option.label, style: const TextStyle(fontSize: 14)),
                  subtitle: Text(
                    option.description,
                    style: const TextStyle(fontSize: 11),
                  ),
                ),
            ],
          ),
        ),

        // 確認を挟むかどうか。
        //
        // 誤って出たときに戻れるほうがよい場面と、慣れて手数が
        // 邪魔になる場面の両方がある。「使わない」を選んでいる間は
        // ジェスチャー自体が無いので、この設定も出さない。
        if (displayPreferences.disconnectGesture != DisconnectGesture.none)
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: const Text('切断の前に確認する',
                style: TextStyle(fontSize: 14)),
            subtitle: const Text(
              '外すと、操作した時点ですぐ切断してホームに戻ります。',
              style: TextStyle(fontSize: 11),
            ),
            value: displayPreferences.confirmBeforeDisconnect,
            onChanged: (value) {
              displayPreferences.setConfirmBeforeDisconnect(value);
              setState(() {});
            },
          ),
      ],
    );
  }

  // ── 表示まわり（この端末だけの設定） ──────────────────────────

  /// 画面の余白とデバッグ表示の切り替え。
  ///
  /// フレームレートやビットレートと違い、PC へは送らない。
  /// 端末の形（丸い角、ジェスチャーの帯）に合わせるための設定なので、
  /// この端末に保存する。
  Widget _buildViewportSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          '画面の余白',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 4),
        const Text(
          '画面の縁は狙って触りにくいので、内側だけを使うようにできます。\n'
          '映像ごと内側に寄せるため、PC 画面のどこにでも届く状態は変わりません。',
          style: TextStyle(fontSize: 12, color: Colors.grey),
        ),
        const SizedBox(height: 12),

        _buildInsetSlider(
          label: '上',
          value: displayPreferences.top,
          onChanged: (v) => displayPreferences.setInsets(top: v),
        ),
        _buildInsetSlider(
          label: '下',
          value: displayPreferences.bottom,
          onChanged: (v) => displayPreferences.setInsets(bottom: v),
        ),
        _buildInsetSlider(
          label: '左',
          value: displayPreferences.left,
          onChanged: (v) => displayPreferences.setInsets(left: v),
        ),
        _buildInsetSlider(
          label: '右',
          value: displayPreferences.right,
          onChanged: (v) => displayPreferences.setInsets(right: v),
        ),

        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerRight,
          child: TextButton(
            onPressed: () {
              displayPreferences.setInsets(top: 0, bottom: 0, left: 0, right: 0);
              setState(() {});
            },
            child: const Text('余白をなくす'),
          ),
        ),

        const SizedBox(height: 16),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('設定ボタンを表示する'),
          subtitle: const Text(
            '映像の上に出る歯車のボタンです。ドラッグで移動、長押しで非表示にできます。'
            '隠したあとは画面の左上を 2 回叩くと戻ります。',
            style: TextStyle(fontSize: 12),
          ),
          value: displayPreferences.showSettingsButton,
          onChanged: (value) {
            displayPreferences.setShowSettingsButton(value);
            setState(() {});
          },
        ),

        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('受信状況を画面に表示する'),
          subtitle: const Text(
            '受信したパケット数などを左上に出します。'
            '映像がまだ来ていない間は、この設定に関わらず表示します。',
            style: TextStyle(fontSize: 12),
          ),
          value: displayPreferences.showDebugOverlay,
          onChanged: (value) {
            displayPreferences.setShowDebugOverlay(value);
            setState(() {});
          },
        ),
      ],
    );
  }

  Widget _buildInsetSlider({
    required String label,
    required double value,
    required ValueChanged<double> onChanged,
  }) {
    return Row(
      children: [
        SizedBox(width: 24, child: Text(label)),
        Expanded(
          child: Slider(
            value: value,
            min: 0,
            max: DisplayPreferences.maxInset,
            divisions: (DisplayPreferences.maxInset / 4).round(),
            label: '${value.round()}',
            onChanged: (v) {
              onChanged(v);
              setState(() {});
            },
          ),
        ),
        SizedBox(
          width: 40,
          child: Text('${value.round()}', textAlign: TextAlign.right),
        ),
      ],
    );
  }

  // ── フレームレートセクション ──────────────────────────────────

  Widget _buildFramerateSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'フレームレート',
          style: Theme.of(context).textTheme.titleMedium,
        ),
        const SizedBox(height: 4),
        Text(
          '映像の滑らかさを設定します。高い値ほど滑らかですが帯域を多く使います。',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: Colors.grey,
              ),
        ),
        const SizedBox(height: 12),
        Row(
          children: _framerateOptions.map((fps) {
            final selected = fps == _selectedFramerate;
            return Expanded(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 4),
                child: _FramerateChip(
                  fps: fps,
                  selected: selected,
                  onTap: () {
                    setState(() {
                      _selectedFramerate = fps;
                      _statusMessage = null;
                    });
                  },
                ),
              ),
            );
          }).toList(),
        ),
      ],
    );
  }

  // ── ビットレートセクション ────────────────────────────────────

  Widget _buildBitrateSection() {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(
              'ビットレート',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            Text(
              '$_selectedBitrateMbps Mbps',
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: Theme.of(context).colorScheme.primary,
                  ),
            ),
          ],
        ),
        const SizedBox(height: 4),
        Text(
          '映像品質を設定します。高い値ほど高品質ですが帯域を多く使います（1〜50 Mbps）。',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: Colors.grey,
              ),
        ),
        Slider(
          value: _selectedBitrateMbps.toDouble(),
          min: 1,
          max: 50,
          divisions: 49,
          label: '$_selectedBitrateMbps Mbps',
          onChanged: (value) {
            setState(() {
              _selectedBitrateMbps = value.round();
              _statusMessage = null;
            });
          },
        ),
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Text(
              '1 Mbps',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Colors.grey,
                  ),
            ),
            Text(
              '50 Mbps',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Colors.grey,
                  ),
            ),
          ],
        ),
      ],
    );
  }

  // ── 適用ボタン ───────────────────────────────────────────────

  Widget _buildApplyButton() {
    return SizedBox(
      width: double.infinity,
      child: ElevatedButton.icon(
        icon: _isSending
            ? const SizedBox(
                width: 18,
                height: 18,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.send),
        label: Text(_isSending ? '送信中…' : '設定を適用'),
        onPressed: _isSending ? null : _applySettings,
      ),
    );
  }

  // ── ステータスメッセージ ─────────────────────────────────────

  Widget _buildStatusMessage() {
    final isError = _statusMessage?.startsWith('送信に失敗') ?? false;
    return Row(
      children: [
        Icon(
          isError ? Icons.error_outline : Icons.check_circle_outline,
          size: 18,
          color: isError ? Colors.red : Colors.green,
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            _statusMessage!,
            style: TextStyle(
              color: isError ? Colors.red : Colors.green,
            ),
          ),
        ),
      ],
    );
  }
}

// ── フレームレート選択チップ ────────────────────────────────────

class _FramerateChip extends StatelessWidget {
  final int fps;
  final bool selected;
  final VoidCallback onTap;

  const _FramerateChip({
    required this.fps,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 150),
        padding: const EdgeInsets.symmetric(vertical: 12),
        decoration: BoxDecoration(
          color: selected ? colorScheme.primary : colorScheme.surfaceContainerHighest,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(
            color: selected ? colorScheme.primary : colorScheme.outline,
          ),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              '$fps',
              style: TextStyle(
                fontSize: 20,
                fontWeight: FontWeight.bold,
                color: selected ? colorScheme.onPrimary : colorScheme.onSurface,
              ),
            ),
            Text(
              'fps',
              style: TextStyle(
                fontSize: 12,
                color: selected
                    ? colorScheme.onPrimary.withValues(alpha: 0.8)
                    : colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
