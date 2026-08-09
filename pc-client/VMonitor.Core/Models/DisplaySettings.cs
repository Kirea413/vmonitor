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
/// スマホに映すときの拡大率（パーセント）。
/// <para>
/// 100 なら端末の画素数そのままで仮想ディスプレイを作る。スマホの画面は
/// 小さいので、そのままでは Windows の文字やボタンが細かすぎて読めない。
/// 150 なら、見た目の大きさが 1.5 倍になるぶん狭い作業領域になる。
/// </para>
/// <para>
/// 端末の画素数を割って仮想ディスプレイの解像度を決める形で効かせる。
/// 映像はスマホ側で画面いっぱいに伸ばされるため、拡大して見える。
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
    /// 端末の画素数から、実際に作る仮想ディスプレイの解像度を求める。
    /// </summary>
    /// <remarks>
    /// 拡大率のぶん解像度を下げる。エンコーダーは偶数でないと扱えないので
    /// 2 の倍数に丸める。極端に小さくならないよう下限も設ける。
    /// </remarks>
    public Resolution ApplyScale(Resolution physical)
    {
        int percent = SafeScalePercent;

        if (percent == 100) return physical;

        static int Scaled(int value, int percent)
        {
            int scaled = (int)Math.Round(value * 100.0 / percent);

            // 奇数だと NV12 に変換できない
            if (scaled % 2 != 0) scaled--;

            return Math.Max(scaled, 320);
        }

        return new Resolution(Scaled(physical.Width, percent),
                              Scaled(physical.Height, percent));
    }

    /// <summary>デフォルトのディスプレイ設定。</summary>
    public static readonly DisplaySettings Default = new(
        Mode: DisplayMode.Extend,
        ManualResolution: null,
        RequireVirtualDisplay: true,
        ScalePercent: 100,
        EnableTouch: true
    );
}
