using System;
using System.Windows;

namespace ActivityTracker.Views.Dev;

public partial class StyleGalleryWindow : Window
{
    public StyleGalleryWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
