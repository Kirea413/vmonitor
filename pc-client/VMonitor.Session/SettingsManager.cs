using System.Text.Json;
using System.Text.Json.Serialization;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.Session;

/// <summary>
/// ISettingsManager の実装。
/// %APPDATA%\vmonitor\settings.json への設定読み書きを行う。
/// ファイルが存在しない場合や JSON が破損している場合はデフォルト値にフォールバックする。
/// </summary>
public sealed class SettingsManager : ISettingsManager
{
    // デフォルトの設定ファイルパス
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "vmonitor",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AppSettings? _cached;

    /// <summary>
    /// デフォルトパス (%APPDATA%\vmonitor\settings.json) を使用して SettingsManager を初期化する。
    /// </summary>
    public SettingsManager() : this(DefaultSettingsPath) { }

    /// <summary>
    /// テスト用に設定ファイルパスを指定して SettingsManager を初期化する。
    /// </summary>
    public SettingsManager(string settingsPath)
    {
        _settingsPath = settingsPath
            ?? throw new ArgumentNullException(nameof(settingsPath));
    }

    /// <inheritdoc/>
    public AppSettings Current => _cached ?? AppSettings.CreateDefault();

    /// <inheritdoc/>
    public async Task<AppSettings> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cached = await LoadInternalAsync().ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(settings).ConfigureAwait(false);
            _cached = settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveStreamingSettingsAsync(StreamingSettings streamingSettings)
    {
        ArgumentNullException.ThrowIfNull(streamingSettings);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _cached ?? await LoadInternalAsync().ConfigureAwait(false);
            var updated = current with { StreamingDefaults = streamingSettings };
            await SaveInternalAsync(updated).ConfigureAwait(false);
            _cached = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveDisplaySettingsAsync(DisplaySettings displaySettings)
    {
        ArgumentNullException.ThrowIfNull(displaySettings);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _cached ?? await LoadInternalAsync().ConfigureAwait(false);
            var updated = current with { DisplayDefaults = displaySettings };
            await SaveInternalAsync(updated).ConfigureAwait(false);
            _cached = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveTrustedDevicesAsync(IReadOnlyList<TrustedDevice> trustedDevices)
    {
        ArgumentNullException.ThrowIfNull(trustedDevices);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _cached ?? await LoadInternalAsync().ConfigureAwait(false);
            var updated = current with { TrustedDevices = trustedDevices };
            await SaveInternalAsync(updated).ConfigureAwait(false);
            _cached = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- private helpers ---

    private async Task<AppSettings> LoadInternalAsync()
    {
        if (!File.Exists(_settingsPath))
            return AppSettings.CreateDefault();

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
            if (dto is null)
                return AppSettings.CreateDefault();

            return dto.ToAppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 破損・読み取り失敗時はデフォルト値にフォールバック
            return AppSettings.CreateDefault();
        }
    }

    private async Task SaveInternalAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var dto = SettingsDto.FromAppSettings(settings);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json).ConfigureAwait(false);
    }

    // --- JSON DTO types ---
    // AppSettings の record は System.Text.Json のデシリアライズ向けに DTO を使う

    private sealed class SettingsDto
    {
        public List<TrustedDeviceDto>? TrustedDevices { get; set; }
        public StreamingSettingsDto? StreamingDefaults { get; set; }
        public DisplaySettingsDto? DisplayDefaults { get; set; }
        public string? LogFilePath { get; set; }
        public string? UsbMode { get; set; }

        public AppSettings ToAppSettings()
        {
            var @default = AppSettings.CreateDefault();

            IReadOnlyList<TrustedDevice> trustedDevices =
                TrustedDevices?.Select(d => d.ToTrustedDevice()).ToList().AsReadOnly()
                ?? @default.TrustedDevices;

            var streaming = StreamingDefaults?.ToStreamingSettings()
                ?? @default.StreamingDefaults;

            var display = DisplayDefaults?.ToDisplaySettings()
                ?? @default.DisplayDefaults;

            var logPath = LogFilePath ?? @default.LogFilePath;

            var usbMode = Enum.TryParse<UsbConnectionMode>(UsbMode, ignoreCase: true, out var m)
                ? m
                : @default.UsbMode;

            return new AppSettings(trustedDevices, streaming, display, logPath, usbMode);
        }

        public static SettingsDto FromAppSettings(AppSettings settings) => new()
        {
            TrustedDevices = settings.TrustedDevices
                .Select(TrustedDeviceDto.FromTrustedDevice)
                .ToList(),
            StreamingDefaults = StreamingSettingsDto.FromStreamingSettings(settings.StreamingDefaults),
            DisplayDefaults = DisplaySettingsDto.FromDisplaySettings(settings.DisplayDefaults),
            LogFilePath = settings.LogFilePath,
            UsbMode = settings.UsbMode.ToString(),
        };
    }

    private sealed class TrustedDeviceDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public DateTimeOffset TrustedAt { get; set; }
        public DateTimeOffset? LastConnectedAt { get; set; }

        public TrustedDevice ToTrustedDevice() => new(
            Id: DeviceIdentifier.Parse(Id ?? Guid.Empty.ToString()),
            Name: Name ?? string.Empty,
            TrustedAt: TrustedAt,
            LastConnectedAt: LastConnectedAt);

        public static TrustedDeviceDto FromTrustedDevice(TrustedDevice d) => new()
        {
            Id = d.Id.ToString(),
            Name = d.Name,
            TrustedAt = d.TrustedAt,
            LastConnectedAt = d.LastConnectedAt,
        };
    }

    private sealed class StreamingSettingsDto
    {
        public int BitrateBps { get; set; }
        public int MaxFps { get; set; }
        public string? Codec { get; set; }
        public bool AdaptiveBitrateEnabled { get; set; }

        public StreamingSettings ToStreamingSettings()
        {
            var codec = Enum.TryParse<VideoCodec>(Codec, ignoreCase: true, out var c)
                ? c
                : StreamingSettings.Default.Codec;

            return new StreamingSettings(
                BitrateBps: BitrateBps > 0 ? BitrateBps : StreamingSettings.Default.BitrateBps,
                MaxFps: MaxFps > 0 ? MaxFps : StreamingSettings.Default.MaxFps,
                Codec: codec,
                AdaptiveBitrateEnabled: AdaptiveBitrateEnabled);
        }

        public static StreamingSettingsDto FromStreamingSettings(StreamingSettings s) => new()
        {
            BitrateBps = s.BitrateBps,
            MaxFps = s.MaxFps,
            Codec = s.Codec.ToString(),
            AdaptiveBitrateEnabled = s.AdaptiveBitrateEnabled,
        };
    }

    private sealed class DisplaySettingsDto
    {
        public string? Mode { get; set; }
        public ResolutionDto? ManualResolution { get; set; }

        /// <summary>
        /// 設定ファイルにこの項目が無い（旧バージョンで書かれた）場合は
        /// null になる。既定値を採用したいので bool? にしてある。
        /// </summary>
        public bool? RequireVirtualDisplay { get; set; }

        public DisplaySettings ToDisplaySettings()
        {
            var mode = Enum.TryParse<DisplayMode>(Mode, ignoreCase: true, out var m)
                ? m
                : DisplaySettings.Default.Mode;

            var resolution = ManualResolution?.ToResolution();

            return new DisplaySettings(
                Mode:                  mode,
                ManualResolution:      resolution,
                RequireVirtualDisplay: RequireVirtualDisplay
                                       ?? DisplaySettings.Default.RequireVirtualDisplay);
        }

        public static DisplaySettingsDto FromDisplaySettings(DisplaySettings d) => new()
        {
            Mode = d.Mode.ToString(),
            ManualResolution = d.ManualResolution is not null
                ? ResolutionDto.FromResolution(d.ManualResolution)
                : null,
            RequireVirtualDisplay = d.RequireVirtualDisplay,
        };
    }

    private sealed class ResolutionDto
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public Resolution ToResolution() => new(Width, Height);

        public static ResolutionDto FromResolution(Resolution r) => new()
        {
            Width = r.Width,
            Height = r.Height,
        };
    }
}
