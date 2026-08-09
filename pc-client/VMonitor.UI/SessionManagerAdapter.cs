using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Driver;
using VMonitor.Session;
using VMonitor.Session.Transport;
using System.Net;

using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.UI;

/// <summary>
/// UI 層が ISessionManager として使用できるよう SessionManager をラップするアダプター。
/// 接続ごとに新しい WifiTransport を生成してセッションを確立する。
/// </summary>
public sealed class SessionManagerAdapter : ISessionManager, IDisposable
{
    private readonly VirtualDisplayDriver _vdd;
    private readonly AuthManager _authManager;
    private readonly VMonitorLogger _logger;

    // アクティブなセッションと対応するマネージャーの辞書
    private readonly Dictionary<Guid, (SessionManager Manager, WifiTransport Transport)> _active = new();
    private readonly object _lock = new();

    public event EventHandler<SessionDisconnectedEventArgs>? SessionDisconnected;

    public SessionManagerAdapter(
        VirtualDisplayDriver vdd,
        AuthManager authManager,
        VMonitorLogger logger)
    {
        _vdd = vdd;
        _authManager = authManager;
        _logger = logger;
    }

    public async Task<SessionModel> EstablishSessionAsync(DeviceInfo device, CancellationToken ct)
    {
        var transport = new WifiTransport();

        var manager = new SessionManager(transport, _vdd);
        manager.SessionDisconnected += OnSessionDisconnected;

        var session = await manager.EstablishSessionAsync(device, ct);

        lock (_lock)
        {
            _active[session.SessionId] = (manager, transport);
        }

        _logger.Info("SessionManagerAdapter", $"Session established: {session.SessionId}");
        return session;
    }

    public async Task TerminateSessionAsync(SessionModel session)
    {
        SessionManager? manager = null;
        WifiTransport? transport = null;

        lock (_lock)
        {
            if (_active.TryGetValue(session.SessionId, out var entry))
            {
                manager = entry.Manager;
                transport = entry.Transport;
                _active.Remove(session.SessionId);
            }
        }

        if (manager != null)
        {
            manager.SessionDisconnected -= OnSessionDisconnected;
            await manager.TerminateSessionAsync(session);
        }

        if (transport != null)
            await transport.DisposeAsync();

        _logger.Info("SessionManagerAdapter", $"Session terminated: {session.SessionId}");
    }

    public async Task<ReconnectResult> TryReconnectAsync(
        SessionModel session, TimeSpan timeout, CancellationToken ct)
    {
        SessionManager? manager;
        lock (_lock)
        {
            if (!_active.TryGetValue(session.SessionId, out var entry))
                return ReconnectResult.Failed;
            manager = entry.Manager;
        }

        return await manager.TryReconnectAsync(session, timeout, ct);
    }

    private void OnSessionDisconnected(object? sender, SessionDisconnectedEventArgs e)
    {
        SessionDisconnected?.Invoke(this, e);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (manager, transport) in _active.Values)
            {
                manager.SessionDisconnected -= OnSessionDisconnected;
                transport.DisposeAsync().AsTask().Wait(500);
            }
            _active.Clear();
        }
    }
}
