namespace VMonitor.Core.Models;

/// <summary>
/// スマホで発生したタッチイベント。
/// マルチタッチに対応し、すべてのタッチポイントを単一メッセージで送信する。
/// </summary>
public class TouchEvent
{
    /// <summary>このイベントに含まれるすべてのタッチポイント（マルチタッチ対応）。</summary>
    public required IReadOnlyList<TouchPoint> Points { get; init; }

    /// <summary>イベント発生時刻（Unix マイクロ秒）。</summary>
    public required long TimestampUs { get; init; }

    /// <summary>イベント発生時のスマホの画面向き。</summary>
    public required Orientation CurrentOrientation { get; init; }
}

/// <summary>個別のタッチポイント情報。</summary>
public class TouchPoint
{
    /// <summary>タッチポイントの識別子（マルチタッチで各指を区別するために使用）。</summary>
    public required int Id { get; init; }

    /// <summary>スマホ画面上の正規化 X 座標（[0.0, 1.0]、左端が 0.0）。</summary>
    public required double X { get; init; }

    /// <summary>スマホ画面上の正規化 Y 座標（[0.0, 1.0]、上端が 0.0）。</summary>
    public required double Y { get; init; }

    /// <summary>タッチ圧力（[0.0, 1.0]）。圧力センサーのないデバイスでは 1.0 固定。</summary>
    public required double Pressure { get; init; }

    /// <summary>タッチの現在フェーズ。</summary>
    public required TouchPhase Phase { get; init; }

    /// <summary>ペン（スタイラス）による接触か。false なら指。</summary>
    /// <remarks>
    /// Windows はタッチとペンを別の入力として扱う。ペンとして注入すれば
    /// 筆圧や傾きを見るアプリが本来の動きをし、手のひらが当たっても
    /// 線にならない。既定は false（古い端末は種別を送ってこない）。
    /// </remarks>
    public bool IsPen { get; init; }

    /// <summary>ペンを右へ倒した角度（度、-90〜90）。立てていれば 0。</summary>
    /// <remarks>
    /// Windows の POINTER_PEN_INFO と同じ取りかた。指では常に 0。
    /// 傾きを送ってこない端末でも 0 になる。
    /// </remarks>
    public int TiltX { get; init; }

    /// <summary>ペンを手前（画面の下）へ倒した角度（度、-90〜90）。</summary>
    public int TiltY { get; init; }
}

/// <summary>タッチポイントのライフサイクルフェーズ。</summary>
public enum TouchPhase
{
    /// <summary>タッチ開始（指が画面に触れた）。</summary>
    Began,

    /// <summary>タッチ移動（指が画面上を移動した）。</summary>
    Moved,

    /// <summary>タッチ終了（指が画面から離れた）。</summary>
    Ended,

    /// <summary>タッチキャンセル（システムによって中断された）。</summary>
    Cancelled,

    /// <summary>触れずに近づいている（ペンのホバー）。</summary>
    /// <remarks>
    /// Windows はペンが近づくと、触れる前から位置を示す丸を出す。
    /// これがあると狙った場所に下ろせる。
    ///
    /// 末尾に足すこと。序数がそのまま通信の値になっているので、
    /// 途中に入れると端末側と食い違う。
    /// </remarks>
    Hovered
}
