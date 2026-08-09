using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Streamer.Capture;

/// <summary>
/// 実ディスプレイの内容をそのまま流す <see cref="IVirtualDisplayDriver"/> 実装。
/// 仮想ディスプレイドライバを入れなくても使える「ミラー（複製）モード」を提供する。
/// </summary>
/// <remarks>
/// <para>
/// 拡張デスクトップとして使うには署名済みの IddCx ドライバが要るが、
/// PC の画面をスマホで見る・触るだけならミラーで足りる。
/// ドライバの導入なしで動くため、これを既定の動作にしている。
/// </para>
/// <para>
/// <see cref="IVirtualDisplayDriver"/> のうちドライバ導入に関わる操作
/// （インストール・ディスプレイ作成）は、ミラーモードでは何もしない。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DesktopMirrorSource : IVirtualDisplayDriver, IDisposable
{
    private readonly DesktopDuplicationCapture _capture;
    private readonly int _targetFps;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<DisplayResolutionUpdatedEventArgs>? ResolutionUpdated;

    /// <param name="outputIndex">ミラーするディスプレイの番号。</param>
    /// <param name="targetFps">目標フレームレート。</param>
    public DesktopMirrorSource(int outputIndex = 0, int targetFps = 60)
    {
        _capture   = new DesktopDuplicationCapture(outputIndex);
        _targetFps = Math.Clamp(targetFps, 1, 240);
    }

    /// <param name="deviceName">
    /// 取り込むディスプレイの名前（<c>\\.\DISPLAY2</c> など）。
    /// 仮想ディスプレイを指すときはこちらを使う。番号は画面の増減でずれる。
    /// </param>
    /// <param name="targetFps">目標フレームレート。</param>
    public DesktopMirrorSource(string deviceName, int targetFps = 60)
    {
        _capture   = new DesktopDuplicationCapture(deviceName);
        _targetFps = Math.Clamp(targetFps, 1, 240);
    }

    /// <summary>ミラー元ディスプレイの解像度。</summary>
    public Resolution Resolution => _capture.Resolution;

    /// <summary>ミラー元ディスプレイの仮想デスクトップ上の左端座標。</summary>
    public int OriginX => _capture.OriginX;

    /// <summary>ミラー元ディスプレイの仮想デスクトップ上の上端座標。</summary>
    public int OriginY => _capture.OriginY;

    // ── フレーム供給 ─────────────────────────────────────────────────────

    /// <summary>
    /// 画面に変化がないときに、前の絵を送り直す間隔（ミリ秒）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 送り直しは「万一取りこぼしたときの保険」であって、表示のためではない。
    /// H.264 のデコーダーは新しい絵が来なければ最後の絵を出し続けるので、
    /// 同じ絵を送り続ける必要はない。
    /// </para>
    /// <para>
    /// 以前は変化がなくても目標フレームレートのまま送り直していた。
    /// エンコードは 1 枚あたり数十ミリ秒かかる（1920x1080 で実測 51.6ms）ため、
    /// エンコーダーが同じ絵で埋まり続け、実際に画面が変わったときの 1 枚が
    /// その後ろに並んで遅れていた。これが体感の遅延の主因。
    /// </para>
    /// </remarks>
    private const int IdleResendIntervalMs = 500;

    /// <inheritdoc/>
    /// <remarks>
    /// 画面に変化がないと Desktop Duplication はフレームを返さない。
    /// 変化がある間はその都度流し、止まっている間は
    /// <see cref="IdleResendIntervalMs"/> ごとに前の絵を送り直すだけにする。
    /// </remarks>
    public async IAsyncEnumerable<VideoFrame> GetFramesAsync(
        VirtualDisplayHandle handle,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int frameIntervalMs = Math.Max(1, 1000 / _targetFps);
        VideoFrame? lastFrame = null;
        var lastResolution = _capture.Resolution;

        // 最後に絵を渡してからの経過。送り直しの間隔を測るのに使う。
        var sinceLastYield = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var frame = _capture.TryCaptureFrame(frameIntervalMs);

            if (frame is not null)
            {
                // 解像度が変わったら購読者（タッチ座標変換など）に知らせる
                if (frame.Resolution != lastResolution)
                {
                    lastResolution = frame.Resolution;
                    ResolutionUpdated?.Invoke(this, new DisplayResolutionUpdatedEventArgs
                    {
                        Handle      = handle,
                        Resolution  = frame.Resolution,
                        Orientation = frame.Resolution.Width >= frame.Resolution.Height
                            ? Orientation.Landscape
                            : Orientation.Portrait,
                    });
                }

                lastFrame = frame;
                sinceLastYield.Restart();
                yield return frame;
                continue;
            }

            // 変化なし。
            //
            // ここで毎回送り直すと、エンコーダーが同じ絵で埋まり続け、
            // 次に画面が変わったときの 1 枚がその後ろに並んで遅れる。
            // 間隔を空けて、保険としてだけ送り直す。
            if (lastFrame is not null)
            {
                if (sinceLastYield.ElapsedMilliseconds < IdleResendIntervalMs)
                    continue;   // TryCaptureFrame が待っているので、ここでは待たない

                sinceLastYield.Restart();

                yield return new VideoFrame
                {
                    SequenceNumber = lastFrame.SequenceNumber,
                    TimestampUs    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L,
                    Resolution     = lastFrame.Resolution,
                    Data           = lastFrame.Data,
                };
            }
            else
            {
                // まだ 1 枚も取れていない。CPU を焼かないよう少し待つ。
                try { await Task.Delay(frameIntervalMs, ct); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    // ── ドライバ操作（ミラーモードでは不要） ─────────────────────────────

    /// <inheritdoc/>
    /// <remarks>ミラーモードはドライバを必要としないため何もしない。</remarks>
    public Task InstallAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>ミラーモードはドライバを必要としないため何もしない。</remarks>
    public Task UninstallAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// 既存の実ディスプレイを流すだけなので、新しいディスプレイは作らず
    /// このセッションを表すハンドルだけを返す。
    /// </remarks>
    public Task<VirtualDisplayHandle> CreateDisplayAsync(DisplaySpec spec)
        => Task.FromResult(VirtualDisplayHandle.NewHandle());

    /// <inheritdoc/>
    public Task RemoveDisplayAsync(VirtualDisplayHandle handle) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// ミラー元は物理ディスプレイなので解像度は変更できない。
    /// 実際の解像度を購読者に通知するだけに留める。
    /// </remarks>
    public Task UpdateResolutionAsync(
        VirtualDisplayHandle handle, Resolution resolution, Orientation orientation)
    {
        ResolutionUpdated?.Invoke(this, new DisplayResolutionUpdatedEventArgs
        {
            Handle      = handle,
            Resolution  = _capture.Resolution,
            Orientation = orientation,
        });

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capture.Dispose();
    }
}
