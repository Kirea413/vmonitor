using System.Windows.Controls;

namespace VMonitor.UI;

/// <summary>
/// 信頼済みデバイス管理ビュー。
/// デバイス一覧の表示と削除操作 UI を提供する（Requirements 8.5）。
/// </summary>
public partial class TrustedDevicesView : UserControl
{
    /// <summary>デザイナー用のデフォルトコンストラクタ。</summary>
    public TrustedDevicesView()
    {
        InitializeComponent();
    }
}
