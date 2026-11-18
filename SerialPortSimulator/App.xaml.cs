using Microsoft.UI.Xaml;
using SerialPortSimulator.Services;

namespace SerialPortSimulator;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                string details = args.Exception.ToString();
                BoundedLogFile.ReplacePortable(
                    "app-crash.log",
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}" +
                    details[..Math.Min(details.Length, 65_536)]);
            }
            catch
            {
                // Preserve the original unhandled exception.
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiLanguage.AddResources(Resources);
        _window = new MainWindow();
        _window.Activate();
    }
}
