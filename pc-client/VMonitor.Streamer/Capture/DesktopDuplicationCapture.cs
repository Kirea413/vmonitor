using System.Runtime.Versioning;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using VMonitor.Core.Models;

namespace VMonitor.Streamer.Capture;

/// <summary>
/// DXGI Desktop Duplication API による実画面キャプチャ。
/// 指定したディスプレイの内容を BGRA32 のフレームとして取り出す。
/// </summary>
/// <remarks>
/// <para>
/// GPU 上のデスクトップ表面を CPU から読めるステージングテクスチャへコピーして取得する。
/// 変化がなければ OS はフレームを返さないため、
/// <see cref="TryCaptureFrame"/> は「今回は新しい絵がなかった」を null で表現する。
/// </para>
/// <para>
/// 画面のモード変更・GPU のリセット・セッション切り替え（Ctrl+Alt+Del、UAC）で
/// 複製は失われる。その場合は <c>DXGI_ERROR_ACCESS_LOST</c> が返るので、
/// 複製を作り直して継続する。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DesktopDuplicationCapture : IDisposable
{
    // DXGI のエラーコード
    private const int DXGI_ERROR_WAIT_TIMEOUT   = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST    = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_INVALID_CALL   = unchecked((int)0x887A0001);

    private readonly int _outputIndex;

    /// <summary>
    /// キャプチャ対象を名指しで指定する場合のディスプレイ名（例 <c>\\.\DISPLAY2</c>）。
    /// null なら <see cref="_outputIndex"/> で選ぶ。
    /// </summary>
    private readonly string? _targetDeviceName;

    private readonly object _lock = new();

    private ID3D11Device?           _device;
    private ID3D11DeviceContext?    _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D?        _staging;

    private int _width;
    private int _height;
    private long _sequence;
    private bool _disposed;

    /// <summary>キャプチャ対象ディスプレイの幅（物理ピクセル）。</summary>
    public int Width { get { lock (_lock) return _width; } }

    /// <summary>キャプチャ対象ディスプレイの高さ（物理ピクセル）。</summary>
    public int Height { get { lock (_lock) return _height; } }

    /// <summary>キャプチャ対象ディスプレイの仮想デスクトップ上の左端座標。</summary>
    public int OriginX { get; private set; }

    /// <summary>キャプチャ対象ディスプレイの仮想デスクトップ上の上端座標。</summary>
    public int OriginY { get; private set; }

    /// <summary>キャプチャ対象ディスプレイの解像度。</summary>
    public Resolution Resolution
    {
        get { lock (_lock) return new Resolution(_width, _height); }
    }

    /// <param name="outputIndex">
    /// 複製するディスプレイの番号。0 が既定のアダプターの先頭ディスプレイ。
    /// </param>
    public DesktopDuplicationCapture(int outputIndex = 0)
    {
        _outputIndex = outputIndex;
        Initialize();
    }

    /// <param name="deviceName">
    /// 複製するディスプレイの名前（<c>\\.\DISPLAY2</c> のような Windows のディスプレイ名）。
    /// <see cref="ListOutputs"/> が返す名前を渡す。
    /// </param>
    /// <remarks>
    /// 仮想ディスプレイのように「何番目か」が状況で変わる相手を指すときに使う。
    /// 番号は画面の抜き差しでずれるが、名前なら取り違えない。
    /// </remarks>
    public DesktopDuplicationCapture(string deviceName)
    {
        _targetDeviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
        Initialize();
    }

    // ── ディスプレイの列挙 ───────────────────────────────────────────────

    /// <summary>Windows が認識しているディスプレイ 1 台ぶんの情報。</summary>
    /// <param name="DeviceName">Windows のディスプレイ名（<c>\\.\DISPLAY1</c> など）。</param>
    /// <param name="AdapterIndex">このディスプレイが繋がっているアダプターの番号。</param>
    /// <param name="OutputIndex">アダプター内での番号。</param>
    /// <param name="Width">幅（ピクセル）。</param>
    /// <param name="Height">高さ（ピクセル）。</param>
    /// <param name="Left">仮想デスクトップ上の左端。</param>
    /// <param name="Top">仮想デスクトップ上の上端。</param>
    /// <param name="AttachedToDesktop">デスクトップの一部として使われているか。</param>
    public readonly record struct OutputInfo(
        string DeviceName,
        int    AdapterIndex,
        int    OutputIndex,
        int    Width,
        int    Height,
        int    Left,
        int    Top,
        bool   AttachedToDesktop)
    {
        /// <summary>左上が原点にあるディスプレイ。ふつうはこれがメイン画面。</summary>
        public bool IsPrimary => Left == 0 && Top == 0;
    }

    /// <summary>
    /// 接続されているすべてのディスプレイを列挙する。
    /// </summary>
    /// <remarks>
    /// アダプターを 1 つに決め打ちにしない。仮想ディスプレイは既定の
    /// アダプターにぶら下がるとは限らず、既定のアダプターだけ見ていると
    /// 「存在するのに見つからない」ことになる。
    /// </remarks>
    public static IReadOnlyList<OutputInfo> ListOutputs()
    {
        var results = new List<OutputInfo>();

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                break;

            using (adapter)
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                        break;

                    using (output)
                    {
                        var desc   = output.Description;
                        var bounds = desc.DesktopCoordinates;

                        results.Add(new OutputInfo(
                            DeviceName:        desc.DeviceName,
                            AdapterIndex:      (int)adapterIndex,
                            OutputIndex:       (int)outputIndex,
                            Width:             bounds.Right  - bounds.Left,
                            Height:            bounds.Bottom - bounds.Top,
                            Left:              bounds.Left,
                            Top:               bounds.Top,
                            AttachedToDesktop: desc.AttachedToDesktop));
                    }
                }
            }
        }

        return results;
    }

    // ── 初期化 ───────────────────────────────────────────────────────────

    private void Initialize()
    {
        lock (_lock)
        {
            ReleaseResourcesNoLock();

            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            // 目的のディスプレイと、それがぶら下がっているアダプターを探す。
            // 複製はディスプレイと同じアダプター上の D3D デバイスでしか作れない。
            IDXGIAdapter1? targetAdapter = null;
            IDXGIOutput?   targetOutput  = null;

            try
            {
                FindTarget(factory, out targetAdapter, out targetOutput);

                if (targetAdapter is null || targetOutput is null)
                {
                    throw new InvalidOperationException(
                        _targetDeviceName is null
                            ? $"ディスプレイ #{_outputIndex} が見つかりません。"
                            : $"ディスプレイ {_targetDeviceName} が見つかりません。");
                }

                // アダプターを明示する場合、DriverType は Unknown でなければならない。
                // Hardware を渡すと E_INVALIDARG で失敗する。
                var result = D3D11.D3D11CreateDevice(
                    targetAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out ID3D11Device? device,
                    out ID3D11DeviceContext? context);

                if (result.Failure || device is null || context is null)
                {
                    throw new InvalidOperationException(
                        $"D3D11 デバイスの作成に失敗しました (HRESULT=0x{result.Code:X8})。");
                }

                _device  = device;
                _context = context;

                var desc   = targetOutput.Description;
                var bounds = desc.DesktopCoordinates;

                _width  = bounds.Right  - bounds.Left;
                _height = bounds.Bottom - bounds.Top;
                OriginX = bounds.Left;
                OriginY = bounds.Top;

                using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(device);
            }
            finally
            {
                targetOutput?.Dispose();
                targetAdapter?.Dispose();
            }

            // ステージングテクスチャは最初のフレーム取得時に、
            // 実際に届いたテクスチャの寸法で作る（EnsureStagingNoLock 参照）。
        }
    }

    /// <summary>
    /// 複製したいディスプレイと、その持ち主のアダプターを探す。
    /// </summary>
    private void FindTarget(IDXGIFactory1 factory, out IDXGIAdapter1? adapter, out IDXGIOutput? output)
    {
        adapter = null;
        output  = null;

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? candidateAdapter).Failure
                || candidateAdapter is null)
            {
                break;
            }

            // 名前指定でないときは、従来どおり先頭のアダプターの中から番号で選ぶ
            if (_targetDeviceName is null && adapterIndex > 0)
            {
                candidateAdapter.Dispose();
                break;
            }

            bool keepAdapter = false;

            try
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (candidateAdapter.EnumOutputs(outputIndex, out IDXGIOutput? candidateOutput).Failure
                        || candidateOutput is null)
                    {
                        break;
                    }

                    bool matched = _targetDeviceName is null
                        ? outputIndex == (uint)_outputIndex
                        : string.Equals(candidateOutput.Description.DeviceName,
                                        _targetDeviceName,
                                        StringComparison.OrdinalIgnoreCase);

                    if (matched)
                    {
                        adapter     = candidateAdapter;
                        output      = candidateOutput;
                        keepAdapter = true;
                        return;
                    }

                    candidateOutput.Dispose();
                }
            }
            finally
            {
                if (!keepAdapter) candidateAdapter.Dispose();
            }
        }
    }

    /// <summary>
    /// 受け取ったデスクトップテクスチャと同じ寸法・フォーマットの
    /// ステージングテクスチャを用意する。
    /// </summary>
    /// <remarks>
    /// ステージングの寸法は <c>DesktopCoordinates</c> ではなく、実際に届いた
    /// テクスチャの記述から決める必要がある。プロセスが DPI 非対応だと
    /// <c>DesktopCoordinates</c> は仮想化された論理サイズ（例: 1920x1080 の画面で
    /// 1536x864）を返す一方、テクスチャは物理ピクセルのままになる。
    /// 寸法が食い違うと <c>CopyResource</c> は何もせず失敗し、
    /// 初期化直後のままの単色フレームが取れてしまう（例外は出ない）。
    /// </remarks>
    private void EnsureStagingNoLock(ID3D11Texture2D source)
    {
        var desc = source.Description;

        int actualWidth  = (int)desc.Width;
        int actualHeight = (int)desc.Height;

        if (_staging is not null && _width == actualWidth && _height == actualHeight)
            return;

        _staging?.Dispose();

        _width  = actualWidth;
        _height = actualHeight;

        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width             = desc.Width,
            Height            = desc.Height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            BindFlags         = BindFlags.None,
            CPUAccessFlags    = CpuAccessFlags.Read,
            MiscFlags         = ResourceOptionFlags.None,
        });
    }

    // ── キャプチャ ───────────────────────────────────────────────────────

    /// <summary>
    /// 新しいフレームがあれば取得する。
    /// </summary>
    /// <param name="timeoutMs">新フレームを待つミリ秒。</param>
    /// <returns>
    /// 新しいフレーム。指定時間内に画面が変化しなかった場合は null。
    /// </returns>
    public VideoFrame? TryCaptureFrame(int timeoutMs = 16)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_duplication is null || _context is null)
            {
                Initialize();
                if (_duplication is null) return null;
            }

            var acquire = _duplication!.AcquireNextFrame(
                (uint)timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? resource);

            if (acquire.Code == DXGI_ERROR_WAIT_TIMEOUT)
            {
                // 画面に変化がない。前フレームをそのまま使えばよい。
                return null;
            }

            if (acquire.Code is DXGI_ERROR_ACCESS_LOST or DXGI_ERROR_INVALID_CALL)
            {
                // 解像度変更・UAC・セッション切り替えで複製が失われた。作り直して次回に賭ける。
                TryReinitializeNoLock();
                return null;
            }

            if (acquire.Failure || resource is null)
                return null;

            try
            {
                // LastPresentTime が 0 のフレームはカーソル移動のみで絵は変わっていない
                if (frameInfo.LastPresentTime == 0)
                    return null;

                using var texture = resource.QueryInterface<ID3D11Texture2D>();

                // 届いたテクスチャに合わせてステージングを用意する（初回・解像度変更時）
                EnsureStagingNoLock(texture);

                _context!.CopyResource(_staging!, texture);

                return ReadStagingTextureNoLock();
            }
            finally
            {
                resource.Dispose();
                try { _duplication.ReleaseFrame(); } catch (SharpGenException) { /* 解放済み */ }
            }
        }
    }

    /// <summary>
    /// ステージングテクスチャを CPU にマップして BGRA32 のバイト列へ写す。
    /// </summary>
    /// <remarks>
    /// GPU 側の行ピッチは幅×4 とは限らない（アラインメントのため広いことがある）。
    /// 行ごとにコピーして詰め直す必要がある。
    /// </remarks>
    private VideoFrame ReadStagingTextureNoLock()
    {
        var mapped = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            int rowBytes = _width * 4;
            var buffer   = new byte[rowBytes * _height];

            unsafe
            {
                byte* source = (byte*)mapped.DataPointer;
                int   pitch  = (int)mapped.RowPitch;

                fixed (byte* dest = buffer)
                {
                    if (pitch == rowBytes)
                    {
                        Buffer.MemoryCopy(source, dest, buffer.Length, buffer.Length);
                    }
                    else
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                source + (long)y * pitch,
                                dest + (long)y * rowBytes,
                                rowBytes,
                                rowBytes);
                        }
                    }
                }
            }

            return new VideoFrame
            {
                SequenceNumber = ++_sequence,
                TimestampUs    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L,
                Resolution     = new Resolution(_width, _height),
                Data           = buffer,
            };
        }
        finally
        {
            _context!.Unmap(_staging!, 0);
        }
    }

    /// <summary>
    /// 複製を作り直す。作り直しに失敗しても例外は投げず、次回の呼び出しで再挑戦する。
    /// </summary>
    private void TryReinitializeNoLock()
    {
        try
        {
            ReleaseResourcesNoLock();
            Initialize();
        }
        catch (Exception)
        {
            // ディスプレイが取り外された等。次のフレーム取得時に再挑戦する。
        }
    }

    // ── 後始末 ───────────────────────────────────────────────────────────

    private void ReleaseResourcesNoLock()
    {
        _staging?.Dispose();     _staging     = null;
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose();     _context     = null;
        _device?.Dispose();      _device      = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseResourcesNoLock();
        }
    }
}
