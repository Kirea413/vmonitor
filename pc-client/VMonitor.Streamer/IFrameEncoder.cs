using VMonitor.Core.Models;

namespace VMonitor.Streamer;

/// <summary>
/// フレームを圧縮映像に変換するエンコーダー。
/// </summary>
/// <remarks>
/// <para>
/// 実装はネイティブの H.264 エンコーダー (<see cref="NativeH264Encoder"/>) だが、
/// これはプロセス内に 1 つしか存在できないグローバル資源で、
/// Media Foundation の初期化・GPU/CPU 資源・解像度の状態を抱えている。
/// </para>
/// <para>
/// ストリーマーがそれを直接掴むと、ストリーマー自身の挙動
/// （フレームレート・スキップ・統計）を検証したいときにも
/// 実エンコードが走ってしまい、遅いうえに実行環境の性能に左右される。
/// ここで差し替え可能にして、両者を切り離す。
/// </para>
/// </remarks>
public interface IFrameEncoder : IDisposable
{
    /// <summary>
    /// エンコーダーを指定の解像度・ビットレート・フレームレートに設定する。
    /// 同じ設定で繰り返し呼ばれても安全でなければならない。
    /// </summary>
    void Configure(Resolution resolution, int bitrateBps, int maxFps);

    /// <summary>目標ビットレートを変更する。</summary>
    void SetBitrate(int bitrateBps);

    /// <summary>
    /// BGRA32 のフレームをエンコードする。
    /// </summary>
    /// <returns>
    /// エンコード済みデータ。まだ出力できるものがない場合は null
    /// （エンコーダーが数フレーム分をためてから出力を始めるため、
    /// ストリーミング開始直後は null が続く）。
    /// </returns>
    byte[]? Encode(ReadOnlySpan<byte> bgra32Data, long timestampUs);
}
