using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Driver;

/// <summary>
/// <see cref="IVirtualDisplayDriver"/> の実装。
/// IddCx アダプターを通じて仮想ディスプレイを管理し、フレームのシミュレーション配信を行う。
/// </summary>
public class VirtualDisplayDriver : IVirtualDisplayDriver
{
    private readonly IddCxAdapter _adapter;

    // ハンドル → DisplaySpec のスレッドセーフな辞書
    private readonly ConcurrentDictionary<VirtualDisplayHandle, DisplaySpec> _displays = new();

    /// <summary>仮想ディスプレイが追加されたときに発生するイベント。</summary>
    public event EventHandler<DisplayEventArgs>? DisplayAdded;

    /// <summary>仮想ディスプレイが削除されたときに発生するイベント。</summary>
    public event EventHandler<DisplayEventArgs>? DisplayRemoved;

    /// <inheritdoc/>
    /// <summary>仮想ディスプレイの解像度・向きが更新されたときに発生するイベント。</summary>
    public event EventHandler<DisplayResolutionUpdatedEventArgs>? ResolutionUpdated;

    /// <summary>
    /// <see cref="VirtualDisplayDriver"/> を初期化する。
    /// </summary>
    /// <param name="adapter">使用する IddCx アダプター。</param>
    public VirtualDisplayDriver(IddCxAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <inheritdoc/>
    public async Task InstallAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 非 Windows 環境ではスキップ（テスト・クロスプラットフォームビルド対応）
            return;
        }

        try
        {
            // IddCx ドライバを DriverStore に追加する
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/add-driver vmonitor.inf /install",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"ドライバのインストールに失敗しました（終了コード: {process.ExitCode}）。" +
                    $"対処手順: 管理者権限でインストーラーを再実行してください。詳細: {error}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ドライバのインストール中にエラーが発生しました。" +
                $"対処手順: 管理者権限でインストーラーを再実行してください。詳細: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task UninstallAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/delete-driver vmonitor.inf /uninstall",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"ドライバのアンインストールに失敗しました（終了コード: {process.ExitCode}）。" +
                    $"対処手順: 管理者権限でアンインストーラーを再実行してください。詳細: {error}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ドライバのアンインストール中にエラーが発生しました。" +
                $"対処手順: 管理者権限でアンインストーラーを再実行してください。詳細: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<VirtualDisplayHandle> CreateDisplayAsync(DisplaySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // アダプターが未初期化なら初期化する
        if (!_adapter.IsInitialized)
        {
            await _adapter.IddCxAdapterInitAsync();
        }

        var handle = _adapter.IddCxMonitorCreate(spec);
        _displays[handle] = spec;

        DisplayAdded?.Invoke(this, new DisplayEventArgs
        {
            Handle = handle,
            Spec = spec
        });

        return handle;
    }

    /// <inheritdoc/>
    public Task RemoveDisplayAsync(VirtualDisplayHandle handle)
    {
        if (_displays.TryRemove(handle, out var spec))
        {
            _adapter.IddCxMonitorRemove(handle);

            DisplayRemoved?.Invoke(this, new DisplayEventArgs
            {
                Handle = handle,
                Spec = spec
            });
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateResolutionAsync(VirtualDisplayHandle handle, Resolution resolution, Orientation orientation)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (!_displays.TryGetValue(handle, out var currentSpec))
            throw new KeyNotFoundException($"指定されたハンドルが存在しません: {handle}");

        // IddCxMonitorUpdateModes を呼び出してディスプレイモードリストを更新し、
        // 向きに応じた有効解像度（縦横正規化済み）を _adapter 側に格納する。
        _adapter.IddCxMonitorUpdateModes(handle, resolution, orientation);

        // アダプターから向き調整後の有効解像度を取得する。
        _adapter.TryGetEffectiveResolution(handle, out var effectiveResolution);
        effectiveResolution ??= IddCxAdapter.NormalizeResolution(resolution, orientation);

        var updatedSpec = currentSpec with
        {
            Resolution = effectiveResolution,
            Orientation = orientation
        };

        _displays[handle] = updatedSpec;

        // ResolutionUpdated イベントを発火する（有効解像度を含む）。
        ResolutionUpdated?.Invoke(this, new DisplayResolutionUpdatedEventArgs
        {
            Handle = handle,
            Resolution = effectiveResolution,
            Orientation = orientation
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<VideoFrame> GetFramesAsync(
        VirtualDisplayHandle handle,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_displays.TryGetValue(handle, out var spec))
            throw new KeyNotFoundException($"指定されたハンドルが存在しません: {handle}");

        // Windows 環境では SwapChainBridge 経由で実際のドライバからフレームを取得する
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Debug.WriteLine($"[VDD] GetFramesAsync called for handle={handle.Value}, displays count={_displays.Count}");

            // SwapChainBridge を try-catch で安全に作成する
            SwapChainBridge? bridge = null;
            try
            {
                bridge = new SwapChainBridge(handle, spec.Resolution.Width, spec.Resolution.Height);
                System.Diagnostics.Debug.WriteLine($"[VDD] SwapChainBridge created OK for handle={handle.Value}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VDD] SwapChainBridge create error: {ex.Message}");
                bridge = null;
            }

            if (bridge != null)
            {
                int frameCount = 0;
                try
                {
                    await foreach (var frame in bridge.GetFramesAsync(ct))
                    {
                        if (!_displays.TryGetValue(handle, out _))
                        {
                            System.Diagnostics.Debug.WriteLine($"[VDD] Handle lost after {frameCount} frames");
                            yield break;
                        }
                        frameCount++;
                        if (frameCount == 1)
                            System.Diagnostics.Debug.WriteLine($"[VDD] First frame received from SwapChainBridge");
                        yield return frame;
                    }
                    System.Diagnostics.Debug.WriteLine($"[VDD] SwapChainBridge stream ended after {frameCount} frames");
                }
                finally
                {
                    bridge.Dispose();
                }
                yield break;
            }
            // bridge が null の場合はシミュレーションにフォールバック
            System.Diagnostics.Debug.WriteLine($"[VDD] Bridge is null, falling back to simulation");
        }

        // 非 Windows 環境: 30fps シミュレーション
        const int TargetFps = 30;
        const int FrameIntervalMs = 1000 / TargetFps;
        long sequenceNumber = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_displays.TryGetValue(handle, out spec))
                yield break;

            var timestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;

            yield return new VideoFrame
            {
                SequenceNumber = sequenceNumber++,
                TimestampUs = timestampUs,
                Resolution = spec.Resolution,
                Data = ReadOnlyMemory<byte>.Empty
            };

            try
            {
                await Task.Delay(FrameIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// 指定ハンドルの仮想ディスプレイ仕様を取得する。
    /// </summary>
    /// <param name="handle">取得対象のハンドル。</param>
    /// <returns>仮想ディスプレイ仕様。存在しない場合は null。</returns>
    public DisplaySpec? GetDisplaySpec(VirtualDisplayHandle handle)
    {
        _displays.TryGetValue(handle, out var spec);
        return spec;
    }

    /// <summary>
    /// 現在アクティブな全仮想ディスプレイのスナップショットを返す。
    /// </summary>
    public IReadOnlyDictionary<VirtualDisplayHandle, DisplaySpec> GetAllDisplays()
    {
        return _displays.ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
