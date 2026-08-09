using System.Diagnostics;
using VMonitor.Core.Interfaces;

namespace VMonitor.Session;

/// <summary>
/// ドライバ障害回復サービス。
///
/// 仮想ディスプレイドライバ（VDD）の予期停止を WMI イベントで検出し、
/// <c>pnputil /restart-device</c> で最大 3 回（5 秒間隔）再起動を試みる。
/// 再起動に失敗した場合は <see cref="DriverRecoveryFailed"/> イベントを発火して
/// ユーザーへの ERROR レベル通知を行う。
///
/// Requirement 9.3: 仮想ディスプレイドライバが予期せず停止した場合、
/// PC クライアントはドライバの再起動を試み、再起動に失敗した場合はユーザーにエラーを通知する。
/// </summary>
public sealed class DriverRecoveryService : IDisposable
{
    // ── 定数 ───────────────────────────────────────────────────────────────

    /// <summary>再起動試行の最大回数。</summary>
    public const int MaxRetryCount = 3;

    /// <summary>再起動試行間のインターバル（5 秒）。</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>ログコンポーネント名。</summary>
    private const string ComponentName = "DriverRecoveryService";

    // ── 依存関係 ───────────────────────────────────────────────────────────

    private readonly IWmiDriverEventSource _wmiSource;
    private readonly IDriverProcessRunner _processRunner;
    private readonly IVMonitorLogger? _logger;
    private readonly string _deviceInstanceId;

    // ── 状態 ───────────────────────────────────────────────────────────────

    private bool _disposed;

    // ── コンストラクタ ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="DriverRecoveryService"/> を初期化する。
    /// </summary>
    /// <param name="wmiSource">WMI ドライバ停止イベントのソース。</param>
    /// <param name="processRunner">pnputil プロセスの実行を担うランナー。</param>
    /// <param name="deviceInstanceId">監視・再起動対象のデバイスインスタンス ID。</param>
    /// <param name="logger">エラーロガー（省略可）。</param>
    public DriverRecoveryService(
        IWmiDriverEventSource wmiSource,
        IDriverProcessRunner processRunner,
        string deviceInstanceId,
        IVMonitorLogger? logger = null)
    {
        _wmiSource = wmiSource ?? throw new ArgumentNullException(nameof(wmiSource));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _deviceInstanceId = deviceInstanceId ?? throw new ArgumentNullException(nameof(deviceInstanceId));
        _logger = logger;

        _wmiSource.DriverStopped += OnDriverStopped;
    }

    // ── イベント ───────────────────────────────────────────────────────────

    /// <summary>
    /// ドライバ再起動が最大試行回数内で成功しなかった場合に発生するイベント。
    /// UI 層がこのイベントを購読してユーザーへの通知を表示する。
    /// </summary>
    public event EventHandler<DriverRecoveryFailedEventArgs>? DriverRecoveryFailed;

    // ── WMI イベントハンドラー ─────────────────────────────────────────────

    /// <summary>
    /// WMI ドライバ停止イベントのハンドラー。
    /// ドライバ停止を検出し、非同期で回復処理を開始する。
    /// </summary>
    private void OnDriverStopped(object? sender, DriverStoppedEventArgs e)
    {
        // 対象デバイスの停止のみを処理する
        if (!string.Equals(e.DeviceInstanceId, _deviceInstanceId, StringComparison.OrdinalIgnoreCase))
            return;

        _logger?.Info(
            ComponentName,
            $"ドライバ停止を検出しました。デバイス: {e.DeviceInstanceId}。回復処理を開始します。");

        // fire-and-forget: イベントハンドラーは同期のため非同期回復をバックグラウンドで開始する
        _ = RecoverAsync(e.DeviceInstanceId);
    }

    // ── 回復ロジック ───────────────────────────────────────────────────────

    /// <summary>
    /// ドライバ回復処理を実行する。
    /// <c>pnputil /restart-device</c> を最大 <see cref="MaxRetryCount"/> 回試行し、
    /// 全て失敗した場合は <see cref="DriverRecoveryFailed"/> イベントを発火する。
    /// </summary>
    /// <param name="deviceInstanceId">再起動対象のデバイスインスタンス ID。</param>
    internal async Task RecoverAsync(string deviceInstanceId)
    {
        for (int attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            _logger?.Info(
                ComponentName,
                $"ドライバ再起動を試みます（試行 {attempt}/{MaxRetryCount}）。デバイス: {deviceInstanceId}");

            bool success = await _processRunner.RestartDeviceAsync(deviceInstanceId);

            if (success)
            {
                _logger?.Info(
                    ComponentName,
                    $"ドライバの再起動に成功しました（試行 {attempt}/{MaxRetryCount}）。デバイス: {deviceInstanceId}");
                return;
            }

            _logger?.Warn(
                ComponentName,
                $"ドライバ再起動に失敗しました（試行 {attempt}/{MaxRetryCount}）。デバイス: {deviceInstanceId}",
                errorCode: "VDD_RESTART_FAILED",
                details: new { attempt, deviceInstanceId });

            // 最後の試行の後はインターバル待機しない
            if (attempt < MaxRetryCount)
            {
                await Task.Delay(RetryInterval);
            }
        }

        // 全試行失敗: ERROR ログを記録して通知イベントを発火する
        _logger?.Error(
            ComponentName,
            $"ドライバの再起動に {MaxRetryCount} 回失敗しました。PC の再起動を案内してください。デバイス: {deviceInstanceId}",
            errorCode: "VDD_RESTART_FAILED",
            details: new { maxAttempts = MaxRetryCount, deviceInstanceId });

        OnDriverRecoveryFailed(deviceInstanceId);
    }

    /// <summary>DriverRecoveryFailed イベントを発火する。</summary>
    private void OnDriverRecoveryFailed(string deviceInstanceId)
    {
        DriverRecoveryFailed?.Invoke(this, new DriverRecoveryFailedEventArgs
        {
            DeviceInstanceId = deviceInstanceId,
            AttemptCount = MaxRetryCount
        });
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wmiSource.DriverStopped -= OnDriverStopped;
    }
}

/// <summary>
/// ドライバ再起動失敗イベントのデータ。
/// </summary>
public sealed class DriverRecoveryFailedEventArgs : EventArgs
{
    /// <summary>再起動に失敗したデバイスインスタンス ID。</summary>
    public required string DeviceInstanceId { get; init; }

    /// <summary>再起動を試みた回数（常に <see cref="DriverRecoveryService.MaxRetryCount"/>）。</summary>
    public required int AttemptCount { get; init; }
}
