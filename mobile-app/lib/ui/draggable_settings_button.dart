import 'package:flutter/material.dart';

import 'display_preferences.dart';

/// 映像の上に重ねる設定ボタン。
///
/// 映像の一部を隠してしまうので、
///   - 好きな位置へドラッグして動かせる
///   - 長押しで隠せる（設定画面からも切り替えられる）
/// ようにしてある。
///
/// 隠したあとは画面の左上を 2 回叩くと戻る。
/// 戻す手段が無いと設定画面へ行けなくなるため、必ず残しておく。
class DraggableSettingsButton extends StatefulWidget {
  /// ボタンが押されたときの動作。
  final VoidCallback onPressed;

  /// 置ける範囲の内側の余白。画面の端に張り付かないようにする。
  final double margin;

  const DraggableSettingsButton({
    super.key,
    required this.onPressed,
    this.margin = 12,
  });

  @override
  State<DraggableSettingsButton> createState() => _DraggableSettingsButtonState();
}

class _DraggableSettingsButtonState extends State<DraggableSettingsButton> {
  static const double _buttonSize = 44;

  /// ボタンの左上位置。null のうちは右上に置く。
  Offset? _position;

  @override
  void initState() {
    super.initState();
    displayPreferences.addListener(_onPreferencesChanged);
  }

  @override
  void dispose() {
    displayPreferences.removeListener(_onPreferencesChanged);
    super.dispose();
  }

  void _onPreferencesChanged() {
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final rawMaxX = constraints.maxWidth  - _buttonSize - widget.margin;
        final rawMaxY = constraints.maxHeight - _buttonSize - widget.margin;

        // 画面が小さいと余白のほうが大きくなりうる。範囲が反転しないようにする。
        final maxX = rawMaxX < widget.margin ? widget.margin : rawMaxX;
        final maxY = rawMaxY < widget.margin ? widget.margin : rawMaxY;

        // 初期位置は右上。画面が回ると大きさが変わるので、そのつど収め直す。
        final position = _position ?? Offset(maxX, widget.margin);
        final clamped = Offset(
          position.dx.clamp(widget.margin, maxX),
          position.dy.clamp(widget.margin, maxY),
        );

        if (!displayPreferences.showSettingsButton) {
          return _buildRevealArea();
        }

        return Stack(
          children: [
            Positioned(
              left: clamped.dx,
              top: clamped.dy,
              child: _buildButton(maxX, maxY, clamped),
            ),
          ],
        );
      },
    );
  }

  Widget _buildButton(double maxX, double maxY, Offset current) {
    // タップ・長押し・ドラッグを 1 つの GestureDetector にまとめる。
    //
    // 以前は外側で長押しとドラッグ、内側の InkWell でタップを拾っていた。
    // 別々の認識器が同じ指を取り合い、長押しが内側のタップに負けて
    // 「非表示にできない」状態になっていた。
    // まとめておけば Flutter が優先順位を正しく解決してくれる。
    return GestureDetector(
      onTap: widget.onPressed,

      onLongPress: _hide,

      onPanUpdate: (details) {
        setState(() {
          _position = Offset(
            (current.dx + details.delta.dx).clamp(widget.margin, maxX),
            (current.dy + details.delta.dy).clamp(widget.margin, maxY),
          );
        });
      },

      child: Container(
        width: _buttonSize,
        height: _buttonSize,
        decoration: const BoxDecoration(
          color: Colors.black54,
          shape: BoxShape.circle,
        ),
        child: const Icon(Icons.settings, color: Colors.white, size: 22),
      ),
    );
  }

  void _hide() {
    displayPreferences.setShowSettingsButton(false);

    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(
        content: Text('設定ボタンを隠しました。画面の左上を 2 回叩くと戻ります。'),
        duration: Duration(seconds: 4),
      ),
    );
  }

  /// 隠しているときの復帰用。
  ///
  /// 見えないものを戻す手段が無いと詰むので、画面の左上に
  /// 触れる場所だけ残しておく。映像には何も描かない。
  Widget _buildRevealArea() {
    return Align(
      alignment: Alignment.topLeft,
      child: GestureDetector(
        behavior: HitTestBehavior.translucent,
        onDoubleTap: () => displayPreferences.setShowSettingsButton(true),
        child: const SizedBox(width: 72, height: 72),
      ),
    );
  }
}
