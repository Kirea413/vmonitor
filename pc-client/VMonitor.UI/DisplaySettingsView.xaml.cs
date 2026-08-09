using System.Windows.Controls;
using VMonitor.UI.ViewModels;

namespace VMonitor.UI;

/// <summary>
/// ディスプレイ設定画面のコードビハインド。
/// <list type="bullet">
///   <item>DisplayMode 切り替え（複製・拡張・セカンダリのみ）（Requirements 7.1）</item>
///   <item>解像度プリセット ComboBox と手動入力フォーム（Requirements 7.2）</item>
/// </list>
/// </summary>
public partial class DisplaySettingsView : UserControl
{
    /// <summary>
    /// デザイナー用のデフォルトコンストラクタ。
    /// </summary>
    public DisplaySettingsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// ViewModel を受け取るコンストラクタ（プロダクション用）。
    /// </summary>
    public DisplaySettingsView(DisplaySettingsViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
