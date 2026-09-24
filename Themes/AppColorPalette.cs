using System;
using MediaColor = System.Windows.Media.Color;

namespace ActivityTracker.Themes;

public static class AppColorPalette
{
    private static readonly MediaColor[] Colors =
    {
        MediaColor.FromRgb(0x66, 0xC0, 0xF4),
        MediaColor.FromRgb(0x4C, 0xC3, 0x8A),
        MediaColor.FromRgb(0xE3, 0xB3, 0x41),
        MediaColor.FromRgb(0xE5, 0x48, 0x4D),
        MediaColor.FromRgb(0xB5, 0x7B, 0xFF),
        MediaColor.FromRgb(0x3D, 0xD6, 0xD0),
        MediaColor.FromRgb(0xFF, 0x8A, 0x5B),
        MediaColor.FromRgb(0x9B, 0xA3, 0xB4)
    };

    public static MediaColor ForAppId(string appId)
    {
        var value = appId ?? string.Empty;
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;

        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return Colors[hash % (uint)Colors.Length];
    }
}
