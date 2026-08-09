using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// ディスプレイ設定画面のビューモデル。
/// <list type="bullet">
///   <item>Clone / Extend / SecondaryOnly の DisplayMode 切り替え（Requirements 7.1）</item>
///   <item>解像度プリセット一覧と手動入力フォーム（Requirements 7.2）</item>
///   <item>設定変更後の永続化（Requirements 7.5）</item>
/// </list>
/// </summary>
public sealed class DisplaySettingsViewModel : INotifyPropertyChanged, IDisposable
{
    // ── 解像度プリセット定義 ──────────────────────────────────────────────
    private static readonly IReadOnlyList<ResolutionPreset> DefaultPresets = new[]
    {
        new ResolutionPreset("640 × 480 (VGA)",    640,  480),
        new ResolutionPreset("1280 × 720 (HD)",   1280,  720),
        new ResolutionPreset("1920 × 1080 (FHD)", 1920, 1080),
        new ResolutionPreset("2560 × 1440 (QHD)", 2560, 1440),
        new ResolutionPreset("3840 × 2160 (4K)",  3840, 2160),
        new ResolutionPreset("手動入力...",           0,    0),   // sentinel for manual
    };

    // ── 依存サービス ───────────────────────────────────────────────────────
    private readonly IDisplaySettingsManager? _displaySettingsManager;
    private readonly ISettingsManager _settingsManager;
    private VirtualDisplayHandle? _currentHandle;

    // ── バッキングフィールド ───────────────────────────────────────────────
    private DisplayMode _selectedMode;
    private ResolutionPreset? _selectedPreset;
    private string _manualWidth  = string.Empty;
    private string _manualHeight = string.Empty;
    private bool _isManualResolution;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _hasError;
    private bool _requireVirtualDisplay = DisplaySettings.Default.RequireVirtualDisplay;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コンストラクタ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// プロダクション用コンストラクタ。
    /// </summary>
    public DisplaySettingsViewModel(
        ISettingsManager settingsManager,
        IDisplaySettingsManager? displaySettingsManager = null)
    {
        _settingsManager = settingsManager
            ?? throw new ArgumentNullException(nameof(settingsManager));
        _displaySettingsManager = displaySettingsManager;

        // プリセット一覧を初期化する
        foreach (var p in DefaultPresets)
            ResolutionPresets.Add(p);

        // コマンド初期化
        ApplyDisplayModeCommand = new RelayCommand(
            execute: _ => _ = ApplyDisplayModeAsync(),
            canExecute: _ => !IsBusy);

        ApplyResolutionCommand = new RelayCommand(
            execute: _ => _ = ApplyResolutionAsync(),
            canExecute: _ => !IsBusy && CanApplyResolution());

        SaveSettingsCommand = new RelayCommand(
            execute: _ => _ = SaveSettingsAsync(),
            canExecute: _ => !IsBusy);

        // デフォルト値をロードする（同期）
        LoadSettingsFromCache();
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開プロパティ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>解像度プリセット一覧。</summary>
    public ObservableCollection<ResolutionPreset> ResolutionPresets { get; } = new();

    /// <summary>現在選択されているディスプレイモード。</summary>
    public DisplayMode SelectedMode
    {
        get => _selectedMode;
        set => SetField(ref _selectedMode, value);
    }

    /// <summary>複製モードが選択されているか（RadioButton バインディング用）。</summary>
    public bool IsCloneMode
    {
        get => _selectedMode == DisplayMode.Clone;
        set { if (value) SelectedMode = DisplayMode.Clone; }
    }

    /// <summary>拡張モードが選択されているか（RadioButton バインディング用）。</summary>
    public bool IsExtendMode
    {
        get => _selectedMode == DisplayMode.Extend;
        set { if (value) SelectedMode = DisplayMode.Extend; }
    }

    /// <summary>セカンダリのみモードが選択されているか（RadioButton バインディング用）。</summary>
    public bool IsSecondaryOnlyMode
    {
        get => _selectedMode == DisplayMode.SecondaryOnly;
        set { if (value) SelectedMode = DisplayMode.SecondaryOnly; }
    }

    /// <summary>現在選択されている解像度プリセット。</summary>
    public ResolutionPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value))
            {
                // 手動入力センチネルを選択したかどうかを判定する
                IsManualResolution = value?.IsManualEntry == true;

                // プリセット選択時は Width / Height フィールドを更新する
                if (value is not null && !value.IsManualEntry)
                {
                    ManualWidth  = value.Width.ToString();
                    ManualHeight = value.Height.ToString();
                }

                ((RelayCommand)ApplyResolutionCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>手動入力モードかどうか。</summary>
    public bool IsManualResolution
    {
        get => _isManualResolution;
        private set => SetField(ref _isManualResolution, value);
    }

    /// <summary>手動入力の幅（文字列）。</summary>
    public string ManualWidth
    {
        get => _manualWidth;
        set
        {
            if (SetField(ref _manualWidth, value))
                ((RelayCommand)ApplyResolutionCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>手動入力の高さ（文字列）。</summary>
    public string ManualHeight
    {
        get => _manualHeight;
        set
        {
            if (SetField(ref _manualHeight, value))
                ((RelayCommand)ApplyResolutionCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>操作中かどうか（ProgressBar / ボタン無効化用）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary>操作結果メッセージ（成功 / エラー）。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    /// <summary>エラー状態かどうか（メッセージ色の切り替え用）。</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetField(ref _hasError, value);
    }

    /// <summary>
    /// 仮想ディスプレイを必須にするか（既定 true）。
    /// </summary>
    /// <remarks>
    /// スマホは 2 枚目のモニターとして使うものなので、既定では
    /// 仮想ディスプレイを用意できないときに接続を諦める。
    /// 外すと、用意できない場合に PC のメイン画面のミラーで接続する。
    /// </remarks>
    public bool RequireVirtualDisplay
    {
        get => _requireVirtualDisplay;
        set => SetField(ref _requireVirtualDisplay, value);
    }

    /// <summary>ミラーへのフォールバックを許すか（チェックボックスの表示反転用）。</summary>
    public bool AllowMirrorFallback
    {
        get => !_requireVirtualDisplay;
        set
        {
            if (RequireVirtualDisplay == !value) return;
            RequireVirtualDisplay = !value;
            OnPropertyChanged();
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コマンド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>ディスプレイモードを適用する。</summary>
    public ICommand ApplyDisplayModeCommand { get; }

    /// <summary>解像度を適用する。</summary>
    public ICommand ApplyResolutionCommand { get; }

    /// <summary>設定を永続化する（Requirements 7.5）。</summary>
    public ICommand SaveSettingsCommand { get; }

    /// <summary>
    /// 設定を保存したときに通知する。
    /// 接続サーバーが次のセッションから新しい設定を使えるようにするため。
    /// </summary>
    public event EventHandler<DisplaySettings>? DisplaySettingsChanged;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開メソッド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// アクティブセッションの仮想ディスプレイハンドルをセットする。
    /// セッション確立後に呼び出す。
    /// </summary>
    public void SetActiveHandle(VirtualDisplayHandle handle)
    {
        _currentHandle = handle;
    }

    /// <summary>
    /// アクティブセッションが終了したときにハンドルをクリアする。
    /// </summary>
    public void ClearActiveHandle()
    {
        _currentHandle = null;
    }

    /// <summary>
    /// 設定ファイルから設定を非同期でロードして UI に反映する。
    /// </summary>
    public async Task LoadSettingsAsync()
    {
        var settings = await _settingsManager.LoadAsync();
        ApplySettingsToUi(settings.DisplayDefaults);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  プライベート: コマンド実装
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private async Task ApplyDisplayModeAsync()
    {
        SetBusy(true);
        ClearStatus();

        try
        {
            var handle = _currentHandle;
            if (_displaySettingsManager is not null && handle is not null)
            {
                await _displaySettingsManager.SetDisplayModeAsync(handle.Value, _selectedMode);
            }

            SetStatus($"ディスプレイモードを「{GetModeLabel(_selectedMode)}」に設定しました。");
        }
        catch (TimeoutException)
        {
            SetError("ディスプレイモードの適用がタイムアウトしました。再試行してください。");
        }
        catch (Exception ex)
        {
            SetError($"ディスプレイモードの適用に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyResolutionAsync()
    {
        if (!TryParseResolution(out var resolution))
        {
            SetError("有効な解像度を入力してください（例: 幅 1920、高さ 1080）。");
            return;
        }

        SetBusy(true);
        ClearStatus();

        try
        {
            var handle = _currentHandle;
            if (_displaySettingsManager is not null && handle is not null)
            {
                await _displaySettingsManager.SetResolutionAsync(handle.Value, resolution!);
            }

            SetStatus($"解像度を {resolution!.Width} × {resolution.Height} に設定しました。");
        }
        catch (TimeoutException)
        {
            SetError("解像度の適用がタイムアウトしました。再試行してください。");
        }
        catch (Exception ex)
        {
            SetError($"解像度の適用に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveSettingsAsync()
    {
        SetBusy(true);
        ClearStatus();

        try
        {
            TryParseResolution(out var resolution);
            var displaySettings = new DisplaySettings(
                Mode: _selectedMode,
                ManualResolution: resolution,
                RequireVirtualDisplay: _requireVirtualDisplay);

            await _settingsManager.SaveDisplaySettingsAsync(displaySettings);
            DisplaySettingsChanged?.Invoke(this, displaySettings);

            SetStatus("設定を保存しました。次回セッション確立時に自動的に適用されます。");
        }
        catch (Exception ex)
        {
            SetError($"設定の保存に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  ヘルパー
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private void LoadSettingsFromCache()
    {
        var settings = _settingsManager.Current;
        ApplySettingsToUi(settings.DisplayDefaults);
    }

    private void ApplySettingsToUi(DisplaySettings displaySettings)
    {
        SelectedMode = displaySettings.Mode;
        OnPropertyChanged(nameof(IsCloneMode));
        OnPropertyChanged(nameof(IsExtendMode));
        OnPropertyChanged(nameof(IsSecondaryOnlyMode));

        RequireVirtualDisplay = displaySettings.RequireVirtualDisplay;
        OnPropertyChanged(nameof(AllowMirrorFallback));

        if (displaySettings.ManualResolution is { } res)
        {
            ManualWidth  = res.Width.ToString();
            ManualHeight = res.Height.ToString();

            // 手動入力センチネルを選択する
            SelectedPreset = ResolutionPresets.FirstOrDefault(p => p.IsManualEntry);
        }
        else
        {
            // デフォルトプリセット（FHD）を選択する
            SelectedPreset = ResolutionPresets
                .FirstOrDefault(p => p.Width == 1920 && p.Height == 1080)
                ?? ResolutionPresets.FirstOrDefault();
        }
    }

    private bool CanApplyResolution()
        => TryParseResolution(out _);

    private bool TryParseResolution(out Resolution? resolution)
    {
        // プリセットが選択されていて、手動入力でない場合はプリセット値を使う
        if (_selectedPreset is not null && !_selectedPreset.IsManualEntry)
        {
            resolution = new Resolution(_selectedPreset.Width, _selectedPreset.Height);
            return true;
        }

        // 手動入力の検証
        if (int.TryParse(_manualWidth, out int w) &&
            int.TryParse(_manualHeight, out int h) &&
            w >= Resolution.MinSupported.Width && w <= Resolution.MaxSupported.Width &&
            h >= Resolution.MinSupported.Height && h <= Resolution.MaxSupported.Height)
        {
            resolution = new Resolution(w, h);
            return true;
        }

        resolution = null;
        return false;
    }

    private static string GetModeLabel(DisplayMode mode) => mode switch
    {
        DisplayMode.Clone         => "複製",
        DisplayMode.Extend        => "拡張",
        DisplayMode.SecondaryOnly => "セカンダリのみ",
        _                         => mode.ToString()
    };

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearStatus()
    {
        StatusMessage = string.Empty;
        HasError = false;
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        HasError = false;
    }

    private void SetError(string message)
    {
        StatusMessage = message;
        HasError = true;
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  INotifyPropertyChanged
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  IDisposable
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    public void Dispose()
    {
        // 現時点でアンマネージドリソースなし
    }
}

/// <summary>
/// 解像度プリセットのエントリー。
/// </summary>
public sealed class ResolutionPreset
{
    /// <summary>プリセットの表示名。</summary>
    public string Label { get; }

    /// <summary>幅（ピクセル）。手動入力センチネルの場合は 0。</summary>
    public int Width { get; }

    /// <summary>高さ（ピクセル）。手動入力センチネルの場合は 0。</summary>
    public int Height { get; }

    /// <summary>手動入力プレースホルダーかどうか。</summary>
    public bool IsManualEntry => Width == 0 && Height == 0;

    public ResolutionPreset(string label, int width, int height)
    {
        Label  = label;
        Width  = width;
        Height = height;
    }

    /// <inheritdoc/>
    public override string ToString() => Label;
}
