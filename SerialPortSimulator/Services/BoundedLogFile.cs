using System.Text;

namespace SerialPortSimulator.Services;

internal static class BoundedLogFile
{
    private const long MaxBytes = 1024 * 1024;

    public static void AppendPortable(string fileName, string line) =>
        Append(Path.Combine(AppContext.BaseDirectory, "Logs", fileName), line);

    public static void ReplacePortable(string fileName, string content)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Logs", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
