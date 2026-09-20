using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ActivityTracker.Views;

// 字符串为空时折叠元素，避免界面出现空行
internal sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var text = value as string;

        return string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
