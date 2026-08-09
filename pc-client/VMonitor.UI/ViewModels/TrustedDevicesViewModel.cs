using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// 信頼済みデバイス管理画面のビューモデル。
/// <list type="bullet">
///   <item>信頼済みデバイスの一覧表示（Requirements 8.5）</item>
///   <item>デバイスの削除操作（Requirements 8.5）</item>
///   <item>初回接続時の許可確認ダイアログ（Requirements 8.1）</item>
/// </list>
/// </summary>
public sealed class TrustedDevicesViewModel : INotifyPropertyChanged
{
    // ── 依存サービス ───────────────────────────────────────────────────────
    private readonly IAuthManager _authManager;

    // ── バッキングフィールド ───────────────────────────────────────────────
    private bool _isBusy;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コンストラクタ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// プロダクション用コンストラクタ。
    /// </summary>
    public TrustedDevicesViewModel(IAuthManager authManager)
    {
        _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));

        DeleteDeviceCommand = new RelayCommand(
            execute: param =>
            {
                if (param is TrustedDeviceRowViewModel row)
                    DeleteDevice(row);
            },
            canExecute: _ => !IsBusy);

        RefreshCommand = new RelayCommand(
            execute: _ => Refresh(),
            canExecute: _ => !IsBusy);

        // 初期表示
        Refresh();
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開プロパティ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>信頼済みデバイスの行 ViewModel リスト。</summary>
    public ObservableCollection<TrustedDeviceRowViewModel> TrustedDevices { get; } = new();

    /// <summary>操作中フラグ。削除操作中などで UI を無効化する。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コマンド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// 指定デバイスを信頼済みリストから削除する（Requirements 8.5）。
    /// CommandParameter には <see cref="TrustedDeviceRowViewModel"/> を渡す。
    /// </summary>
    public ICommand DeleteDeviceCommand { get; }

    /// <summary>一覧を再読み込みする。</summary>
    public ICommand RefreshCommand { get; }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  初回接続許可ダイアログ（Requirements 8.1）
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// 初回接続デバイスの許可確認ダイアログを表示し、認証結果を返す（Requirements 8.1）。
    /// <para>
    /// このメソッドを AuthManager の showAuthorizationDialog コールバックとして登録することで、
    /// 未知のデバイスが初めて接続を試みた際に UI ダイアログが表示される。
    /// </para>
    /// </summary>
    /// <param name="device">接続を試みているデバイス情報。</param>
    /// <returns>ユーザーが許可した場合 true、拒否した場合 false。</returns>
    public static Task<bool> ShowAuthorizationDialogAsync(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var result = Application.Current?.Dispatcher.Invoke(() =>
        {
            var platform = device.Platform.ToString();
            var resolution = $"{device.PhysicalResolution.Width} × {device.PhysicalResolution.Height}";

            var message =
                $"新しいデバイスが接続を要求しています。\n\n" +
                $"デバイス名: {device.Name}\n" +
                $"プラットフォーム: {platform}\n" +
                $"解像度: {resolution}\n\n" +
                $"このデバイスの接続を許可しますか？";

            // メインウィンドウを最前面にしてからダイアログを表示する
            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null)
            {
                // バックグラウンドにいる場合でも最前面に持ってくる
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                mainWindow.Topmost = true;
            }

            // 親ウィンドウを指定する版に null を渡すと ArgumentNullException になる。
            // USB 直結ではアプリ起動直後に接続要求が来ることがあり、
            // そのときはまだメインウィンドウが用意できていない。
            var dialogResult = mainWindow != null
                ? MessageBox.Show(
                    owner: mainWindow,
                    messageBoxText: message,
                    caption: "デバイス接続の確認",
                    button: MessageBoxButton.YesNo,
                    icon: MessageBoxImage.Question,
                    defaultResult: MessageBoxResult.No)
                : MessageBox.Show(
                    messageBoxText: message,
                    caption: "デバイス接続の確認",
                    button: MessageBoxButton.YesNo,
                    icon: MessageBoxImage.Question,
                    defaultResult: MessageBoxResult.No);

            // ダイアログ表示後は Topmost を解除して通常ウィンドウに戻す
            if (mainWindow != null)
                mainWindow.Topmost = false;

            return dialogResult == MessageBoxResult.Yes;
        });

        return Task.FromResult(result ?? false);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  プライベート: 一覧管理
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>AuthManager から信頼済みデバイスを再読み込みして一覧を更新する。</summary>
    private void Refresh()
    {
        var devices = _authManager.GetTrustedDevices();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            TrustedDevices.Clear();
            foreach (var d in devices)
                TrustedDevices.Add(new TrustedDeviceRowViewModel(d));
        });
    }

    /// <summary>
    /// 指定デバイスの信頼を取り消して一覧から削除する。
    /// 削除前にユーザーへ確認ダイアログを表示する。
    /// </summary>
    private void DeleteDevice(TrustedDeviceRowViewModel row)
    {
        var confirm = MessageBox.Show(
            messageBoxText: $"「{row.Name}」を信頼済みリストから削除しますか？\n次回接続時に改めて許可が必要になります。",
            caption: "デバイスの削除",
            button: MessageBoxButton.YesNo,
            icon: MessageBoxImage.Warning,
            defaultResult: MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            _authManager.RevokeTrust(row.Device.Id);
            Application.Current?.Dispatcher.Invoke(() => TrustedDevices.Remove(row));
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
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
}

/// <summary>
/// 信頼済みデバイス一覧の 1 行を表す ViewModel。
/// </summary>
public sealed class TrustedDeviceRowViewModel
{
    /// <summary>元の TrustedDevice モデル。</summary>
    public TrustedDevice Device { get; }

    /// <summary>デバイス名（UI 表示用）。</summary>
    public string Name => Device.Name;

    /// <summary>信頼登録日時の文字列表現（ローカル時刻）。</summary>
    public string TrustedAt => Device.TrustedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    /// <summary>最終接続日時の文字列表現。未接続の場合は "未接続" と表示する。</summary>
    public string LastConnectedAt =>
        Device.LastConnectedAt.HasValue
            ? Device.LastConnectedAt.Value.LocalDateTime.ToString("yyyy/MM/dd HH:mm")
            : "未接続";

    public TrustedDeviceRowViewModel(TrustedDevice device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
    }
}
