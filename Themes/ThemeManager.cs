using System;
using System.Linq;
using System.Windows;

namespace ActivityTracker.Themes;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        var root = System.Windows.Application.Current?.Resources.MergedDictionaries
            .FirstOrDefault(dictionary =>
                dictionary.Source?.OriginalString.EndsWith(
                    "Theme.Dark.xaml",
                    StringComparison.OrdinalIgnoreCase) == true);

        if (root is null || root.MergedDictionaries.Count == 0)
            return;

        root.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/ActivityTracker;component/Themes/" +
                (theme == AppTheme.Dark
                    ? "DesignTokens.Dark.xaml"
                    : "DesignTokens.Light.xaml"),
                UriKind.Absolute)
        };
    }
}
