namespace VMonitor.Driver;

/// <summary>
/// ドライバインストール・アンインストール操作の結果を表すレコード。
/// </summary>
/// <param name="Success">操作が成功したかどうか。</param>
/// <param name="ErrorMessage">
/// 成功時は空文字列。失敗時は具体的なエラー内容と対処手順を含む非空文字列。
/// </param>
/// <param name="ExitCode">pnputil.exe の終了コード。</param>
public record DriverInstallResult(
    bool Success,
    string ErrorMessage,
    int ExitCode);
