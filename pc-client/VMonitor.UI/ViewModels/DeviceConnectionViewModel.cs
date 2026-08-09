using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace VMonitor.UI.ViewModels;

/// <summary>
/// PC からスマホへ Wi-Fi で繋ぎにいく操作をまとめたビューモデル。
/// </summary>
/// <remarks>
/// スマホ側のホーム画面に出ているアドレスとポートを入れてもらう。
/// PC の前にいるときに、スマホを手に取らずに始められるようにするためのもの。
/// </remarks>
public sealed class DeviceConnectionViewModel : INotifyPropertyChanged
{
    private readonly ConnectionServer _server;

    private string _host = string.Empty;
    private string _port = ConnectionServer.DevicePort.ToString();

    public DeviceConnectionViewModel(ConnectionServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));

        _host = LoadLastHost();

        ConnectCommand = new RelayCommand(
            execute:    _ => Connect(),
            canExecute: _ => !_server.IsOutboundConnected &&
                             !_server.IsOutboundBusy &&
                             !string.IsNullOrWhiteSpace(Host));

        DisconnectCommand = new RelayCommand(
            execute:    _ => _server.DisconnectFromDevice(),
            canExecute: _ => _server.IsOutboundConnected || _server.IsOutboundBusy);

        // 状態はワーカースレッドから変わるので、UI スレッドへ移してから通知する
        _server.OutboundStateChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null) { RaiseAll(); return; }

            dispatcher.Invoke(RaiseAll);
        };
    }

    /// <summary>スマホの IP アドレス。</summary>
    public string Host
    {
        get => _host;
        set
        {
            if (_host == value) return;
            _host = value;
            OnPropertyChanged();
            RaiseCanExecute();
        }
    }

    /// <summary>スマホが待ち受けているポート。</summary>
    public string Port
    {
        get => _port;
        set
        {
            if (_port == value) return;
            _port = value;
            OnPropertyChanged();
        }
    }

    /// <summary>いまの状態（画面に出す文言）。</summary>
    public string Status => _server.OutboundStatus;

    /// <summary>接続中かどうか。</summary>
    public bool IsConnected => _server.IsOutboundConnected;

    /// <summary>スマホへ繋ぎにいく。</summary>
    public ICommand ConnectCommand { get; }

    /// <summary>繋がっているセッションを切る。</summary>
    public ICommand DisconnectCommand { get; }

    private void Connect()
    {
        if (!int.TryParse(Port, out int port) || port <= 0 || port > 65535)
            port = ConnectionServer.DevicePort;

        SaveLastHost(Host);

        // 接続はセッションが終わるまで返ってこない。UI を止めない。
        //
        // Task.Run で包むこと。ボタンから呼ばれるここは UI スレッドなので、
        // 素で呼ぶと接続処理の await がすべて UI スレッドに戻り、
        // 仮想ディスプレイの用意や画面取り込みの初期化で画面が固まる。
        var host = Host;
        _ = Task.Run(() => _server.ConnectToDeviceAsync(host, port));

        RaiseCanExecute();
    }

    // ── 前回のアドレスを覚えておく ────────────────────────────────

    /// <summary>
    /// 前回入れたアドレスの控え。
    /// </summary>
    /// <remarks>
    /// スマホの IP は毎回同じことが多い。繋ぐたびに手で打ち直すのは面倒なので、
    /// 最後に使ったものを次に出す。
    /// 設定ファイル本体には入れない（壊れても実害がなく、
    /// 共有の設定モデルを増やすほどの内容でもないため）。
    /// </remarks>
    private static string LastHostPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "vmonitor", "last-device.txt");

    private static string LoadLastHost()
    {
        try
        {
            return File.Exists(LastHostPath)
                ? File.ReadAllText(LastHostPath).Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SaveLastHost(string host)
    {
        try
        {
            var directory = Path.GetDirectoryName(LastHostPath);
            if (directory is not null) Directory.CreateDirectory(directory);

            File.WriteAllText(LastHostPath, host.Trim());
        }
        catch
        {
            // 覚えられなくても接続そのものには関係ない
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsConnected));
        RaiseCanExecute();
    }

    private void RaiseCanExecute()
    {
        ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
