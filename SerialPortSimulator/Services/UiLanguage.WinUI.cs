using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialPortSimulator.Services;

internal static partial class UiLanguage
{
    public static async Task PromptForChangeAsync(XamlRoot root)
    {
        bool switchToEnglish = !_useEnglish;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Text("切换为英文", "Switch to Chinese"),
            Content = Text("界面将立即切换为英文，无需重启。", "The UI will switch to Chinese immediately."),
            PrimaryButtonText = Text("保存", "Save"),
            CloseButtonText = Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        bool saved = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                SavePreference(switchToEnglish);
                saved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                dialog.Content = Text("无法保存语言设置，请重试。", "Could not save the language. Try again.");
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && saved)
            ApplyLanguage(switchToEnglish);
    }
}
