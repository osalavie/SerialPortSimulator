using System.Text;

namespace SerialPortSimulator.Services;

internal static class BoundedLogFile
{
    private const long MaxBytes = 1024 * 1024;

    public static void AppendPortable(string fileName, string line)
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "Logs", fileName);
        try
        {
            Append(localPath, line);
            return;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        Append(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SerialPortSimulator", fileName), line);
    }

    public static void ReplacePortable(string fileName, string content)
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "Logs", fileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllText(localPath, content);
            return;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        string fallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SerialPortSimulator", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
        File.WriteAllText(fallbackPath, content);
    }

    public static void Append(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        if (bytes.Length > MaxBytes)
            bytes = Encoding.UTF8.GetBytes(line[^Math.Min(line.Length, 32_768)..] + Environment.NewLine);

        using var stream = new FileStream(path, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length + bytes.Length > MaxBytes)
            stream.SetLength(0);
        stream.Position = stream.Length;
        stream.Write(bytes);
    }
}
