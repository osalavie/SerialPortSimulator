namespace SerialPortSimulator.Services;

internal static partial class UiLanguage
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SerialPortSimulator", "language.txt");

    private static readonly bool UseEnglish = ReadPreference();

    public static string Text(string chinese, string english) => UseEnglish ? english : chinese;

    private static bool ReadPreference()
    {
        try
        {
            return File.ReadAllText(PreferencePath).Trim() == "en";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
