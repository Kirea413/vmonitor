using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>
/// USB 接続（Android: ADB TCP フォワード、iOS: libimobiledevice トンネル）を使用した
/// <see cref="ITransport"/> の実装。
/// <para>
/// Android の場合は <c>adb forward tcp:{AdbPort} tcp:{AdbPort}</c> でトンネルを確立し、
/// ループバック TCP 接続を通じて通信する。
/// iOS の場合は libimobiledevice が同じポートでトンネルを提供することを前提とする。
/// </para>
/// </summary>
/// <remarks>
/// フレーム構造（WifiTransport と共通）:
/// <code>
/// ┌─────────────────────────────────────────────┐
/// │ ChannelId (1 byte)                          │
/// │ PayloadLength (4 bytes, big-endian uint32)  │
/// │ Payload (PayloadLength bytes)               │
/// └─────────────────────────────────────────────┘
/// </code>
/// </remarks>
public sealed class UsbTransport : ITransport, IAsyncDisposable
{
    // ── 定数 ───────────────────────────────────────────────────────────
    /// <summary>ADB フォワードおよび libimobiledevice トンネルで使用するポート番号。</summary>
    private const int AdbPort = 7979;

    /// <summary>フレームヘッダーのサイズ: ChannelId(1) + PayloadLength(4)。</summary>
    private const int FrameHeaderSize = 5;

    /// <summary>USB 2.0 の推定帯域幅（480 Mbps）。</summary>
    private const long DefaultBandwidthBps = 480_000_000L;

    /// <summary>送受信バッファサイズ。</summary>
    private const int BufferSize = 64 * 1024; // 64 KB

    // ── 状態 ───────────────────────────────────────────────────────────
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private bool _disposed;

    /// <summary>送信の直列化ロック（NetworkStream は同時書き込みをサポートしないため）。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // ── ITransport ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public TransportType Type => TransportType.USB;

    /// <inheritdoc/>
    /// <remarks>USB 2.0 の理論値 480 Mbps を返す固定値。帯域適応が必要な場合は拡張すること。</remarks>
    public long EstimatedBandwidthBps => DefaultBandwidthBps;

    /// <inheritdoc/>
    /// <summary>
    /// ADB フォワードトンネルを確立してからループバックの TCP 接続を開く。
    /// Windows 環境でのみ <c>adb forward</c> コマンドを実行する。
    /// </summary>
    /// <param name="endpoint">
    /// 使用しない（USB 接続は常にループバック <c>127.0.0.1:{AdbPort}</c> へ接続する）。
    /// </param>
    /// <param name="ct">キャンセルトークン。</param>
    public async Task ConnectAsync(EndPoint endpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Android: ADB フォワードトンネルを確立する
        // iOS: libimobiledevice が同じポートでトンネルを提供していることを前提とする
        if (OperatingSystem.IsWindows())
        {
            await RunAdbCommandAsync($"forward tcp:{AdbPort} tcp:{AdbPort}", ct);
        }

        _tcpClient = new TcpClient
        {
            ReceiveBufferSize = BufferSize,
            SendBufferSize = BufferSize,
            NoDelay = true  // 低遅延のため Nagle アルゴリズムを無効化
        };

        await _tcpClient.ConnectAsync(IPAddress.Loopback, AdbPort, ct);
        _stream = _tcpClient.GetStream();
    }

    /// <inheritdoc/>
    /// <summary>
    /// TCP 接続を閉じ、ADB フォワードルールを削除する。
    /// </summary>
    public async Task DisconnectAsync()
    {
        _stream?.Close();
        _stream = null;

        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                await RunAdbCommandAsync($"forward --remove tcp:{AdbPort}", CancellationToken.None);
            }
            catch
            {
                // クリーンアップエラーは無視する
            }
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// フレームヘッダー（ChannelId + PayloadLength）とペイロードを順に書き込む。
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
            await _stream!.WriteAsync(header, ct);
            await _stream!.WriteAsync(data, ct);
            await _stream!.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// 接続が切断されるかキャンセルされるまでフレームを読み続け、
    /// (ChannelId, Payload) タプルとして返す。
    /// </summary>
    public async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConnected();

        var headerBuffer = new byte[FrameHeaderSize];

        while (!ct.IsCancellationRequested)
        {
            // ヘッダーを完全に読み取る
            int headerRead = 0;
            while (headerRead < FrameHeaderSize)
            {
                int n = await _stream!.ReadAsync(
                    headerBuffer.AsMemory(headerRead, FrameHeaderSize - headerRead), ct);
                if (n == 0) yield break; // 接続切断
                headerRead += n;
            }

            var channelId = (ChannelId)headerBuffer[0];
            var payloadLength = (int)BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(1));

            // ペイロードを完全に読み取る
            var payload = new byte[payloadLength];
            int payloadRead = 0;
            while (payloadRead < payloadLength)
            {
                int n = await _stream!.ReadAsync(
                    payload.AsMemory(payloadRead, payloadLength - payloadRead), ct);
                if (n == 0) yield break; // 接続切断
                payloadRead += n;
            }

            yield return (channelId, payload.AsMemory());
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _sendLock.Dispose();
    }

    // ── プライベートヘルパー ──────────────────────────────────────────

    private void EnsureConnected()
    {
        if (_stream is null || _tcpClient is null)
            throw new InvalidOperationException("接続が確立されていません。ConnectAsync を先に呼び出してください。");
    }

    /// <summary>
    /// <c>adb</c> コマンドを非同期で実行して終了を待機する。
    /// </summary>
    /// <param name="args">adb コマンドの引数（例: "forward tcp:7979 tcp:7979"）。</param>
    /// <param name="ct">キャンセルトークン。</param>
    private static async Task RunAdbCommandAsync(string args, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("adb", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync(ct);
    }
}
