using System.Windows;
using VMonitor.UI.ViewModels;

namespace VMonitor.UI;

/// <summary>
/// vmonitor メインウィンドウ。
/// 接続候補リスト・接続状態インジケーター・切断/再接続通知 UI を提供する（Requirements 2.4, 2.6）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConnectionViewModel? _viewModel;

    /// <summary>
    /// デザイナー用のデフォルトコンストラクタ。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// ViewModel を受け取るコンストラクタ（プロダクション用）。
    /// </summary>
    /// <param name="viewModel">接続タブのビューモデル。</param>
    /// <param name="settingsViewModel">
    /// 設定タブのビューモデル。設定タブは XAML 上で直接置かれているため、
    /// ここで明示的に結び付けないと何にもバインドされず、
    /// 画面には出るのに操作しても何も起きない状態になる。
    /// </param>
    public MainWindow(ConnectionViewModel viewModel, ErrorLogViewModel? settingsViewModel = null) : this()
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        if (settingsViewModel is not null)
            SettingsView.DataContext = settingsViewModel;
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }
}
