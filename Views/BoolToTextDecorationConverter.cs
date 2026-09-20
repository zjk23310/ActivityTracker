using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ActivityTracker.Views;

// 将 bool 转换为 TextDecorations（true → Strikethrough，false → None）
// 用于待办事项完成后显示删除线
internal sealed class BoolToTextDecorationConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is bool completed && completed)
            return TextDecorations.Strikethrough;

        return new TextDecorationCollection();
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
