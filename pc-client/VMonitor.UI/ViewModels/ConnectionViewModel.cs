using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VMonitor.Core.Interfaces;
using VMonitor.Core.Models;

// VMonitor.Session 名前空間と Session モデル型の衝突を回避するエイリアス
using SessionModel = VMonitor.Core.Models.Session;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// メインウィンドウの接続管理エリアを担うビューモデル。
/// <list type="bullet">
///   <item>接続候補リストの表示（Requirements 2.1, 2.2）</item>
///   <item>接続状態インジケーター（Connecting / Active / Reconnecting / Terminated）</item>
///   <item>タイムアウト通知と再試行 UI（Requirements 2.4）</item>
///   <item>切断通知と再接続 UI（Requirements 2.6）</item>
/// </list>
/// </summary>
public sealed class ConnectionViewModel : INotifyPropertyChanged, IDisposable
{
    // ── 再接続タイムアウト ─────────────────────────────────────────────────
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);

    // ── 依存サービス ───────────────────────────────────────────────────────
    private readonly ISessionManager _sessionManager;

    // ── 状態フィールド ─────────────────────────────────────────────────────
    private SessionModel? _activeSession;
    private CancellationTokenSource? _reconnectCts;

    // ── バッキングフィールド ───────────────────────────────────────────────
    private string _connectionStatus = "未接続";
    private bool _isBusy;
    private bool _showTimeoutNotification;
    private bool _showDisconnectNotification;
    private string _notificationMessage = string.Empty;
    private ConnectionCandidateViewModel? _selectedCandidate;

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コンストラクタ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// プロダクション用コンストラクタ。
    /// </summary>
    public ConnectionViewModel(ISessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sessionManager.SessionDisconnected += OnSessionDisconnected;

        // コマンド初期化
        ConnectCommand = new RelayCommand(
            execute: _ => _ = ConnectAsync(),
            canExecute: _ => !IsBusy && SelectedCandidate is not null);

        RetryConnectCommand = new RelayCommand(
            execute: _ => _ = RetryConnectAsync(),
            canExecute: _ => !IsBusy);

        ReconnectCommand = new RelayCommand(
            execute: _ => _ = ReconnectAsync(),
            canExecute: _ => !IsBusy);

        DisconnectCommand = new RelayCommand(
            execute: _ => _ = DisconnectAsync(),
            canExecute: _ => !IsBusy && _activeSession is not null);

        DismissNotificationCommand = new RelayCommand(
            execute: _ => DismissNotification());
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  公開プロパティ
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// USB 直結の接続操作。接続サーバーが用意できてから差し込まれる。
    /// </summary>
    /// <remarks>
    /// 接続サーバーはこのビューモデルより後に組み立てられるので、
    /// 生成時ではなく後から渡す。使わない場面（テストなど）では null のまま。
    /// </remarks>
    public UsbConnectionViewModel? Usb
    {
        get => _usb;
        set { _usb = value; OnPropertyChanged(); }
    }

    private UsbConnectionViewModel? _usb;

    /// <summary>
    /// PC からスマホへ Wi-Fi で繋ぎにいく操作。<see cref="Usb"/> と同じく後から差し込む。
    /// </summary>
    public DeviceConnectionViewModel? Device
    {
        get => _device;
        set { _device = value; OnPropertyChanged(); }
    }

    private DeviceConnectionViewModel? _device;

    /// <summary>接続候補のリスト。mDNS 検出・USB 接続イベントで追加される。</summary>
    public ObservableCollection<ConnectionCandidateViewModel> Candidates { get; } = new();

    /// <summary>
    /// 一覧で何か選ばれているか。
    /// </summary>
    /// <remarks>
    /// 画面の表示切り替えに使う。SelectedCandidate をそのまま
    /// BooleanToVisibilityConverter に渡していたが、bool ではないので
    /// 変換できず、選んでも詳細が出ないままだった。
    /// </remarks>
    public bool HasSelection => _selectedCandidate is not null;

    /// <summary>現在選択されている接続候補。</summary>
    public ConnectionCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 現在の接続ステータスを表す文字列。
    /// "未接続" / "接続中..." / "接続済み" / "再接続中..." / "切断"
    /// </summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    /// <summary>接続操作中などで UI を無効化するフラグ。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary>
    /// タイムアウト通知を表示するかどうか（Requirements 2.4）。
    /// true の場合、通知バナーを表示して再試行オプションを有効化する。
    /// </summary>
    public bool ShowTimeoutNotification
    {
        get => _showTimeoutNotification;
        private set => SetField(ref _showTimeoutNotification, value);
    }

    /// <summary>
    /// 切断通知を表示するかどうか（Requirements 2.6）。
    /// true の場合、通知バナーを表示して再接続オプションを有効化する。
    /// </summary>
    public bool ShowDisconnectNotification
    {
        get => _showDisconnectNotification;
        private set => SetField(ref _showDisconnectNotification, value);
    }

    /// <summary>通知バナーに表示するメッセージ。</summary>
    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => SetField(ref _notificationMessage, value);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  コマンド
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>選択中の候補へ接続する。</summary>
    public ICommand ConnectCommand { get; }

    /// <summary>タイムアウト後に再試行する（Requirements 2.4）。</summary>
    public ICommand RetryConnectCommand { get; }

    /// <summary>切断後に再接続する（Requirements 2.6）。</summary>
    public ICommand ReconnectCommand { get; }

    /// <summary>現在のセッションを切断する。</summary>
    public ICommand DisconnectCommand { get; }

    /// <summary>通知バナーを閉じる。</summary>
    public ICommand DismissNotificationCommand { get; }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  接続候補管理（外部から呼び出す）
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// 接続候補を追加する。既存候補（同一 DeviceId）は重複追加しない。
    /// </summary>
    public void AddCandidate(DeviceInfo device, TransportType transport)
    {
        if (Candidates.Any(c => c.Device.Id == device.Id))
            return;

        var vm = new ConnectionCandidateViewModel(device, transport);
        Application.Current?.Dispatcher.Invoke(() => Candidates.Add(vm));
    }

    /// <summary>
    /// スマホから接続が来て認証済みになったことを通知する。
    /// 候補リストをクリアして接続済み状態を表示する。
    /// </summary>
    public void SetConnected(DeviceInfo device, TransportType transport)
    {
        Candidates.Clear();
        var vm = new ConnectionCandidateViewModel(device, transport);
        Candidates.Add(vm);
        SelectedCandidate = vm;
        ConnectionStatus = $"接続済み — {device.Name}";
        DismissNotification();
    }

    /// <summary>
    /// 接続が切断されたことを通知する。
    /// </summary>
    public void SetDisconnected()
    {
        ConnectionStatus = "切断されました";
        ShowDisconnectBanner("スマホとの接続が切断されました。");

        // 繋がっている扱いを解く。
        //
        // 選択したままにすると、切れたあとも「この端末に接続中」の
        // 見た目が残る。電源が落ちて切れた場合など、繋がっているつもりで
        // 操作してしまう。行そのものは、端末が見えている限り残す
        // （また繋げるため）。見えなくなったら接続サーバー側が取り除く。
        Application.Current?.Dispatcher.Invoke(() => SelectedCandidate = null);
    }

    /// <summary>
    /// 指定デバイス ID の候補を削除する（デバイス切断など）。
    /// </summary>
    public void RemoveCandidate(DeviceIdentifier deviceId)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var target = Candidates.FirstOrDefault(c => c.Device.Id == deviceId);
            if (target is not null)
                Candidates.Remove(target);
        });
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  プライベート: 接続ロジック
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private async Task ConnectAsync()
    {
        if (SelectedCandidate is null)
            return;

        DismissNotification();

        // 実際に繋ぐのは接続サーバー。経路ごとに手順が違うので、
        // 選ばれた端末の種類で振り分ける。
        //
        // ここから直接セッションを張ろうとしていたが、それは
        // USB のアクセサリー切り替えも仮想ディスプレイの用意も通らない
        // 別経路で、押しても繋がらなかった。
        if (SelectedCandidate.Transport == TransportType.USB)
        {
            if (Usb is not null)
            {
                ConnectionStatus = "接続中...";
                Usb.ConnectCommand.Execute(null);
                return;
            }
        }
        else if (Device is not null)
        {
            ConnectionStatus = "接続中...";
            Device.ConnectCommand.Execute(null);
            return;
        }

        SetBusy(true);
        ConnectionStatus = "接続中...";

        using var cts = new CancellationTokenSource();
        try
        {
            var session = await _sessionManager.EstablishSessionAsync(
                SelectedCandidate.Device, cts.Token);

            _activeSession = session;
            ConnectionStatus = "接続済み";
        }
        catch (TimeoutException)
        {
            // Requirements 2.4: 10 秒超でタイムアウト通知 + 再試行オプション
            ConnectionStatus = "接続タイムアウト";
            ShowTimeoutBanner();
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "接続キャンセル";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "接続エラー";
            NotificationMessage = $"接続に失敗しました: {ex.Message}";
            ShowDisconnectNotification = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RetryConnectAsync()
    {
        DismissNotification();
        await ConnectAsync();
    }

    private async Task ReconnectAsync()
    {
        if (_activeSession is null)
            return;

        DismissNotification();
        SetBusy(true);
        ConnectionStatus = "再接続中...";

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        try
        {
            var result = await _sessionManager.TryReconnectAsync(
                _activeSession, ReconnectTimeout, _reconnectCts.Token);

            if (result == ReconnectResult.Success)
            {
                ConnectionStatus = "接続済み";
            }
            else
            {
                // 30 秒タイムアウト後
                _activeSession = null;
                ConnectionStatus = "切断";
                ShowDisconnectBanner("30 秒間の再接続に失敗しました。");
            }
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "再接続キャンセル";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DisconnectAsync()
    {
        if (_activeSession is null)
            return;

        SetBusy(true);
        ConnectionStatus = "切断中...";

        try
        {
            _reconnectCts?.Cancel();
            await _sessionManager.TerminateSessionAsync(_activeSession);
            _activeSession = null;
            ConnectionStatus = "未接続";
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  イベントハンドラー
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// セッションが切断されたときに呼ばれる（Requirements 2.6）。
    /// UI スレッドで通知バナーを表示する。
    /// </summary>
    private void OnSessionDisconnected(object? sender, SessionDisconnectedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _activeSession = e.Session;
            ConnectionStatus = "切断";
            ShowDisconnectBanner("接続が切断されました。再接続しますか？");
        });
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    //  ヘルパー
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private void ShowTimeoutBanner()
    {
        NotificationMessage = "セッションの確立が 10 秒を超えました。再試行しますか？";
        ShowTimeoutNotification = true;
        ShowDisconnectNotification = false;
    }

    private void ShowDisconnectBanner(string message)
    {
        NotificationMessage = message;
        ShowDisconnectNotification = true;
        ShowTimeoutNotification = false;
    }

    private void DismissNotification()
    {
        ShowTimeoutNotification = false;
        ShowDisconnectNotification = false;
        NotificationMessage = string.Empty;
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        CommandManager.InvalidateRequerySuggested();
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
        _sessionManager.SessionDisconnected -= OnSessionDisconnected;
        _reconnectCts?.Dispose();
    }
}
