namespace SerialPortSimulator.BusinessTest;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        string? portName = GetOption(args, "--port");
        if (portName is not null)
        {
            int seconds = int.TryParse(GetOption(args, "--seconds"), out int parsedSeconds)
                ? Math.Clamp(parsedSeconds, 1, 60)
                : 5;
            string? logPath = GetOption(args, "--log");
            form.Shown += async (_, _) =>
            {
                form.PrepareAutomation(portName, logPath);
                form.OpenPortForAutomation();
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                form.ClosePortForAutomation();
                form.Close();
            };
        }
        Application.Run(form);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }
}
