using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

// VMonitor.Session 名前空間と VMonitor.Core.Models.Session 型の名前衝突を回避するためにエイリアスを使用。
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.Session;

/// <summary>
/// <see cref="ISessionManager"/> の実装。
/// セッションの確立・終了・指数バックオフによる再接続を管理する。
/// </summary>
public sealed class SessionManager : ISessionManager
{
    // ── 定数 ───────────────────────────────────────────────────────────────

    /// <summary>セッション確立タイムアウト（10 秒）。</summary>
    private static readonly TimeSpan EstablishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>再接続バックオフの初回待機時間。</summary>
    private static readonly TimeSpan BackoffInitial = TimeSpan.FromSeconds(1);

    /// <summary>再接続バックオフの最大待機時間。</summary>
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(5);

    /// <summary>バックオフの乗数。</summary>
    private const double BackoffMultiplier = 2.0;

    // ── 状態 ───────────────────────────────────────────────────────────────

    private readonly ITransport _transport;
    private readonly IVirtualDisplayDriver _vdd;
    private readonly IWindowsInkInjector? _inkInjector;
    private readonly IDisplaySettingsManager? _displaySettingsManager;
    private readonly DisplayMode _defaultDisplayMode;

    /// <summary>
    /// アクティブなセッションを SessionId で管理する辞書。
    /// SessionModel レコードは不変なので、状態変更時は新しいインスタンスで上書きする。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SessionModel> _sessions = new();

    // ── コンストラクタ ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SessionManager"/> を初期化する。
    /// </summary>
    /// <param name="transport">セッション確立・再接続に使用するトランスポート。</param>
    /// <param name="vdd">
    /// セッション確立時に仮想ディスプレイを作成し、セッション終了・タイムアウト時に削除するドライバ。
    /// </param>
    /// <param name="inkInjector">
    /// 向き変更時にタッチ座標変換行列を更新するインジェクター。
    /// null の場合は向き変更イベントへの応答を行わない。
    /// </param>
    public SessionManager(ITransport transport, IVirtualDisplayDriver vdd, IWindowsInkInjector? inkInjector = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _vdd = vdd ?? throw new ArgumentNullException(nameof(vdd));
        _inkInjector = inkInjector;

        // 向き変更イベントを購読する（Requirements 6.6）
        // VDD が解像度・向きを更新したときに、タッチ入力インジェクターの変換行列を更新する。
        _vdd.ResolutionUpdated += OnVddResolutionUpdated;
    }

    // ── ISessionManager ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public event EventHandler<SessionDisconnectedEventArgs>? SessionDisconnected;

    /// <summary>
    /// 指定デバイスとのセッションを確立する。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>セッションを <see cref="SessionState.Connecting"/> 状態で作成し辞書に追加する。</item>
    ///   <item>10 秒タイムアウト付きで <see cref="ITransport.ConnectAsync"/> を呼び出す。</item>
    ///   <item>成功時は状態を <see cref="SessionState.Active"/> に遷移して返す。</item>
    ///   <item>タイムアウト時は辞書から削除し <see cref="TimeoutException"/> を投げる。</item>
    /// </list>
    /// </remarks>
    /// <param name="device">接続対象のデバイス情報。</param>
    /// <param name="ct">外部キャンセルトークン。</param>
    /// <returns>確立された <see cref="SessionModel"/>（State = Active）。</returns>
    /// <exception cref="TimeoutException">10 秒以内にセッションを確立できなかった場合。</exception>
    public async Task<SessionModel> EstablishSessionAsync(DeviceInfo device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        // Connecting 状態でセッションを生成して辞書に登録
        var session = new SessionModel(
            SessionId: Guid.NewGuid(),
            DeviceId: device.Id,
            Transport: _transport.Type,
            State: SessionState.Connecting,
            EstablishedAt: DateTimeOffset.UtcNow,
            DisplayHandle: new VirtualDisplayHandle(Guid.Empty));

        _sessions[session.SessionId] = session;

        // 10 秒タイムアウトを外部 CancellationToken とリンクして作成
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(EstablishTimeout);

        try
        {
            // ITransport の実装はエンドポイントを必要とするが、SessionManager は
            // トランスポート抽象を通じて接続するため、プレースホルダーエンドポイントを使用する。
            // 実際の接続先は ITransport 実装が内部で解決する想定。
            var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
            await _transport.ConnectAsync(endpoint, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 外部 CancellationToken ではなくタイムアウトによるキャンセル
            _sessions.TryRemove(session.SessionId, out _);
            throw new TimeoutException("セッション確立タイムアウト (10 秒)");
        }
        catch
        {
            // その他の例外（外部キャンセルを含む）でも辞書から除去してから再スロー
            _sessions.TryRemove(session.SessionId, out _);
            throw;
        }

        // 仮想ディスプレイを作成してハンドルを取得する（Requirements 2.5, 3.1）
        var displaySpec = BuildDisplaySpec(device);
        var displayHandle = await _vdd.CreateDisplayAsync(displaySpec);

        // セッション確立時に初期変換行列を設定する（Requirements 6.6）
        // VDD の ResolutionUpdated イベントが発火される前に確実に初期値を設定する。
        _inkInjector?.UpdateTransform(displaySpec.Resolution, displaySpec.Orientation);

        // Active 状態に遷移し、仮想ディスプレイハンドルを設定する
        var activeSession = session with
        {
            State = SessionState.Active,
            DisplayHandle = displayHandle
        };
        _sessions[session.SessionId] = activeSession;

        return activeSession;
    }

    /// <summary>
    /// デバイス情報から仮想ディスプレイ仕様を構築する。
    /// デバイスの物理解像度と向きをもとに <see cref="DisplaySpec"/> を生成する。
    /// </summary>
    /// <param name="device">接続デバイスの情報。</param>
    /// <returns>仮想ディスプレイ仕様。</returns>
    private static DisplaySpec BuildDisplaySpec(DeviceInfo device)
    {
        var resolution = device.PhysicalResolution;

        // デバイスの物理解像度から向きを判定する
        var orientation = resolution.Width >= resolution.Height
            ? Orientation.Landscape
            : Orientation.Portrait;

        return new DisplaySpec(
            Resolution: resolution,
            RefreshRateHz: 60,
            Orientation: orientation,
            Mode: DisplayMode.Extend);
    }

    /// <summary>
    /// 指定セッションを正常終了する。
    /// </summary>
    /// <remarks>
    /// 状態を <see cref="SessionState.Terminated"/> に遷移し、
    /// 仮想ディスプレイを削除したあと
    /// <see cref="ITransport.DisconnectAsync"/> を呼び出し、辞書から削除する。
    /// </remarks>
    /// <param name="session">終了するセッション。</param>
    public async Task TerminateSessionAsync(SessionModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var terminated = session with { State = SessionState.Terminated };
        _sessions[session.SessionId] = terminated;

        // 仮想ディスプレイを削除する（Requirements 3.5）
        if (session.DisplayHandle.Value != Guid.Empty)
        {
            await _vdd.RemoveDisplayAsync(session.DisplayHandle);
        }

        try
        {
            await _transport.DisconnectAsync();
        }
        finally
        {
            _sessions.TryRemove(session.SessionId, out _);
        }
    }

    /// <summary>
    /// 切断されたセッションへの再接続を試みる。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>指数バックオフ（初回 1s、乗数 2x、上限 5s）で再試行する。</item>
    ///   <item>各試行では状態を <see cref="SessionState.Reconnecting"/> に設定してから接続を試みる。</item>
    ///   <item>成功時は <see cref="SessionState.Active"/> に遷移し <see cref="ReconnectResult.Success"/> を返す。</item>
    ///   <item><paramref name="timeout"/> 経過後は <see cref="SessionState.Terminated"/> に遷移し、
    ///         <see cref="SessionDisconnected"/> イベントを発火して <see cref="ReconnectResult.TimedOut"/> を返す。</item>
    /// </list>
    /// </remarks>
    /// <param name="session">再接続対象のセッション。</param>
    /// <param name="timeout">再接続を試みる最大時間。</param>
    /// <param name="ct">外部キャンセルトークン。</param>
    /// <returns>再接続の結果。</returns>
    public async Task<ReconnectResult> TryReconnectAsync(SessionModel session, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sw = Stopwatch.StartNew();
        var backoff = BackoffInitial;
        var currentSession = session;

        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            // Reconnecting 状態に遷移
            currentSession = currentSession with { State = SessionState.Reconnecting };
            _sessions[currentSession.SessionId] = currentSession;

            // 残り時間を計算してタイムアウト付きで接続試行
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(remaining);

            try
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
                await _transport.ConnectAsync(endpoint, attemptCts.Token);

                // 成功: Active 状態に遷移
                currentSession = currentSession with { State = SessionState.Active };
                _sessions[currentSession.SessionId] = currentSession;
                return ReconnectResult.Success;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 外部キャンセル: 再試行を中止
                break;
            }
            catch
            {
                // 接続失敗: バックオフ待機して再試行
            }

            // バックオフ待機（残り時間を超えないよう制限）
            var waitTime = remaining < backoff ? remaining : backoff;
            if (waitTime > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(waitTime, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // 次回のバックオフ時間を計算（上限 BackoffMax）
            var nextMs = backoff.TotalMilliseconds * BackoffMultiplier;
            backoff = TimeSpan.FromMilliseconds(Math.Min(nextMs, BackoffMax.TotalMilliseconds));
        }

        // タイムアウト: Terminated 状態に遷移してイベントを発火
        var terminatedSession = currentSession with { State = SessionState.Terminated };
        _sessions[terminatedSession.SessionId] = terminatedSession;

        // タイムアウト時に仮想ディスプレイを削除する（Requirements 3.5）
        if (terminatedSession.DisplayHandle.Value != Guid.Empty)
        {
            await _vdd.RemoveDisplayAsync(terminatedSession.DisplayHandle);
        }

        OnSessionDisconnected(terminatedSession, reason: null);

        return ReconnectResult.TimedOut;
    }

    /// <summary>アクティブなセッションのスナップショットを返す（テスト・デバッグ用）。</summary>
    public IReadOnlyDictionary<Guid, SessionModel> GetActiveSessions()
        => _sessions.ToDictionary(kv => kv.Key, kv => kv.Value);

    // ── プライベートヘルパー ───────────────────────────────────────────────

    /// <summary>SessionDisconnected イベントを発火する。</summary>
    private void OnSessionDisconnected(SessionModel session, Exception? reason)
    {
        SessionDisconnected?.Invoke(this, new SessionDisconnectedEventArgs
        {
            Session = session,
            Reason = reason
        });
    }

    /// <summary>
    /// VDD の解像度・向き更新イベントハンドラー。
    /// スマートフォンの画面向きが変更されたとき、タッチ入力インジェクターの
    /// 座標変換行列を新しい向き・解像度で更新する（Requirements 6.6）。
    /// </summary>
    /// <param name="sender">イベント送信元（IVirtualDisplayDriver）。</param>
    /// <param name="e">更新後の解像度と向きを含むイベントデータ。</param>
    private void OnVddResolutionUpdated(object? sender, DisplayResolutionUpdatedEventArgs e)
    {
        _inkInjector?.UpdateTransform(e.Resolution, e.Orientation);
    }
}
