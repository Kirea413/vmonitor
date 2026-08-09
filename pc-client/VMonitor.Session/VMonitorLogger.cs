using System.Text.Json;
using System.Text.Json.Serialization;
using VMonitor.Core.Interfaces;

namespace VMonitor.Session;

/// <summary>
/// 構造化 JSON エラーロガーの実装。
///
/// ログは <c>%APPDATA%\vmonitor\logs\vmonitor.log</c> に JSON Lines 形式（1 エントリ 1 行）で記録される。
/// ファイルサイズが 10MB を超えた場合、以下のローテーションを行う:
///   .log.5  → 削除
///   .log.4  → .log.5
///   .log.3  → .log.4
///   .log.2  → .log.3
///   .log.1  → .log.2
///   .log    → .log.1
///   （新しい .log を作成）
/// 最大 5 世代を保持する。
///
/// スレッドセーフ: <see cref="SemaphoreSlim"/> で書き込みを直列化する。
/// </summary>
public sealed class VMonitorLogger : IVMonitorLogger
{
    private const long RotationThresholdBytes = 10 * 1024 * 1024; // 10 MB
    private const int MaxGenerations = 5;

    private static readonly string DefaultLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "vmonitor",
        "logs",
        "vmonitor.log");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _logPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// デフォルトパス (<c>%APPDATA%\vmonitor\logs\vmonitor.log</c>) を使用して初期化する。
    /// </summary>
    public VMonitorLogger() : this(DefaultLogPath) { }

    /// <summary>
    /// テスト用にログファイルパスを指定して初期化する。
    /// </summary>
    /// <param name="logPath">ログファイルの絶対パス</param>
    public VMonitorLogger(string logPath)
    {
        _logPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
    }

    /// <inheritdoc/>
    public void Log(
        LogLevel level,
        string component,
        string message,
        string? errorCode = null,
        object? details = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Level = level.ToString().ToUpperInvariant(),
            Component = component ?? string.Empty,
            Message = message ?? string.Empty,
            ErrorCode = errorCode,
            Details = details,
        };

        var line = JsonSerializer.Serialize(entry, JsonOptions);

        _lock.Wait();
        try
        {
            WriteLineInternal(line);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public void Debug(string component, string message, string? errorCode = null, object? details = null)
        => Log(LogLevel.Debug, component, message, errorCode, details);

    /// <inheritdoc/>
    public void Info(string component, string message, string? errorCode = null, object? details = null)
        => Log(LogLevel.Info, component, message, errorCode, details);

    /// <inheritdoc/>
    public void Warn(string component, string message, string? errorCode = null, object? details = null)
        => Log(LogLevel.Warn, component, message, errorCode, details);

    /// <inheritdoc/>
    public void Error(string component, string message, string? errorCode = null, object? details = null)
        => Log(LogLevel.Error, component, message, errorCode, details);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    // --- private helpers ---

    private void WriteLineInternal(string line)
    {
        // ログディレクトリを作成
        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // ローテーション判定: ファイルが 10MB 超の場合はローテートしてから書き込む
        if (File.Exists(_logPath))
        {
            var info = new FileInfo(_logPath);
            if (info.Length >= RotationThresholdBytes)
                RotateLogs();
        }

        // JSON Lines 形式で追記
        using var writer = new StreamWriter(_logPath, append: true, encoding: System.Text.Encoding.UTF8);
        writer.WriteLine(line);
    }

    /// <summary>
    /// ログファイルをローテートする。
    /// .log.5 を削除し、.log.4→.log.5、...、.log→.log.1 とリネームする。
    /// </summary>
    private void RotateLogs()
    {
        // 最古の世代（.log.5）を削除
        var oldest = _logPath + $".{MaxGenerations}";
        if (File.Exists(oldest))
            File.Delete(oldest);

        // .log.4 → .log.5、.log.3 → .log.4、... 、.log.1 → .log.2
        for (int gen = MaxGenerations - 1; gen >= 1; gen--)
        {
            var src = _logPath + $".{gen}";
            var dst = _logPath + $".{gen + 1}";
            if (File.Exists(src))
                File.Move(src, dst);
        }

        // .log → .log.1
        File.Move(_logPath, _logPath + ".1");
    }

    // --- JSON entry DTO ---

    private sealed class LogEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorCode { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Details { get; set; }
    }
}
