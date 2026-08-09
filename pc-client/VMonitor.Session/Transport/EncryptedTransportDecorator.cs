using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session.Transport;

/// <summary>
/// AES-256-GCM ペイロード暗号化を既存の <see cref="ITransport"/> にデコレーターとして追加する。
/// <para>
/// TLS トランスポート（<see cref="WifiTransport"/> など）の上にペイロードレベルの暗号化を重ね、
/// 多重防御（Defense-in-Depth）を実現する。
/// </para>
/// </summary>
/// <remarks>
/// 暗号化フレーム構造（SendAsync / ReceiveAsync の Payload 内部）:
/// <code>
/// ┌─────────────────────────────────────────────────────────┐
/// │ Nonce (12 bytes, AES-GCM 推奨 96-bit nonce)            │
/// │ Tag   (16 bytes, AES-GCM 認証タグ)                     │
/// │ CipherText (元ペイロードと同バイト数)                   │
/// └─────────────────────────────────────────────────────────┘
/// </code>
/// 鍵管理: コンストラクターに 32 バイト（256-bit）の PSK（事前共有鍵）を渡す。
/// 本番環境では ECDH セッション鍵に差し替えることを想定しているが、
/// テスタビリティのため PSK を直接受け取るインターフェースを提供する。
/// </remarks>
public sealed class EncryptedTransportDecorator : ITransport, IAsyncDisposable
{
    // ── 定数 ───────────────────────────────────────────────────────────
    /// <summary>AES-GCM ノンスのバイト長（96-bit = 12 bytes、RFC 5116 推奨）。</summary>
    private const int NonceSize = 12;

    /// <summary>AES-GCM 認証タグのバイト長（128-bit = 16 bytes）。</summary>
    private const int TagSize = 16;

    /// <summary>暗号化オーバーヘッド（Nonce + Tag）のバイト長。</summary>
    private const int EncryptionOverhead = NonceSize + TagSize; // 28

    // ── フィールド ─────────────────────────────────────────────────────
    private readonly ITransport _inner;
    private readonly byte[] _key; // 32 bytes (AES-256)
    private bool _disposed;

    // ── コンストラクター ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="EncryptedTransportDecorator"/> を初期化する。
    /// </summary>
    /// <param name="inner">ラップ対象の <see cref="ITransport"/>（TLS 済みトランスポートを推奨）。</param>
    /// <param name="key">
    /// AES-256 事前共有鍵（32 バイト）。テスト時はランダム生成、
    /// 本番ではセッション確立時に ECDH 等で交換した鍵を渡す。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> または <paramref name="key"/> が null の場合。</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> が 32 バイトでない場合。</exception>
    public EncryptedTransportDecorator(ITransport inner, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException($"AES-256 鍵は 32 バイトである必要があります。渡された長さ: {key.Length}", nameof(key));

        _inner = inner;
        // 鍵をコピーして外部変更から保護する
        _key = (byte[])key.Clone();
    }

    // ── ITransport ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public TransportType Type => _inner.Type;

    /// <inheritdoc/>
    public long EstimatedBandwidthBps => _inner.EstimatedBandwidthBps;

    /// <inheritdoc/>
    public Task ConnectAsync(EndPoint endpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.ConnectAsync(endpoint, ct);
    }

    /// <inheritdoc/>
    public Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.DisconnectAsync();
    }

    /// <summary>
    /// ペイロードを AES-256-GCM で暗号化してから内部トランスポートで送信する。
    /// </summary>
    /// <remarks>
    /// 暗号化フレーム = Nonce(12) + Tag(16) + CipherText(data.Length)。
    /// ノンスはフレームごとにランダム生成し、再利用を防ぐ。
    /// </remarks>
    public async Task SendAsync(ReadOnlyMemory<byte> data, ChannelId channel, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 出力バッファ: [Nonce(12)] [Tag(16)] [CipherText(data.Length)]
        var encrypted = Encrypt(data.Span, _key);
        await _inner.SendAsync(encrypted, channel, ct);
    }

    /// <summary>
    /// 内部トランスポートから受信し、AES-256-GCM で復号して返す。
    /// 認証タグ検証に失敗した場合は <see cref="CryptographicException"/> をスローする。
    /// </summary>
    public async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await foreach (var (channel, encryptedData) in _inner.ReceiveAsync(ct))
        {
            if (encryptedData.Length < EncryptionOverhead)
            {
                // フレームが短すぎる（ヘッダー分に満たない）: スキップして続行
                continue;
            }

            // 復号は同期ヘルパーに委譲して Span の async-iterator 制限を回避する
            var plainText = Decrypt(encryptedData.Span, _key);
            yield return (channel, plainText.AsMemory());
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 鍵をメモリからクリア
        CryptographicOperations.ZeroMemory(_key);

        if (_inner is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_inner is IDisposable disposable)
            disposable.Dispose();
    }

    // ── 静的ユーティリティ ────────────────────────────────────────────

    /// <summary>
    /// テスト・初期セットアップ用: 暗号論的に安全な 32 バイトのランダム鍵を生成する。
    /// </summary>
    /// <returns>32 バイトの AES-256 鍵。</returns>
    public static byte[] GenerateKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    // ── プライベートヘルパー ──────────────────────────────────────────

    /// <summary>
    /// AES-256-GCM でペイロードを暗号化する同期ヘルパー。
    /// async/iterator 内での Span 使用制限を回避するために切り出している。
    /// </summary>
    private static byte[] Encrypt(ReadOnlySpan<byte> plainText, byte[] key)
    {
        // 出力バッファ: [Nonce(12)] [Tag(16)] [CipherText(plainText.Length)]
        var result = new byte[EncryptionOverhead + plainText.Length];

        var nonce = result.AsSpan(0, NonceSize);
        var tag = result.AsSpan(NonceSize, TagSize);
        var cipherText = result.AsSpan(EncryptionOverhead, plainText.Length);

        // ランダムノンス生成（CSP 由来、フレームごとに一意）
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainText, cipherText, tag);

        return result;
    }

    /// <summary>
    /// AES-256-GCM でペイロードを復号する同期ヘルパー。
    /// 認証タグ検証に失敗した場合は <see cref="CryptographicException"/> をスローする。
    /// </summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> encryptedFrame, byte[] key)
    {
        var nonce = encryptedFrame.Slice(0, NonceSize);
        var tag = encryptedFrame.Slice(NonceSize, TagSize);
        var cipherText = encryptedFrame.Slice(EncryptionOverhead);

        var plainText = new byte[cipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        // 認証タグ不一致の場合は CryptographicException がスローされる
        aes.Decrypt(nonce, cipherText, tag, plainText);

        return plainText;
    }
}
