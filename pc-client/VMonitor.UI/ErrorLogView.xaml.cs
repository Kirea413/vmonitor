using System.Windows;
using System.Windows.Controls;
using VMonitor.UI.ViewModels;

namespace VMonitor.UI;

/// <summary>
/// エラーログ確認・設定画面のコードビハインド。
/// <list type="bullet">
///   <item>ログファイルパスの表示とフォルダー/ファイルを開くボタン（Requirements 9.5）</item>
///   <item>ビットレート入力フィールド（スライダー + 数値入力）と保存ボタン（Requirements 7.5）</item>
/// </list>
/// </summary>
public partial class ErrorLogView : UserControl
{
    /// <summary>デザイナー用のデフォルトコンストラクタ。</summary>
    public ErrorLogView()
    {
        InitializeComponent();
    }

    /// <summary>ViewModel を受け取るコンストラクタ（プロダクション用）。</summary>
    public ErrorLogView(ErrorLogViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>
    /// スライダー値変更時に ViewModel の BitrateBps を更新する。
    /// Slider の Value は Mbps 単位なので bps に変換して反映する。
    /// </summary>
    private void BitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DataContext is ErrorLogViewModel vm)
        {
            // スライダーは Mbps 単位 → bps に変換
            vm.BitrateBps = (int)(e.NewValue * 1_000_000);
        }
    }
}
