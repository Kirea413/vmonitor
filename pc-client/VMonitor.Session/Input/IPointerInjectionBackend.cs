using VMonitor.Core.Models;

namespace VMonitor.Session.Input;

/// <summary>
/// 注入するポインターの種別。
/// </summary>
public enum PointerInjectionMode
{
    /// <summary>
    /// タッチとして注入する。Windows 8 以降で動作し、
    /// ピンチ・スワイプなどの OS ジェスチャーがそのまま効く。
    /// </summary>
    Touch,

    /// <summary>
    /// ペンとして注入する（Windows Ink）。Windows 10 1809 以降。
    /// 筆圧付きの本物のペン入力になるため、OneNote などで手書きとして扱われる。
    /// </summary>
    Pen
}

/// <summary>
/// ピクセル座標に変換済みのポインター（指またはペン先）1 点。
/// </summary>
/// <param name="Id">タッチポイント識別子。マルチタッチで各指を区別する。</param>
/// <param name="PixelX">仮想デスクトップ上の X ピクセル座標。</param>
/// <param name="PixelY">仮想デスクトップ上の Y ピクセル座標。</param>
/// <param name="Pressure">筆圧 [0.0, 1.0]。</param>
/// <param name="Phase">このポインターのライフサイクルフェーズ。</param>
public readonly record struct InjectedPointer(
    int Id,
    int PixelX,
    int PixelY,
    double Pressure,
    TouchPhase Phase
);

/// <summary>
/// ポインター注入の実行部。Win32 API を叩く実装とテスト用の記録実装を差し替える。
/// </summary>
public interface IPointerInjectionBackend : IDisposable
{
    /// <summary>このバックエンドがペン（Windows Ink）注入に対応しているか。</summary>
    bool SupportsPen { get; }

    /// <summary>
    /// 注入デバイスを初期化する。複数回呼ばれても安全でなければならない。
    /// </summary>
    /// <param name="mode">注入するポインター種別。</param>
    /// <param name="maxContacts">同時に注入する最大コンタクト数。</param>
    /// <returns>初期化に成功した場合 true。</returns>
    bool Initialize(PointerInjectionMode mode, int maxContacts);

    /// <summary>
    /// 1 フレーム分のポインター状態を注入する。
    /// </summary>
    /// <remarks>
    /// Windows のポインター注入 API は「そのフレーム時点で有効な全コンタクト」を
    /// 毎回まとめて要求するため、<paramref name="frame"/> には
    /// 継続中の接触も含めて渡す必要がある。
    /// </remarks>
    /// <returns>注入に成功した場合 true。</returns>
    bool InjectFrame(IReadOnlyList<InjectedPointer> frame);
}
