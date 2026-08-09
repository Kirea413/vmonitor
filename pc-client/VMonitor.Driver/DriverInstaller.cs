using System.Diagnostics;

namespace VMonitor.Driver;

/// <summary>
/// IddCx ドライバのインストール・アンインストールを管理するクラス。
/// pnputil.exe を使用してドライバの自動インストールを行う。
/// </summary>
public class DriverInstaller
{
    /// <summary>
    /// ドライバをインストールし、インストール結果を返す。
    /// 成功時は <see cref="DriverInstallResult.ErrorMessage"/> が空文字列、
    /// 失敗時は具体的なエラーと対処手順を含む非空文字列になる。
    /// </summary>
    /// <param name="infPath">インストールする .inf ファイルのパス。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>インストール操作の結果。</returns>
    public async Task<DriverInstallResult> InstallDriverAsync(string infPath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 環境ではスキップ（テスト・クロスプラットフォームビルド対応）
            return new DriverInstallResult(true, string.Empty, 0);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        var exitCode = process.ExitCode;
        var errorMessage = GenerateErrorMessage(exitCode, stderr);
        return new DriverInstallResult(exitCode == 0, errorMessage, exitCode);
    }

    /// <summary>
    /// ドライバをアンインストールする。
    /// </summary>
    /// <param name="infPath">アンインストールする .inf ファイルのパス。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>アンインストール操作の結果。</returns>
    public async Task<DriverInstallResult> UninstallDriverAsync(string infPath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DriverInstallResult(true, string.Empty, 0);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/delete-driver \"{infPath}\" /uninstall",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        var exitCode = process.ExitCode;
        var errorMessage = GenerateErrorMessage(exitCode, stderr);
        return new DriverInstallResult(exitCode == 0, errorMessage, exitCode);
    }

    /// <summary>
    /// エラーコードから具体的なエラーメッセージと対処手順を生成する。
    /// 成功時（<paramref name="exitCode"/> == 0）は空文字列を返す。
    /// </summary>
    /// <param name="exitCode">pnputil.exe の終了コード。</param>
    /// <param name="stderr">標準エラー出力の内容。</param>
    /// <returns>
    /// 成功時は空文字列。失敗時はエラー内容と対処手順を含む非空文字列。
    /// </returns>
    public static string GenerateErrorMessage(int exitCode, string stderr)
    {
        // 成功 (0) の場合はエラーメッセージなし
        if (exitCode == 0)
            return string.Empty;

        // 0x5 = ERROR_ACCESS_DENIED (5): 管理者権限が必要
        if (exitCode == 0x5)
            return "管理者権限が必要です。対処手順: アプリを管理者として実行してください。";

        // 0x103 = ERROR_NO_MORE_ITEMS (259): 既にインストール済み
        if (exitCode == 0x103)
            return "ドライバは既にインストールされています。対処手順: アンインストール後に再試行してください。";

        // その他のエラー
        return $"ドライバのインストールに失敗しました（エラーコード: {exitCode}）。対処手順: ログファイルを確認し、サポートにお問い合わせください。詳細: {stderr}";
    }
}
