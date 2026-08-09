namespace VMonitor.Core.Interfaces;

/// <summary>
/// ログレベルの列挙型。DEBUG / INFO / WARN / ERROR の 4 段階。
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// 構造化 JSON エラーロガーのインターフェース。
/// ログは %APPDATA%\vmonitor\logs\vmonitor.log に JSON Lines 形式で記録される。
/// </summary>
public interface IVMonitorLogger : IDisposable
{
    /// <summary>
    /// 指定したレベルでログエントリを記録する。
    /// </summary>
    /// <param name="level">ログレベル</param>
    /// <param name="component">ログを生成したコンポーネント名</param>
    /// <param name="message">ログメッセージ</param>
    /// <param name="errorCode">エラーコード（省略可）</param>
    /// <param name="details">追加の構造化詳細情報（省略可）</param>
    void Log(
        LogLevel level,
        string component,
        string message,
        string? errorCode = null,
        object? details = null);

    /// <summary>DEBUG レベルでログを記録する。</summary>
    void Debug(string component, string message, string? errorCode = null, object? details = null);

    /// <summary>INFO レベルでログを記録する。</summary>
    void Info(string component, string message, string? errorCode = null, object? details = null);

    /// <summary>WARN レベルでログを記録する。</summary>
    void Warn(string component, string message, string? errorCode = null, object? details = null);

    /// <summary>ERROR レベルでログを記録する。</summary>
    void Error(string component, string message, string? errorCode = null, object? details = null);
}
