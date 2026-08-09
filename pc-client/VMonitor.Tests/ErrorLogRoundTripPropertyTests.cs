using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Interfaces;
using VMonitor.Session;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 23: エラーログの記録

/// <summary>
/// Property 23: エラーログの記録
/// Validates: Requirements 9.4
///
/// 任意のエラーイベント（エラーコード・メッセージ・タイムスタンプ）に対して、
/// ログ記録後にログファイルからそのエラー情報が読み取れなければならない（ログのラウンドトリップ）。
/// </summary>
public class ErrorLogRoundTripPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public ErrorLogRoundTripPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vmonitor_errlog_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string GetTempLogPath() =>
        Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".log");

    /// <summary>
    /// Property 23: 任意のコンポーネント名・エラーメッセージ・エラーコードに対して、
    /// VMonitorLogger.Error(component, message, errorCode) を呼び出した後、
    /// ログファイルの末尾行が対応する JSON エントリを含むことを検証する。
    ///
    /// パラメーター:
    ///   componentRaw - FsCheck が生成する非null文字列（コンポーネント名として使用）
    ///   messageRaw   - FsCheck が生成する非null文字列（ログメッセージとして使用）
    ///   errorCodeRaw - FsCheck が生成する非null文字列（エラーコードとして使用）
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ErrorLogRoundTrip(NonNull<string> componentRaw, NonNull<string> messageRaw, NonNull<string> errorCodeRaw)
    {
        var component = componentRaw.Get;
        var message = messageRaw.Get;
        var errorCode = errorCodeRaw.Get;

        var logPath = GetTempLogPath();

        // ERROR エントリをログファイルに書き込む
        using (var logger = new VMonitorLogger(logPath))
        {
            logger.Error(component, message, errorCode);
        }

        // ログファイルが存在することを確認
        if (!File.Exists(logPath))
            return false;

        // ログファイルから全行を読み込み、最後の非空行を取得する
        var lines = File.ReadAllLines(logPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
        if (lines.Length == 0)
            return false;

        var lastLine = lines[^1];

        // JSON としてパースする
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(lastLine).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }

        // level フィールドが "ERROR" であること
        if (!root.TryGetProperty("level", out var levelEl) || levelEl.GetString() != "ERROR")
            return false;

        // component フィールドが記録した値と一致すること
        if (!root.TryGetProperty("component", out var componentEl) || componentEl.GetString() != component)
            return false;

        // message フィールドが記録した値と一致すること
        if (!root.TryGetProperty("message", out var messageEl) || messageEl.GetString() != message)
            return false;

        // errorCode フィールドが記録した値と一致すること
        if (!root.TryGetProperty("errorCode", out var errorCodeEl) || errorCodeEl.GetString() != errorCode)
            return false;

        // timestamp フィールドが存在し、パース可能な日時であること
        if (!root.TryGetProperty("timestamp", out var tsEl))
            return false;
        var tsStr = tsEl.GetString();
        if (string.IsNullOrEmpty(tsStr) || !DateTimeOffset.TryParse(tsStr, out _))
            return false;

        return true;
    }

    /// <summary>
    /// Property 23（追加検証）: Log(LogLevel.Error, ...) での書き込みも同様に
    /// ログのラウンドトリップが成立することを確認する。
    ///
    /// パラメーター:
    ///   componentRaw - FsCheck が生成する非null文字列
    ///   messageRaw   - FsCheck が生成する非null文字列
    ///   errorCodeRaw - FsCheck が生成する非null文字列
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ErrorLogRoundTripViaLogMethod(NonNull<string> componentRaw, NonNull<string> messageRaw, NonNull<string> errorCodeRaw)
    {
        var component = componentRaw.Get;
        var message = messageRaw.Get;
        var errorCode = errorCodeRaw.Get;

        var logPath = GetTempLogPath();

        using (var logger = new VMonitorLogger(logPath))
        {
            logger.Log(LogLevel.Error, component, message, errorCode);
        }

        if (!File.Exists(logPath))
            return false;

        var lines = File.ReadAllLines(logPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
        if (lines.Length == 0)
            return false;

        var lastLine = lines[^1];

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(lastLine).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }

        return root.TryGetProperty("level", out var lvl) && lvl.GetString() == "ERROR"
            && root.TryGetProperty("component", out var comp) && comp.GetString() == component
            && root.TryGetProperty("message", out var msg) && msg.GetString() == message
            && root.TryGetProperty("errorCode", out var ec) && ec.GetString() == errorCode;
    }
}
