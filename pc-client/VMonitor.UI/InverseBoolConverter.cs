using System.Globalization;
using System.Windows.Data;

namespace VMonitor.UI;

/// <summary>
/// bool 値を反転する IValueConverter。
/// IsBusy=true のとき IsEnabled=false にするバインディングで使用する。
/// </summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
