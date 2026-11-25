using System.Globalization;

namespace SerialPortSimulator.Services;

public enum DataMode { Counter, Sine, Random, Fixed }

public sealed class DataGenerator
{
    private readonly Random _random = new();
    private long _counter;

    public double SineAmplitude { get; set; } = 100;
    public double SinePeriod { get; set; } = 20;
    public double SineOffset { get; set; }
    public double RandomMinimum { get; set; }
    public double RandomMaximum { get; set; } = 100;
    public int RandomPrecision { get; set; } = 2;

    public void Reset() => _counter = 0;

    public string Next(DataMode mode, string template)
    {
        long counter = ++_counter;
        double value = mode switch
        {
            DataMode.Sine => SineOffset + SineAmplitude * Math.Sin(2 * Math.PI * (counter - 1) / SinePeriod),
            DataMode.Random => RandomMinimum + _random.NextDouble() * (RandomMaximum - RandomMinimum),
            _ => counter
        };

        if (mode == DataMode.Fixed)
            return template;

        string valueText = mode switch
        {
            DataMode.Random => value.ToString("F" + RandomPrecision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            DataMode.Sine => value.ToString("F2", CultureInfo.InvariantCulture),
            _ => counter.ToString(CultureInfo.InvariantCulture)
        };

        string resolvedTemplate = string.IsNullOrEmpty(template)
            ? "{timestamp},{counter},{value}"
            : template;

        return resolvedTemplate
            .Replace("{timestamp}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{counter}", counter.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", valueText, StringComparison.OrdinalIgnoreCase);
    }
}
