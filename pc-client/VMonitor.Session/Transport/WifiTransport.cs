using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>
/// Wi-Fi (mDNS + TCP/TLS) を使用した <see cref="ITransport"/> の実装。
/// <para>
/// 単一の TLS TCP コネクション上で映像・タッチ・制御の 3 チャンネルを多重化する。
/// </para>
/// </summary>
/// <remarks>
/// フレーム構造（送受信共通）:
/// <code>
/// ┌─────────────────────────────────────────────┐
/// │ ChannelId (1 byte)                          │
/// │ PayloadLength (4 bytes, big-endian uint32)  │
/// │ Payload (PayloadLength bytes)               │
/// └─────────────────────────────────────────────┘
/// </code>
/// </remarks>
public sealed class WifiTransport : ITransport, IAsyncDisposable
{
    // ── 定数 ───────────────────────────────────
    /// <summary>フレームヘッダーのサイズ: ChannelId(1) + Length(4)。</summary>
    private const int FrameHeaderSize = 5;

    /// <summary>デフォルト推定帯域幅（10 Mbps）。</summary>
    private const long DefaultBandwidthBps = 10_000_000L;

    /// <summary>送受信バッファサイズ。</summary>
    private const int BufferSize = 64 * 1024; // 64 KB

    // ── 状態 ───────────────────────────────────
    private TcpClient? _tcpClient;
    private SslStream? _sslStream;
    private System.Net.Sockets.NetworkStream? _plainStream; // TLS なし素 TCP 用
    private bool _disposed;
    private bool _acceptedByServer; // AcceptPlain/AcceptAsync で受け入れ済みの場合 true

    // 帯域推定用
    private long _totalBytesSent;
    private long _sendStartTickMs = -1;
    private long _estimatedBandwidthBps = DefaultBandwidthBps;

    // 送信の直列化ロック（SslStream はスレッドセーフでないため）
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // ── ITransport ────────────────────────────

    /// <inheritdoc/>
    public TransportType Type => TransportType.WiFi;

    /// <inheritdoc/>
    public long EstimatedBandwidthBps => Volatile.Read(ref _estimatedBandwidthBps);

    /// <inheritdoc/>
    /// <summary>
    /// 指定エンドポイントへ TCP 接続を確立し、TLS ハンドシェイクを実行する。
    /// AcceptPlain/AcceptAsync で既に接続済みの場合は何もしない。
    /// </summary>
    /// <param name="endpoint">接続先 <see cref="IPEndPoint"/>。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task ConnectAsync(EndPoint endpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // サーバー側で既に Accept 済みの場合は ConnectAsync をスキップする
        if (_acceptedByServer)
            return;

        if (endpoint is not IPEndPoint ipEndPoint)
            throw new ArgumentException($"WifiTransport は IPEndPoint のみサポートします。受け取った型: {endpoint.GetType().Name}", nameof(endpoint));

        _tcpClient = new TcpClient
        {
            ReceiveBufferSize = BufferSize,
            SendBufferSize = BufferSize,
            NoDelay = true  // 低遅延のため Nagle アルゴリズムを無効化
        };

        await _tcpClient.ConnectAsync(ipEndPoint.Address, ipEndPoint.Port, ct);

        // TLS ハンドシェイク
        // 開発・テスト環境では自己署名証明書を許容する
        // 本番環境では RemoteCertificateValidationCallback を厳密に実装する
        _sslStream = new SslStream(
            _tcpClient.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: ValidateServerCertificate);

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = ipEndPoint.Address.ToString(),
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        await _sslStream.AuthenticateAsClientAsync(sslOptions, ct);

        _sendStartTickMs = Environment.TickCount64;
    }

    /// <inheritdoc/>
    /// <summary>
    /// データをフレーミングして送信する。
    /// フレーム = ChannelId(1byte) + PayloadLength(4bytes BE) + Payload。
    /// </summary>
    public async Task SendAsync(ReadOnlyMemory<byte> data, ChannelId channel, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();

        // ヘッダー構築
        var header = new byte[FrameHeaderSize];
        header[0] = (byte)channel;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)data.Length);

        await _sendLock.WaitAsync(ct);
        try
        {
            Stream stream = (Stream?)_sslStream ?? _plainStream!;
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(data, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }

        // 帯域推定の更新
        UpdateBandwidthEstimate(FrameHeaderSize + data.Length);
    }

    /// <inheritdoc/>
    /// <summary>
    /// 受信データを非同期ストリームとして返す。
    /// 接続が切断されるかキャンセルされるまでフレームを読み続ける。
    /// </summary>
    public async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();

        var headerBuffer = new byte[FrameHeaderSize];
        Stream stream = (Stream?)_sslStream ?? _plainStream!;

        while (!ct.IsCancellationRequested)
        {
            // ヘッダー読み取り
            int headerRead = 0;
            while (headerRead < FrameHeaderSize)
            {
                int n = await stream.ReadAsync(
                    headerBuffer.AsMemory(headerRead, FrameHeaderSize - headerRead), ct);
                if (n == 0) yield break;  // 接続切断
                headerRead += n;
            }

            var channelId = (ChannelId)headerBuffer[0];
            var payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(1));

            // ペイロード読み取り
            var payload = new byte[payloadLength];
            int payloadRead = 0;
            while (payloadRead < payloadLength)
            {
                int n = await stream.ReadAsync(
                    payload.AsMemory(payloadRead, payloadLength - payloadRead), ct);
                if (n == 0) yield break;  // 接続切断
                payloadRead += n;
            }

            yield return (channelId, payload.AsMemory());
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        if (_sslStream is not null)
        {
            try
            {
                await _sslStream.ShutdownAsync();
            }
            catch
            {
                // シャットダウン失敗は無視して強制クローズ
            }
            await _sslStream.DisposeAsync();
            _sslStream = null;
        }

        if (_plainStream is not null)
        {
            await _plainStream.DisposeAsync();
            _plainStream = null;
        }

        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    // ── サーバーサイド接続受け入れ ───────────────

    /// <summary>
    /// PC クライアント（サーバー）側: TcpListener が受け入れた接続を TLS でラップして
    /// このトランスポートに割り当てる。
    /// </summary>
    /// <param name="client">受け入れ済みの <see cref="TcpClient"/>。</param>
    /// <param name="serverCertificate">サーバー証明書。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task AcceptAsync(TcpClient client, X509Certificate2 serverCertificate, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tcpClient = client;
        _tcpClient.NoDelay = true;

        _sslStream = new SslStream(
            _tcpClient.GetStream(),
            leaveInnerStreamOpen: false);

        var sslOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCertificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        };

        await _sslStream.AuthenticateAsServerAsync(sslOptions, ct);
        _sendStartTickMs = Environment.TickCount64;
    }

    /// <summary>
    /// 端末が待ち受けているところへ、TLS なし素 TCP で繋ぎにいく（開発段階用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通常は端末から PC へ繋いでくるが、PC の前にいるときは PC 側から
    /// 始められた方が早い。その向き用の入口。
    /// </para>
    /// <para>
    /// 端末側の待ち受けは素 TCP なので、<see cref="ConnectAsync"/> の
    /// TLS ハンドシェイクは使えない。
    /// </para>
    /// </remarks>
    /// <param name="endpoint">端末の待ち受け先。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task ConnectPlainAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tcpClient = new TcpClient
        {
            ReceiveBufferSize = BufferSize,
            SendBufferSize = BufferSize,
            NoDelay = true  // 低遅延のため Nagle アルゴリズムを無効化
        };

        await _tcpClient.ConnectAsync(endpoint.Address, endpoint.Port, ct);

        _plainStream = _tcpClient.GetStream();
        _sendStartTickMs = Environment.TickCount64;
    }

    /// <summary>
    /// PC クライアント（サーバー）側: TcpListener が受け入れた接続を TLS なし素 TCP として
    /// このトランスポートに割り当てる（開発段階用）。
    /// </summary>
    /// <param name="client">受け入れ済みの <see cref="TcpClient"/>。</param>
    public void AcceptPlain(TcpClient client)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tcpClient = client;
        _tcpClient.NoDelay = true;

        _plainStream = _tcpClient.GetStream();
        _sendStartTickMs = Environment.TickCount64;
        _acceptedByServer = true;
    }

    // ── IAsyncDisposable ─────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _sendLock.Dispose();
    }

    // ── プライベートヘルパー ─────────────────

    private void EnsureConnected()
    {
        if ((_sslStream is null && _plainStream is null) || _tcpClient is null)
            throw new InvalidOperationException("接続が確立されていません。ConnectAsync または AcceptAsync / AcceptPlain を先に呼び出してください。");
    }

    /// <summary>送受信バイト数をもとに帯域を推定する。</summary>
    private void UpdateBandwidthEstimate(long bytesSent)
    {
        Interlocked.Add(ref _totalBytesSent, bytesSent);

        var elapsedMs = Environment.TickCount64 - _sendStartTickMs;
        if (elapsedMs > 0)
        {
            // bps = bytes * 8 / seconds
            var bps = Interlocked.Read(ref _totalBytesSent) * 8L * 1000L / elapsedMs;
            Interlocked.Exchange(ref _estimatedBandwidthBps, bps);
        }
    }

    /// <summary>
    /// TLS サーバー証明書の検証コールバック。
    /// 開発・テスト環境では自己署名証明書を許容する。
    /// 本番環境では信頼されたルート CA チェーン検証に差し替える。
    /// </summary>
    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // 本番: sslPolicyErrors == None のみ許可
        // 開発: 自己署名も許可（RemoteCertificateChainErrors を無視）
        return sslPolicyErrors == SslPolicyErrors.None
            || sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors;
    }
}
