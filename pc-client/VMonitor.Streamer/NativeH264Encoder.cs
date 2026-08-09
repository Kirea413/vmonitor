using System.Runtime.Versioning;
using VMonitor.Core.Models;

namespace VMonitor.Streamer;

/// <summary>
/// ネイティブの Media Foundation H.264 エンコーダーを <see cref="IFrameEncoder"/> として提供する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeH264Encoder : IFrameEncoder
{
    private MfEncoderHandle? _encoder;
    private Resolution? _configured;
    private int _bitrateBps;
    private int _maxFps;
    private bool _disposed;

    /// <summary>ネイティブエンコーダーがこの環境で使えるか。</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() && NativeEncoderBridge.IsAvailable;

    /// <inheritdoc/>
    public void Configure(Resolution resolution, int bitrateBps, int maxFps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _bitrateBps = bitrateBps;
        _maxFps     = maxFps;

        if (_configured == resolution && _encoder is not null)
            return;

        _encoder?.Dispose();
        _encoder    = new MfEncoderHandle(resolution, bitrateBps, maxFps);
        _configured = resolution;
    }

    /// <inheritdoc/>
    public void SetBitrate(int bitrateBps)
    {
        _bitrateBps = bitrateBps;
        _encoder?.SetBitrate(bitrateBps);
    }

    /// <inheritdoc/>
    public byte[]? Encode(ReadOnlySpan<byte> bgra32Data, long timestampUs)
        => _encoder?.Encode(bgra32Data, timestampUs);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _encoder?.Dispose();
        _encoder = null;
    }
}

/// <summary>
/// ネイティブエンコーダーの設定と 1 つの解像度を結び付けた薄いハンドル。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfEncoderHandle : IDisposable
{
    private readonly MfEncoder _encoder;
    private bool _disposed;

    public MfEncoderHandle(Resolution resolution, int bitrateBps, int maxFps)
    {
        _encoder = new MfEncoder();
        _encoder.Initialize(resolution.Width, resolution.Height, bitrateBps, maxFps);
    }

    public void SetBitrate(int bitrateBps) => _encoder.SetBitrate(bitrateBps);

    public byte[]? Encode(ReadOnlySpan<byte> data, long timestampUs)
        => _encoder.EncodeFrame(data, timestampUs);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
    }
}
