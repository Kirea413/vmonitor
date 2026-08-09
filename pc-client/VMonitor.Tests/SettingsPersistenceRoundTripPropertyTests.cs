using FsCheck;
using FsCheck.Xunit;
using VMonitor.Core.Models;
using VMonitor.Session;

namespace VMonitor.Tests;

// Feature: vmonitor, Property 19: 設定永続化のラウンドトリップ

/// <summary>
/// Property 19: 設定永続化のラウンドトリップ
/// Validates: Requirements 7.5
///
/// 任意の有効な設定値（StreamingSettings / DisplaySettings）に対して、
/// 保存後に読み込んだ設定が元の値と等しくなければならない。
/// </summary>
public class SettingsPersistenceRoundTripPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsPersistenceRoundTripPropertyTests()
    {
        // テストごとに独立した一時ディレクトリを使用する
        _tempDir = Path.Combine(Path.GetTempPath(), "vmonitor_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string GetTempSettingsPath() =>
        Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".json");

    // --- Properties ---

    /// <summary>
    /// Property 19a: 任意の StreamingSettings に対して、
    /// SaveStreamingSettingsAsync → LoadAsync のラウンドトリップで値が保持されること。
    ///
    /// パラメーター:
    ///   bitrate       - ビットレート (bps)
    ///   fps           - 最大フレームレート
    ///   codecIndex    - コーデック種別インデックス (0=H264, 1=H265)
    ///   adaptive      - アダプティブビットレート有効フラグ
    /// </summary>
    [Property(MaxTest = 100)]
    public bool StreamingSettingsRoundTrip(
        PositiveInt bitrateRaw,
        PositiveInt fpsRaw,
        bool codecH265,
        bool adaptive)
    {
        var bitrate = (bitrateRaw.Get % 49_900_000) + 100_000; // 100kbps..50Mbps
        var fps = (fpsRaw.Get % 119) + 1;                       // 1..120
        var codec = codecH265 ? VideoCodec.H265 : VideoCodec.H264;

        var original = new StreamingSettings(bitrate, fps, codec, adaptive);
        var path = GetTempSettingsPath();

        var saver = new SettingsManager(path);
        saver.SaveStreamingSettingsAsync(original).GetAwaiter().GetResult();

        var loader = new SettingsManager(path);
        var loaded = loader.LoadAsync().GetAwaiter().GetResult();

        return loaded.StreamingDefaults == original;
    }

    /// <summary>
    /// Property 19b: 任意の DisplaySettings に対して、
    /// SaveDisplaySettingsAsync → LoadAsync のラウンドトリップで値が保持されること。
    ///
    /// パラメーター:
    ///   modeIndex     - DisplayMode 値インデックス
    ///   hasManual     - 手動解像度が指定されているか
    ///   width         - 手動解像度の幅 (hasManual=true の場合のみ使用)
    ///   height        - 手動解像度の高さ (hasManual=true の場合のみ使用)
    /// </summary>
    [Property(MaxTest = 100)]
    public bool DisplaySettingsRoundTrip(
        NonNegativeInt modeIndexRaw,
        bool hasManual,
        PositiveInt widthRaw,
        PositiveInt heightRaw)
    {
        var modes = new[] { DisplayMode.Clone, DisplayMode.Extend, DisplayMode.SecondaryOnly };
        var mode = modes[modeIndexRaw.Get % modes.Length];
        var manualResolution = hasManual
            ? new Resolution((widthRaw.Get % 3200) + 640, (heightRaw.Get % 1680) + 480)
            : null;

        var original = new DisplaySettings(mode, manualResolution);
        var path = GetTempSettingsPath();

        var saver = new SettingsManager(path);
        saver.SaveDisplaySettingsAsync(original).GetAwaiter().GetResult();

        var loader = new SettingsManager(path);
        var loaded = loader.LoadAsync().GetAwaiter().GetResult();

        return loaded.DisplayDefaults == original;
    }

    /// <summary>
    /// Property 19c: SaveAsync(AppSettings) → LoadAsync のラウンドトリップで
    /// StreamingDefaults と DisplayDefaults の両方が保持されること。
    ///
    /// パラメーター:
    ///   bitrateRaw    - ビットレート (bps, 正の整数)
    ///   fpsRaw        - 最大フレームレート (正の整数)
    ///   modeIndexRaw  - DisplayMode 値インデックス
    /// </summary>
    [Property(MaxTest = 100)]
    public bool FullSettingsRoundTrip(
        PositiveInt bitrateRaw,
        PositiveInt fpsRaw,
        NonNegativeInt modeIndexRaw)
    {
        var bitrate = (bitrateRaw.Get % 49_900_000) + 100_000;
        var fps = (fpsRaw.Get % 119) + 1;
        var modes = new[] { DisplayMode.Clone, DisplayMode.Extend, DisplayMode.SecondaryOnly };
        var mode = modes[modeIndexRaw.Get % modes.Length];

        var streaming = new StreamingSettings(bitrate, fps, VideoCodec.H264, true);
        var display = new DisplaySettings(mode, null);
        var original = new AppSettings(
            TrustedDevices: Array.Empty<TrustedDevice>(),
            StreamingDefaults: streaming,
            DisplayDefaults: display,
            LogFilePath: @"%APPDATA%\vmonitor\logs\vmonitor.log");

        var path = GetTempSettingsPath();
        var saver = new SettingsManager(path);
        saver.SaveAsync(original).GetAwaiter().GetResult();

        var loader = new SettingsManager(path);
        var loaded = loader.LoadAsync().GetAwaiter().GetResult();

        return loaded.StreamingDefaults == original.StreamingDefaults
            && loaded.DisplayDefaults == original.DisplayDefaults;
    }

    // --- Unit tests for default fallback on corruption ---

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var path = GetTempSettingsPath();
        var manager = new SettingsManager(path);

        var settings = await manager.LoadAsync();

        Assert.Equal(AppSettings.CreateDefault(), settings);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsCorrupted_ReturnsDefaults()
    {
        var path = GetTempSettingsPath();
        await File.WriteAllTextAsync(path, "{ this is not valid JSON !!!");

        var manager = new SettingsManager(path);
        var settings = await manager.LoadAsync();

        Assert.Equal(AppSettings.CreateDefault(), settings);
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsEmpty_ReturnsDefaults()
    {
        var path = GetTempSettingsPath();
        await File.WriteAllTextAsync(path, "");

        var manager = new SettingsManager(path);
        var settings = await manager.LoadAsync();

        Assert.Equal(AppSettings.CreateDefault(), settings);
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNotExists()
    {
        // Use a path in a non-existent subdirectory
        var subDir = Path.Combine(_tempDir, "sub", "nested");
        var path = Path.Combine(subDir, "settings.json");

        var manager = new SettingsManager(path);
        await manager.SaveAsync(AppSettings.CreateDefault());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Current_BeforeLoad_ReturnsDefault()
    {
        var path = GetTempSettingsPath();
        var manager = new SettingsManager(path);

        // No load has been called yet
        Assert.Equal(AppSettings.CreateDefault(), manager.Current);
    }

    [Fact]
    public async Task TrustedDevicesRoundTrip()
    {
        var path = GetTempSettingsPath();
        var devices = new List<TrustedDevice>
        {
            new(DeviceIdentifier.NewIdentifier(), "My iPhone", DateTimeOffset.UtcNow, null),
            new(DeviceIdentifier.NewIdentifier(), "My Android", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1)),
        };

        var manager = new SettingsManager(path);
        await manager.SaveTrustedDevicesAsync(devices);

        var loader = new SettingsManager(path);
        var loaded = await loader.LoadAsync();

        Assert.Equal(devices.Count, loaded.TrustedDevices.Count);
        for (int i = 0; i < devices.Count; i++)
        {
            Assert.Equal(devices[i].Id, loaded.TrustedDevices[i].Id);
            Assert.Equal(devices[i].Name, loaded.TrustedDevices[i].Name);
            // DateTimeOffset round-trip precision: compare to second precision
            Assert.Equal(
                devices[i].TrustedAt.ToUnixTimeSeconds(),
                loaded.TrustedDevices[i].TrustedAt.ToUnixTimeSeconds());
        }
    }
}
