namespace VMonitor.Streamer;

/// <summary>
/// この PC で使える映像エンコーダーを調べる。
/// </summary>
/// <remarks>
/// ソフトウェアエンコードは 1 枚あたり数十ミリ秒かかり、その時間が
/// そのままスマホ側の遅れになる（1920x1080 で実測 40〜55ms）。
/// ハードウェアエンコーダーがあるかどうかで遅延を詰める手立てが変わるため、
/// 推測せず実際に列挙して確かめられるようにしてある。
/// </remarks>
public static class EncoderCapabilities
{
    /// <summary>エンコーダー 1 台ぶんの情報。</summary>
    /// <param name="IsHardware">GPU 側で動くものか。</param>
    /// <param name="IsAsync">非同期 MFT か（ハードウェアはたいてい非同期）。</param>
    /// <param name="Name">Windows が報告する名前。</param>
    public readonly record struct EncoderInfo(bool IsHardware, bool IsAsync, string Name);

    /// <summary>
    /// H.264 を出力できるエンコーダーを列挙する。
    /// ネイティブ DLL が無い環境では空を返す。
    /// </summary>
    public static IReadOnlyList<EncoderInfo> ListH264Encoders()
        => NativeEncoder.ListEncoders()
            .Select(e => new EncoderInfo(e.IsHardware, e.IsAsync, e.Name))
            .ToList();

    /// <summary>
    /// いま使っているエンコーダーの内部状態（切り分け用）。
    /// </summary>
    /// <param name="IsAsync">非同期 MFT（＝ふつうハードウェア）を使っているか。</param>
    /// <param name="EventsSeen">MFT から受け取ったイベントの総数。</param>
    /// <param name="NeedInputSeen">「入力をよこせ」の回数。</param>
    /// <param name="HaveOutputSeen">「出力があるぞ」の回数。</param>
    /// <param name="ProcessInputCalls">ProcessInput を呼んだ回数。</param>
    /// <param name="ProcessInputFails">ProcessInput が失敗した回数。</param>
    /// <param name="ProcessOutputCalls">ProcessOutput を呼んだ回数。</param>
    /// <param name="LastHr">最後の HRESULT。</param>
    public readonly record struct EncoderDiagnostics(
        bool IsAsync,
        int  EventsSeen,
        int  NeedInputSeen,
        int  HaveOutputSeen,
        int  ProcessInputCalls,
        int  ProcessInputFails,
        int  ProcessOutputCalls,
        int  LastHr);

    /// <summary>直近のエンコード動作の内部状態を取得する。</summary>
    public static EncoderDiagnostics GetDiagnostics() => NativeEncoder.GetDiagnostics();

    /// <summary>
    /// エンコーダーを解放して次回の呼び出しで作り直させる（計測用）。
    /// </summary>
    public static void ResetEncoder() => NativeEncoder.Release();

    /// <summary>
    /// 直近 1 枚ぶんの内訳（色形式の変換 / エンコード本体）をマイクロ秒で返す。
    /// </summary>
    /// <remarks>
    /// どちらが重いかで打ち手が変わる。変換が重いならそこを GPU へ移す、
    /// エンコード本体が重いならハードウェアエンコーダーを使う、という判断になる。
    /// </remarks>
    public static (int ConvertUs, int MftUs) GetLastFrameTiming() => NativeEncoder.GetTiming();
}
