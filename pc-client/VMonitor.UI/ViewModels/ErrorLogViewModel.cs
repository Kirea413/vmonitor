using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;
using VMonitor.Session.Transport;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// エラーログ確認・設定画面のビューモデル。
/// <list type="bullet">
///   <item>エラーログファイルパスの表示（Requirements 9.5）</item>
///   <item>ビットレート設定の永続化（Requirements 7.5）</item>
/// </list>
/// </summary>
public sealed class ErrorLogViewModel : INotifyPropertyChanged, IDisposable
{
    // ── ビットレート範囲定数 ──────────────────────────────────────────────
    /// <summary>最低ビットレート: 1 Mbps</summary>
    public const int MinBitrateBps = 1_000_000;

    /// <summary>最高ビットレート: 50 Mbps</summary>
    public const int MaxBitrateBps = 50_000_000;

    // ── 依存サービス ───────────────────────────────────────────────────────
    private readonly ISettingsManager _settingsManager;

    // ── バッキングフィールド ───────────────────────────────────────────────
    private string _logFilePath = string.Empty;
    private int _bitrateBps;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _hasError;
    private UsbConnectionMode _usbMode = UsbConnectionMode.WinUsb;
    private bool _requireVirtualDisplay = DisplaySettings.Default.RequireVirtualDisplay;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コンストラクタ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// プロダクション用コンストラクタ。
    /// </summary>
    public ErrorLogViewModel(ISettingsManager settingsManager)
    {
        _settingsManager = settingsManager
            ?? throw new ArgumentNullException(nameof(settingsManager));

        // コマンド初期化
        OpenLogFolderCommand = new RelayCommand(
            execute: _ => OpenLogFolder(),
            canExecute: _ => !string.IsNullOrEmpty(LogFilePath));

        OpenLogFileCommand = new RelayCommand(
            execute: _ => OpenLogFile(),
            canExecute: _ => !string.IsNullOrEmpty(LogFilePath));

        SaveBitrateCommand = new RelayCommand(
            execute: _ => _ = SaveBitrateAsync(),
            canExecute: _ => !IsBusy);

        SetUsbModeCommand = new RelayCommand(
            execute: param => { if (param is string s && Enum.TryParse<UsbConnectionMode>(s, out var m)) _usbMode = m; OnPropertyChanged(nameof(IsWinUsbMode)); OnPropertyChanged(nameof(IsAdbMode)); },
            canExecute: _ => true);

        SaveUsbModeCommand = new RelayCommand(
            execute: _ => _ = SaveUsbModeAsync(),
            canExecute: _ => !IsBusy);

        SaveDisplayPolicyCommand = new RelayCommand(
            execute: _ => _ = SaveDisplayPolicyAsync(),
            canExecute: _ => !IsBusy);

        // 現在の設定をキャッシュからロードする
        LoadSettingsFromCache();
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開プロパティ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// ログファイルの絶対パス（%APPDATA% を展開済み）。
    /// Requirements 9.5: 設定画面にログファイルパスを表示する。
    /// </summary>
    public string LogFilePath
    {
        get => _logFilePath;
        private set
        {
            if (SetField(ref _logFilePath, value))
            {
                OnPropertyChanged(nameof(LogFileExists));
                ((RelayCommand)OpenLogFolderCommand).RaiseCanExecuteChanged();
                ((RelayCommand)OpenLogFileCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// ログファイルが実際に存在するかどうか（ファイルリンクの有効化用）。
    /// </summary>
    public bool LogFileExists => File.Exists(LogFilePath);

    /// <summary>
    /// 現在のビットレート設定（bps）。スライダーおよびテキスト入力にバインドする。
    /// Requirements 7.5: 設定を永続化して次回セッションに適用する。
    /// </summary>
    public int BitrateBps
    {
        get => _bitrateBps;
        set => SetField(ref _bitrateBps, Math.Clamp(value, MinBitrateBps, MaxBitrateBps));
    }

    /// <summary>
    /// ビットレートの Mbps 表示用（UI ラベル向け）。
    /// </summary>
    public double BitrateMbps => BitrateBps / 1_000_000.0;

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

    /// <summary>スライダー最小値（Mbps）。バインディング用。</summary>
    public double SliderMinMbps => MinBitrateBps / 1_000_000.0;

    /// <summary>スライダー最大値（Mbps）。バインディング用。</summary>
    public double SliderMaxMbps => MaxBitrateBps / 1_000_000.0;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コマンド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>ログフォルダーをエクスプローラーで開く（Requirements 9.5）。</summary>
    public ICommand OpenLogFolderCommand { get; }

    /// <summary>ログファイルをメモ帳で開く（Requirements 9.5）。</summary>
    public ICommand OpenLogFileCommand { get; }

    /// <summary>ビットレート設定を保存する（Requirements 7.5）。</summary>
    public ICommand SaveBitrateCommand { get; }

    /// <summary>USB モードを切り替えるコマンド。</summary>
    public ICommand SetUsbModeCommand { get; }

    /// <summary>USB モード設定を保存するコマンド。</summary>
    public ICommand SaveUsbModeCommand { get; }

    /// <summary>ディスプレイの扱い（拡張の強制）を保存するコマンド。</summary>
    public ICommand SaveDisplayPolicyCommand { get; }

    /// <summary>
    /// ディスプレイ設定を保存したときに通知する。
    /// 接続サーバーが次のセッションから新しい設定を使うため。
    /// </summary>
    public event EventHandler<DisplaySettings>? DisplaySettingsChanged;

    // ── ディスプレイの扱い ───────────────────────────────────────────────

    /// <summary>
    /// 仮想ディスプレイ（拡張）だけを使うか。既定は true。
    /// </summary>
    /// <remarks>
    /// スマホは 2 枚目のモニターとして使うものなので、既定では
    /// 仮想ディスプレイを用意できないときに接続を中止する。
    /// 黙って PC 画面のミラーに落ちると、拡張のつもりで繋いだ利用者には
    /// 「同じ画面が出てきた」としか見えないため。
    /// </remarks>
    public bool RequireVirtualDisplay
    {
        get => _requireVirtualDisplay;
        set
        {
            if (_requireVirtualDisplay == value) return;
            _requireVirtualDisplay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AllowMirrorFallback));
        }
    }

    /// <summary>ミラーへのフォールバックを許すか（チェックボックス用の反転）。</summary>
    public bool AllowMirrorFallback
    {
        get => !_requireVirtualDisplay;
        set => RequireVirtualDisplay = !value;
    }

    /// <summary>
    /// 更新の確認と適用。
    /// </summary>
    /// <remarks>
    /// 設定画面から使うので、ここにぶら下げておく。
    /// </remarks>
    public UpdateViewModel Update { get; } = new();

    // ── 拡大率とタッチ ───────────────────────────────────────────────────

    private int  _scalePercent = DisplaySettings.Default.ScalePercent;
    private bool _enableTouch  = DisplaySettings.Default.EnableTouch;

    /// <summary>
    /// スマホに映すときの拡大率（パーセント）。
    /// </summary>
    /// <remarks>
    /// スマホの画素数そのままで作ると、Windows の文字やボタンが
    /// 細かすぎて読めない。拡大率のぶん解像度を下げて作ることで、
    /// 見た目を大きくする（そのぶん作業領域は狭くなる）。
    /// </remarks>
    public int ScalePercent
    {
        get => _scalePercent;
        set
        {
            var clamped = Math.Clamp(value,
                DisplaySettings.MinScalePercent, DisplaySettings.MaxScalePercent);

            if (_scalePercent == clamped) return;

            _scalePercent = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScaleText));
        }
    }

    /// <summary>拡大率の表示文字列。</summary>
    public string ScaleText => _scalePercent == 100
        ? "100%（等倍）"
        : $"{_scalePercent}%";

    /// <summary>
    /// スマホの操作を PC へ送るか。
    /// </summary>
    /// <remarks>
    /// 切ると「見るだけ」になる。映しておくだけの使い方では、
    /// 画面に触れるたびにマウスが飛ぶほうが困る。
    /// </remarks>
    public bool EnableTouch
    {
        get => _enableTouch;
        set
        {
            if (_enableTouch == value) return;
            _enableTouch = value;
            OnPropertyChanged();
        }
    }

    // ── USB モードプロパティ ─────────────────────────────────────────────

    /// <summary>WinUSB モードが選択されているかどうか。</summary>
    public bool IsWinUsbMode => _usbMode == UsbConnectionMode.WinUsb;

    /// <summary>ADB モードが選択されているかどうか。</summary>
    public bool IsAdbMode => _usbMode == UsbConnectionMode.Adb;

    /// <summary>ADB の利用状況テキスト（UI 表示用）。</summary>
    public string AdbStatusText => UsbTransportFactory.IsAdbAvailable()
        ? "利用可能 ✓"
        : "見つかりません（PATH に adb がない）";

    /// <summary>ADB ステータスの色。</summary>
    public Brush AdbStatusColor => UsbTransportFactory.IsAdbAvailable()
        ? Brushes.Green
        : Brushes.Red;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開メソッド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// 設定ファイルから最新の設定を非同期でロードして UI に反映する。
    /// </summary>
    public async Task LoadSettingsAsync()
    {
        var settings = await _settingsManager.LoadAsync();
        ApplySettingsToUi(settings);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  プライベート: コマンド実装
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// ログファイルが格納されているフォルダーをエクスプローラーで開く。
    /// フォルダーが存在しない場合は作成を試みる。
    /// </summary>
    private void OpenLogFolder()
    {
        if (string.IsNullOrEmpty(LogFilePath))
            return;

        try
        {
            var folder = Path.GetDirectoryName(LogFilePath);
            if (string.IsNullOrEmpty(folder))
                return;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SetError($"フォルダーを開けませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// ログファイルをメモ帳で開く。
    /// ファイルが存在しない場合は親フォルダーをエクスプローラーで開く。
    /// </summary>
    private void OpenLogFile()
    {
        if (string.IsNullOrEmpty(LogFilePath))
            return;

        try
        {
            if (File.Exists(LogFilePath))
            {
                // ログファイルをメモ帳で開く
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{LogFilePath}\"",
                    UseShellExecute = false
                });
            }
            else
            {
                // ファイルがない場合はフォルダーを開く
                OpenLogFolder();
                SetStatus("ログファイルはまだ作成されていません。フォルダーを開きました。");
            }
        }
        catch (Exception ex)
        {
            SetError($"ログファイルを開けませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// 現在の BitrateBps を StreamingSettings として永続化する（Requirements 7.5）。
    /// </summary>
    private async Task SaveBitrateAsync()
    {
        SetBusy(true);
        ClearStatus();

        try
        {
            var current = _settingsManager.Current.StreamingDefaults;
            var updated = current with { BitrateBps = BitrateBps };
            await _settingsManager.SaveStreamingSettingsAsync(updated);

            SetStatus($"ビットレートを {BitrateMbps:F1} Mbps で保存しました。次回セッション確立時に適用されます。");
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

    /// <summary>
    /// USB モード設定を永続化する。
    /// </summary>
    private async Task SaveUsbModeAsync()
    {
        SetBusy(true);
        ClearStatus();
        try
        {
            var current = _settingsManager.Current;
            var updated = current with { UsbMode = _usbMode };
            await _settingsManager.SaveAsync(updated);

            var modeText = _usbMode == UsbConnectionMode.WinUsb ? "WinUSB（ADB 不要）" : "ADB フォールバック";
            SetStatus($"USB 接続モードを「{modeText}」で保存しました。");
        }
        catch (Exception ex)
        {
            SetError($"USB モードの保存に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// ディスプレイの扱いを永続化する。
    /// </summary>
    private async Task SaveDisplayPolicyAsync()
    {
        SetBusy(true);
        ClearStatus();
        try
        {
            var current = _settingsManager.Current.DisplayDefaults;
            var updated = current with
            {
                RequireVirtualDisplay = _requireVirtualDisplay,
                ScalePercent          = _scalePercent,
                EnableTouch           = _enableTouch,
            };

            await _settingsManager.SaveDisplaySettingsAsync(updated);
            DisplaySettingsChanged?.Invoke(this, updated);

            // 効き始める時期が項目で違う。拡大率は作り直しが要るので次の接続から、
            // タッチの可否はその場で効く。混ぜて書くと誤解のもとになる。
            var note = _requireVirtualDisplay
                ? "拡張ディスプレイのみを使う設定で保存しました。"
                : "仮想ディスプレイを用意できないときは PC 画面のミラーで接続します。";

            SetStatus($"{note} 拡大率 {ScaleText}／" +
                      (_enableTouch ? "タッチあり" : "見るだけ") +
                      "。拡大率は次の接続から有効です。");
        }
        catch (Exception ex)
        {
            SetError($"ディスプレイ設定の保存に失敗しました: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadSettingsFromCache()
    {
        var settings = _settingsManager.Current;
        ApplySettingsToUi(settings);
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        // %APPDATA% などの環境変数を展開してパスを解決する
        LogFilePath = Environment.ExpandEnvironmentVariables(settings.LogFilePath);
        BitrateBps = settings.StreamingDefaults.BitrateBps;
        _usbMode = settings.UsbMode;
        _requireVirtualDisplay = settings.DisplayDefaults.RequireVirtualDisplay;
        _scalePercent          = settings.DisplayDefaults.SafeScalePercent;
        _enableTouch           = settings.DisplayDefaults.EnableTouch;
        OnPropertyChanged(nameof(BitrateMbps));
        OnPropertyChanged(nameof(RequireVirtualDisplay));
        OnPropertyChanged(nameof(AllowMirrorFallback));
        OnPropertyChanged(nameof(ScalePercent));
        OnPropertyChanged(nameof(ScaleText));
        OnPropertyChanged(nameof(EnableTouch));
        OnPropertyChanged(nameof(IsWinUsbMode));
        OnPropertyChanged(nameof(IsAdbMode));
        OnPropertyChanged(nameof(AdbStatusText));
        OnPropertyChanged(nameof(AdbStatusColor));
    }

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
