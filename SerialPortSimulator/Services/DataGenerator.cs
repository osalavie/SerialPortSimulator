using System.Globalization;

namespace SerialPortSimulator.Services;

public enum DataMode { Counter, Sine, Random, Fixed }

public sealed class DataGenerator
{
    private readonly Random _random = new();
    private long _counter;

    public void Reset() => _counter = 0;

    public string Next(DataMode mode, string template)
    {
        long counter = ++_counter;
        double value = mode switch
        {
            DataMode.Sine => Math.Sin((counter - 1) * Math.PI / 10) * 100,
            DataMode.Random => _random.NextDouble() * 100,
            _ => counter
        };

        if (mode == DataMode.Fixed)
            return template;

        string resolvedTemplate = string.IsNullOrEmpty(template)
            ? "{timestamp},{counter},{value}"
            : template;

        return resolvedTemplate
            .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{counter}", counter.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", value.ToString("F2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }
}
