using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialPortSimulator.Services;

/// <summary>Localized text attached to an element so a language switch can update it in place.</summary>
public static class UiText
{
    private static readonly List<WeakReference<DependencyObject>> Targets = new();

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(UiText), new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty TipProperty = DependencyProperty.RegisterAttached(
        "Tip", typeof(string), typeof(UiText), new PropertyMetadata(null, OnTipChanged));

    public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);

    public static void SetTip(DependencyObject element, string value) => element.SetValue(TipProperty, value);

    public static string? GetTip(DependencyObject element) => (string?)element.GetValue(TipProperty);

    public static void RefreshAll()
    {
        foreach (WeakReference<DependencyObject> reference in Targets.ToArray())
        {
            if (!reference.TryGetTarget(out DependencyObject? target))
            {
                Targets.Remove(reference);
                continue;
            }

            ApplyContent(target);
            ApplyTip(target);
        }
    }

    public static void RefreshSelection(ComboBox combo)
    {
        int index = combo.SelectedIndex;
        if (index < 0) return;

        combo.SelectedIndex = -1;
        combo.SelectedIndex = index;
    }

    private static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        Register(target);
        ApplyContent(target);
    }

    private static void OnTipChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        Register(target);
        ApplyTip(target);
    }

    private static void Register(DependencyObject target)
    {
        foreach (WeakReference<DependencyObject> reference in Targets.ToArray())
            if (!reference.TryGetTarget(out _)) Targets.Remove(reference);

        if (Targets.Any(reference => reference.TryGetTarget(out DependencyObject? existing) &&
                                     ReferenceEquals(existing, target)))
            return;

        Targets.Add(new WeakReference<DependencyObject>(target));
    }

    private static void ApplyContent(DependencyObject target)
    {
        string? key = GetKey(target);
        if (string.IsNullOrEmpty(key)) return;

        string value = UiLanguage.Get(key);
        switch (target)
        {
            case TabViewItem tab:
                tab.Header = value;
                break;
            case TextBlock textBlock:
                textBlock.Text = value;
                break;
            case TextBox textBox:
                textBox.PlaceholderText = value;
                break;
            case ComboBox comboBox:
                comboBox.PlaceholderText = value;
                break;
            case ContentControl contentControl:
                contentControl.Content = value;
                break;
        }
    }

    private static void ApplyTip(DependencyObject target)
    {
        string? key = GetTip(target);
        if (string.IsNullOrEmpty(key)) return;

        ToolTipService.SetToolTip(target, UiLanguage.Get(key));
    }
}
