using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace SerialPortSimulator.Services;

internal static class TitleBarChrome
{
    public static void Attach(Window window, FrameworkElement themeRoot, UIElement dragRegion, ColumnDefinition captionInset)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(dragRegion);

        AppWindowTitleBar titleBar = window.AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        void UpdateInset() => captionInset.Width = new GridLength(titleBar.RightInset);

        void ApplyColors()
        {
            bool dark = themeRoot.ActualTheme == ElementTheme.Dark;
            Color foreground = dark ? Colors.White : Colors.Black;
            Color inactiveForeground = dark
                ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x80, 0, 0, 0);
            Color hoverBackground = dark
                ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x12, 0, 0, 0);
            Color pressedBackground = dark
                ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x20, 0, 0, 0);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
            titleBar.ButtonPressedForegroundColor = foreground;
            UpdateInset();
        }

        themeRoot.ActualThemeChanged += (_, _) => ApplyColors();
        themeRoot.SizeChanged += (_, _) => UpdateInset();
        window.Activated += (_, _) => UpdateInset();
        ApplyColors();
    }
}
