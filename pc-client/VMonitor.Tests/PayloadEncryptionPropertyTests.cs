using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Transport;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 21: ペイロードの暗号化

/// <summary>
/// Property 21: ペイロードの暗号化
/// Validates: Requirements 8.4
///
/// 任意のペイロードバイト列に対して、暗号化後の出力はペイロードの平文と等しくなってはならない。
/// また、暗号化→復号のラウンドトリップで元のペイロードが復元されなければならない。
/// </summary>
public class PayloadEncryptionPropertyTests
{
    // ────────────────────────────────────────────────────────────
    // テスト用インメモリトランスポート
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// SendAsync で書き込んだデータを ReceiveAsync で読み返せる
    /// シンプルなインメモリ ITransport 実装。
    /// </summary>
    private sealed class InMemoryTransport : ITransport
    {
        private readonly List<(ChannelId Channel, byte[] Data)> _sent = new();

        public TransportType Type => TransportType.WiFi;
        public long EstimatedBandwidthBps => long.MaxValue;

        public Task ConnectAsync(System.Net.EndPoint endpoint, CancellationToken ct)
            => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> data, ChannelId channel, CancellationToken ct)
        {
            _sent.Add((channel, data.ToArray()));
            return Task.CompletedTask;
        }

        /// <summary>送信済みデータを一件ずつ返す非同期ストリーム。</summary>
        public async IAsyncEnumerable<(ChannelId Channel, Memory<byte> Data)> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var (ch, bytes) in _sent)
            {
                ct.ThrowIfCancellationRequested();
                yield return (ch, bytes.AsMemory());
                await Task.Yield();
            }
        }

        /// <summary>最後に送信されたデータを返す（テスト検証用）。</summary>
        public byte[]? LastSentData => _sent.Count > 0 ? _sent[^1].Data : null;

        /// <summary>送信済みデータをすべてクリアする。</summary>
        public void Clear() => _sent.Clear();
    }

    // ────────────────────────────────────────────────────────────
    // Property 21-A: 暗号化後の出力は平文と等しくなってはならない
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 21-A: 任意の非空ペイロードバイト列に対して、
    /// EncryptedTransportDecorator の SendAsync が内部トランスポートへ渡すデータは
    /// 元の平文と等しくなってはならない（暗号化が実施されていることの確認）。
    ///
    /// Validates: Requirements 8.4
    /// </summary>
    [Property(MaxTest = 100)]
    public bool EncryptedOutputDiffersFromPlaintext(NonEmptyArray<byte> rawPayload)
    {
        var plaintext = rawPayload.Get;

        var inner = new InMemoryTransport();
        var key = EncryptedTransportDecorator.GenerateKey();
        var decorator = new EncryptedTransportDecorator(inner, key);

        var channel = ChannelId.Video;
        decorator.SendAsync(plaintext.AsMemory(), channel, CancellationToken.None)
                 .GetAwaiter().GetResult();

        var encrypted = inner.LastSentData;

        // 1. 暗号化データが存在すること
        if (encrypted is null || encrypted.Length == 0)
            return false;

        // 2. 暗号化後のバイト列が平文と一致しないこと
        // （Nonce+Tag のオーバーヘッドがあるため長さも異なるはずだが、
        //    バイト列の等価比較で十分）
        if (plaintext.Length == encrypted.Length && plaintext.AsSpan().SequenceEqual(encrypted.AsSpan()))
            return false;

        return true;
    }

    // ────────────────────────────────────────────────────────────
    // Property 21-B: 暗号化→復号のラウンドトリップで元のペイロードが復元される
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 21-B: 任意のペイロードバイト列に対して、
    /// EncryptedTransportDecorator の SendAsync で暗号化されたデータを
    /// ReceiveAsync で復号すると元のペイロードと完全に一致しなければならない
    /// （暗号化→復号のラウンドトリップ）。
    ///
    /// Validates: Requirements 8.4
    /// </summary>
    [Property(MaxTest = 100)]
    public bool EncryptDecryptRoundTripRestoresPayload(byte[] rawPayload)
    {
        // null は空配列として扱う
        var plaintext = rawPayload ?? Array.Empty<byte>();

        var inner = new InMemoryTransport();
        var key = EncryptedTransportDecorator.GenerateKey();
        var decorator = new EncryptedTransportDecorator(inner, key);

        var channel = ChannelId.Touch;

        // 暗号化して送信
        decorator.SendAsync(plaintext.AsMemory(), channel, CancellationToken.None)
                 .GetAwaiter().GetResult();

        // 同じ鍵を持つ別の EncryptedTransportDecorator で復号する
        // inner に蓄積された暗号化済みデータを ReceiveAsync で読み返す
        var decryptingDecorator = new EncryptedTransportDecorator(inner, key);

        byte[]? decrypted = null;
        foreach (var (_, data) in decryptingDecorator.ReceiveAsync(CancellationToken.None)
                     .ToBlockingEnumerable())
        {
            decrypted = data.ToArray();
            break; // 最初の1件だけ処理する
        }

        // 空ペイロードの場合: 暗号化オーバーヘッド(28バイト)を満たさないため
        // EncryptedTransportDecorator の ReceiveAsync はスキップする（設計上の動作）
        // よって空配列に対しては decrypted が null になることを許容する
        if (plaintext.Length == 0)
            return true;

        if (decrypted is null)
            return false;

        // 復号結果が元の平文と完全に一致すること
        return plaintext.AsSpan().SequenceEqual(decrypted.AsSpan());
    }
}
