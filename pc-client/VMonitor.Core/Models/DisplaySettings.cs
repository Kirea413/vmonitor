namespace VMonitor.Core.Models;

/// <summary>ディスプレイ設定のデフォルト値（永続化対象）。</summary>
/// <param name="Mode">スマホをどう使うか（拡張・複製・セカンダリのみ）。</param>
/// <param name="ManualResolution">
/// 解像度を手で決める場合の値。null なら接続してきた端末の画面に合わせる。
/// </param>
/// <param name="RequireVirtualDisplay">
/// 仮想ディスプレイを必須にするか。
/// <para>
/// 既定は true。スマホは「2 枚目のモニター」として使うものなので、
/// 仮想ディスプレイを用意できないときに黙って PC 画面のミラーへ落ちると、
/// 拡張のつもりで繋いだ利用者には「同じ画面が出てきた」としか見えない。
/// そのため既定では落とさず、失敗として扱う。
/// </para>
/// <para>
/// false にすると、仮想ディスプレイが使えないときに PC のメイン画面の
/// ミラーへ切り替える（ドライバ未導入でもとりあえず映る）。
/// </para>
/// </param>
/// <param name="ScalePercent">
/// スマホに映すときの表示スケール（パーセント）。
/// <para>
/// Windows の「拡大縮小」と同じもの。解像度は端末の画素数のまま保ち、
/// 文字やボタンだけを大きくする。スマホの画面は小さく、PC の画面を
/// そのまま映すと細かすぎて読めないため。
/// </para>
/// <para>
/// 以前は解像度を下げて実現していたが、それでは映像がぼやける。
/// 表示スケールならくっきりしたまま大きくなる。
/// </para>
/// <para>
/// Windows が用意している刻み（100/125/150/175/200/225/250/300…）に
/// 丸められる。画面によって使える上限が違う。
/// </para>
/// </param>
/// <param name="EnableTouch">
/// スマホの操作を PC へ送るか。
/// <para>
/// false にすると「見るだけ」になる。動画を映しておくだけの用途では、
/// 画面に触れるたびにマウスが飛ぶほうが困る。
/// </para>
/// </param>
public record DisplaySettings(
    DisplayMode Mode,
    Resolution? ManualResolution,
    bool RequireVirtualDisplay = true,
    int ScalePercent = 100,
    bool EnableTouch = true
)
{
    /// <summary>拡大率として受け付ける範囲。</summary>
    public const int MinScalePercent = 100;
    public const int MaxScalePercent = 300;

    /// <summary>範囲に収めた拡大率。</summary>
    public int SafeScalePercent =>
        Math.Clamp(ScalePercent, MinScalePercent, MaxScalePercent);

    /// <summary>
    /// ディスプレイの短辺として Windows が受け付ける下限。
    /// </summary>
    /// <remarks>
    /// これを下回る解像度を要求すると、Windows は要求を通さず
    /// 既定のモードでモニターを作る。実際に 300% を指定したとき
    /// 360x800 を要求して 1920x1080 が出来てしまい、
    /// 「拡大率が効かない」という形で表面化した。
    /// </remarks>
    /// <summary>
    /// 解像度は端末の画素数のまま使う。
    /// </summary>
    /// <remarks>
    /// 以前は拡大率のぶん解像度を下げていたが、それでは映像がぼやけ、
    /// 作業領域も狭くなる。いまは解像度を保ったまま Windows の
    /// 表示スケールを上げる方式にしたので、ここでは何も変えない。
    /// </remarks>
    public Resolution ApplyScale(Resolution physical) => physical;

    /// <summary>デフォルトのディスプレイ設定。</summary>
    public static readonly DisplaySettings Default = new(
        Mode: DisplayMode.Extend,
        ManualResolution: null,
        RequireVirtualDisplay: true,
        ScalePercent: 100,
        EnableTouch: true
    );
}
