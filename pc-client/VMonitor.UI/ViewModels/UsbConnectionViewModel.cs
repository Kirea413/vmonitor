using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// USB 直結の接続操作をまとめたビューモデル。
/// </summary>
/// <remarks>
/// USB では PC 側が端末を掴みにいくので、繋ぐ・切るの操作は PC にも要る。
/// スマホ側からしか操作できないと、手元に PC しかないときに詰む。
/// </remarks>
public sealed class UsbConnectionViewModel : INotifyPropertyChanged
{
    private readonly ConnectionServer _server;

    public UsbConnectionViewModel(ConnectionServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));

        // 繋がっているときも押せるようにする。
        //
        // 以前は接続中に無効化していたが、それだと「繋がっている状態で
        // 押しても反応がない」ことになる。押されたら繋ぎ直して、
        // スマホ側に改めて承認を出すのが期待どおりの動き。
        ConnectCommand = new RelayCommand(
            execute: _ => _server.ConnectUsbNow(),
            canExecute: _ => true);

        DisconnectCommand = new RelayCommand(
            execute: _ => _server.DisconnectUsb(),
            canExecute: _ => _server.IsUsbConnected);

        // 状態はワーカースレッドから変わるので、UI スレッドへ移してから通知する
        _server.UsbStateChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null) { RaiseAll(); return; }

            dispatcher.Invoke(RaiseAll);
        };
    }

    /// <summary>いまの状態（画面に出す文言）。</summary>
    public string Status => _server.UsbStatus;

    /// <summary>映像が流れるセッションが確立しているか。</summary>
    public bool IsConnected => _server.IsUsbConnected;

    /// <summary>ケーブルで繋がっているか（セッションとは別）。</summary>
    public bool IsLinkUp => _server.IsUsbLinkUp;

    /// <summary>
    /// 段階を一言で表す。
    /// </summary>
    /// <remarks>
    /// 「接続」には「ケーブルが挿さっている」と「映像が流れている」の
    /// 2 つの意味があり、混ぜると話が通じなくなる。ここで区別して見せる。
    /// </remarks>
    public string StageLabel =>
        _server.IsUsbConnected ? "接続中"
        : _server.IsUsbLinkUp  ? "ケーブルのみ"
        :                        "未接続";

    /// <summary>いますぐ接続を試す。</summary>
    public ICommand ConnectCommand { get; }

    /// <summary>いま繋がっているセッションを切る。</summary>
    public ICommand DisconnectCommand { get; }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsLinkUp));
        OnPropertyChanged(nameof(StageLabel));

        ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
