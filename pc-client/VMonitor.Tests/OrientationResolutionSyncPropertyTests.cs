// Feature: vmonitor, Property 10: 向き変更による解像度同期

using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Driver;

namespace VMonitor.Tests;

/// <summary>
/// Property 10: 向き変更による解像度同期
/// Validates: Requirements 5.1, 5.2
///
/// 任意の Orientation 値（Portrait / Landscape / PortraitFlipped / LandscapeFlipped）と
/// デバイスの物理解像度に対して、向き変更後に仮想ディスプレイの解像度が
/// スマートフォンの当該向き物理解像度と一致しなければならない。
/// </summary>
public class OrientationResolutionSyncPropertyTests
{
    // 解像度の有効範囲（サポート範囲内に収める）
    private const int MinDim = 640;
    private const int MaxDim = 3840;

    /// <summary>
    /// FsCheck の任意整数から有効範囲（640〜3840）の解像度次元を生成するヘルパー。
    /// </summary>
    private static int NormalizeDim(int raw) =>
        MinDim + Math.Abs(raw) % (MaxDim - MinDim + 1);

    /// <summary>
    /// Orientation 列挙型の全値をインデックスで選択するヘルパー。
    /// FsCheck は int をランダムに生成するので、余剰演算で Orientation に変換する。
    /// </summary>
    private static Orientation ToOrientation(int raw)
    {
        var values = (Orientation[])Enum.GetValues(typeof(Orientation));
        return values[Math.Abs(raw) % values.Length];
    }

    /// <summary>
    /// テスト用の SUT（System Under Test）を生成するファクトリ。
    /// IddCxAdapter と VirtualDisplayDriver を使用する。
    /// </summary>
    private static async Task<(VirtualDisplayDriver driver, VirtualDisplayHandle handle)> CreateSutAsync()
    {
        var adapter = new IddCxAdapter();
        await adapter.IddCxAdapterInitAsync();

        var driver = new VirtualDisplayDriver(adapter);

        // 初期スペックは向き・解像度を後で UpdateResolutionAsync で上書きするため仮の値で作成する
        var initialSpec = new DisplaySpec(
            Resolution: new Resolution(1920, 1080),
            RefreshRateHz: 60,
            Orientation: Orientation.Landscape,
            Mode: DisplayMode.Extend);

        var handle = await driver.CreateDisplayAsync(initialSpec);

        return (driver, handle);
    }

    /// <summary>
    /// 向きに合わせた期待解像度を計算するヘルパー（IddCxAdapter.NormalizeResolution と同じロジック）。
    /// Portrait / PortraitFlipped の場合は Width &lt;= Height に、
    /// Landscape / LandscapeFlipped の場合は Width &gt;= Height になるよう縦横を調整する。
    /// </summary>
    private static Resolution ExpectedResolution(Resolution resolution, Orientation orientation)
    {
        bool isPortrait = orientation == Orientation.Portrait || orientation == Orientation.PortraitFlipped;

        if (isPortrait && resolution.Width > resolution.Height)
            return new Resolution(resolution.Height, resolution.Width);

        if (!isPortrait && resolution.Width < resolution.Height)
            return new Resolution(resolution.Height, resolution.Width);

        return resolution;
    }

    // ── Property 10-A: 任意の Orientation と物理解像度で解像度同期が成立する ──

    /// <summary>
    /// Property 10-A: 任意の Orientation 値とデバイスの物理解像度に対して、
    /// UpdateResolutionAsync 呼び出し後に仮想ディスプレイの解像度が
    /// 当該向きの物理解像度と一致しなければならない。
    ///
    /// 向きに応じた縦横正規化後の解像度と一致することを検証する。
    ///
    /// Validates: Requirements 5.1, 5.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool OrientationResolutionSyncMatchesNormalized(
        int orientationIndex,
        int rawWidth,
        int rawHeight)
    {
        var orientation = ToOrientation(orientationIndex);
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, orientation)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        // 向き変更後の解像度は向き正規化後の物理解像度と一致しなければならない
        var expected = ExpectedResolution(physicalResolution, orientation);
        return spec.Resolution == expected;
    }

    // ── Property 10-B: Portrait 向きでは Width <= Height になる ──────────────

    /// <summary>
    /// Property 10-B: Orientation が Portrait の場合、
    /// UpdateResolutionAsync 後の仮想ディスプレイ解像度は Width &lt;= Height でなければならない。
    ///
    /// Validates: Requirements 5.1
    /// </summary>
    [Property(MaxTest = 100)]
    public bool PortraitOrientationYieldsPortraitResolution(int rawWidth, int rawHeight)
    {
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Portrait)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        // Portrait 向きでは Width <= Height（縦長）になること
        return spec.Resolution.Width <= spec.Resolution.Height;
    }

    // ── Property 10-C: Landscape 向きでは Width >= Height になる ─────────────

    /// <summary>
    /// Property 10-C: Orientation が Landscape の場合、
    /// UpdateResolutionAsync 後の仮想ディスプレイ解像度は Width &gt;= Height でなければならない。
    ///
    /// Validates: Requirements 5.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool LandscapeOrientationYieldsLandscapeResolution(int rawWidth, int rawHeight)
    {
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Landscape)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        // Landscape 向きでは Width >= Height（横長）になること
        return spec.Resolution.Width >= spec.Resolution.Height;
    }

    // ── Property 10-D: PortraitFlipped 向きでも Portrait と同じ向き制約が成立する ──

    /// <summary>
    /// Property 10-D: Orientation が PortraitFlipped の場合も Portrait と同様に
    /// Width &lt;= Height でなければならない。
    ///
    /// Validates: Requirements 5.1
    /// </summary>
    [Property(MaxTest = 100)]
    public bool PortraitFlippedOrientationYieldsPortraitResolution(int rawWidth, int rawHeight)
    {
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.PortraitFlipped)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        return spec.Resolution.Width <= spec.Resolution.Height;
    }

    // ── Property 10-E: LandscapeFlipped 向きでも Landscape と同じ向き制約が成立する ─

    /// <summary>
    /// Property 10-E: Orientation が LandscapeFlipped の場合も Landscape と同様に
    /// Width &gt;= Height でなければならない。
    ///
    /// Validates: Requirements 5.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool LandscapeFlippedOrientationYieldsLandscapeResolution(int rawWidth, int rawHeight)
    {
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.LandscapeFlipped)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        return spec.Resolution.Width >= spec.Resolution.Height;
    }

    // ── Property 10-F: 向き変更後の Orientation フィールドが更新される ─────────

    /// <summary>
    /// Property 10-F: 任意の Orientation 値に対して、
    /// UpdateResolutionAsync 後に DisplaySpec.Orientation が指定した値と一致しなければならない。
    ///
    /// Validates: Requirements 5.1, 5.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool OrientationFieldIsUpdatedAfterUpdateResolution(
        int orientationIndex,
        int rawWidth,
        int rawHeight)
    {
        var orientation = ToOrientation(orientationIndex);
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        driver.UpdateResolutionAsync(handle, physicalResolution, orientation)
              .GetAwaiter().GetResult();

        var spec = driver.GetDisplaySpec(handle);

        if (spec is null)
            return false;

        return spec.Orientation == orientation;
    }

    // ── Property 10-G: ResolutionUpdated イベントが発火される ───────────────

    /// <summary>
    /// Property 10-G: 任意の Orientation と物理解像度に対して、
    /// UpdateResolutionAsync 呼び出し後に ResolutionUpdated イベントが 1 回発火し、
    /// イベント引数の Resolution が正規化済み物理解像度と一致しなければならない。
    ///
    /// Validates: Requirements 5.1, 5.2
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ResolutionUpdatedEventFiredWithCorrectResolution(
        int orientationIndex,
        int rawWidth,
        int rawHeight)
    {
        var orientation = ToOrientation(orientationIndex);
        var physicalResolution = new Resolution(NormalizeDim(rawWidth), NormalizeDim(rawHeight));

        var (driver, handle) = CreateSutAsync().GetAwaiter().GetResult();

        DisplayResolutionUpdatedEventArgs? eventArgs = null;
        int eventCount = 0;

        driver.ResolutionUpdated += (_, args) =>
        {
            eventArgs = args;
            eventCount++;
        };

        driver.UpdateResolutionAsync(handle, physicalResolution, orientation)
              .GetAwaiter().GetResult();

        if (eventArgs is null || eventCount != 1)
            return false;

        var expected = ExpectedResolution(physicalResolution, orientation);
        return eventArgs!.Resolution == expected
            && eventArgs.Orientation == orientation
            && eventArgs.Handle == handle;
    }

    // ── 具体的なユニットテスト（代表値）────────────────────────────────────

    /// <summary>
    /// 横向き物理解像度（1920x1080）で Portrait に変更すると解像度が 1080x1920 になること。
    /// Validates: Requirement 5.1
    /// </summary>
    [Fact]
    public async Task Portrait_LandscapePhysicalResolution_SwapsWidthAndHeight()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1920, 1080); // 横長

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Portrait);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        // Portrait では縦長になるよう縦横が入れ替わる
        Assert.Equal(new Resolution(1080, 1920), spec.Resolution);
    }

    /// <summary>
    /// 縦向き物理解像度（1080x1920）で Landscape に変更すると解像度が 1920x1080 になること。
    /// Validates: Requirement 5.2
    /// </summary>
    [Fact]
    public async Task Landscape_PortraitPhysicalResolution_SwapsWidthAndHeight()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1080, 1920); // 縦長

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Landscape);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        // Landscape では横長になるよう縦横が入れ替わる
        Assert.Equal(new Resolution(1920, 1080), spec.Resolution);
    }

    /// <summary>
    /// Landscape 向きに既に横長（1920x1080）の物理解像度を適用しても解像度は変わらないこと。
    /// Validates: Requirement 5.2
    /// </summary>
    [Fact]
    public async Task Landscape_AlreadyLandscapeResolution_NoSwap()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1920, 1080);

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Landscape);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        Assert.Equal(new Resolution(1920, 1080), spec.Resolution);
    }

    /// <summary>
    /// Portrait 向きに既に縦長（1080x1920）の物理解像度を適用しても解像度は変わらないこと。
    /// Validates: Requirement 5.1
    /// </summary>
    [Fact]
    public async Task Portrait_AlreadyPortraitResolution_NoSwap()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1080, 1920);

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.Portrait);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        Assert.Equal(new Resolution(1080, 1920), spec.Resolution);
    }

    /// <summary>
    /// PortraitFlipped 向きも Portrait と同様に縦長になること（縦横入れ替え）。
    /// Validates: Requirement 5.1
    /// </summary>
    [Fact]
    public async Task PortraitFlipped_LandscapePhysicalResolution_SwapsWidthAndHeight()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1920, 1080);

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.PortraitFlipped);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        Assert.Equal(new Resolution(1080, 1920), spec.Resolution);
    }

    /// <summary>
    /// LandscapeFlipped 向きも Landscape と同様に横長になること（縦横入れ替え）。
    /// Validates: Requirement 5.2
    /// </summary>
    [Fact]
    public async Task LandscapeFlipped_PortraitPhysicalResolution_SwapsWidthAndHeight()
    {
        var (driver, handle) = await CreateSutAsync();
        var physicalResolution = new Resolution(1080, 1920);

        await driver.UpdateResolutionAsync(handle, physicalResolution, Orientation.LandscapeFlipped);

        var spec = driver.GetDisplaySpec(handle);
        Assert.NotNull(spec);
        Assert.Equal(new Resolution(1920, 1080), spec.Resolution);
    }
}
