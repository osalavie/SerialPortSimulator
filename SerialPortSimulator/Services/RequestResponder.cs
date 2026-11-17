using System.Text;

namespace SerialPortSimulator.Services;

/// <summary>Frames UTF-8 CRLF requests independently of serial read chunk boundaries.</summary>
public sealed class RequestResponder
{
    private readonly List<byte> _pending = new();
    private readonly Dictionary<string, string> _rules = new(StringComparer.Ordinal);
    private const int MaximumFrameBytes = 65536;
    private bool _discarding;
    private byte _previous;

    public void SetRule(string request, string response) => _rules[request] = response;
    public void Reset() { _pending.Clear(); _discarding = false; _previous = 0; }

    public IReadOnlyList<(string Request, byte[]? Response)> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<(string, byte[]?)>();
        foreach (byte value in bytes)
        {
            bool delimiter = _previous == 13 && value == 10;
            _previous = value;
            if (_discarding)
            {
                if (delimiter) { _discarding = false; _previous = 0; }
                continue;
            }
            _pending.Add(value);
            if (delimiter)
            {
                string request = Encoding.UTF8.GetString(_pending.ToArray(), 0, _pending.Count - 2);
                frames.Add((request, _rules.TryGetValue(request, out var response)
                    ? Encoding.UTF8.GetBytes(response + "\r\n") : null));
                _pending.Clear();
                _previous = 0;
            }
            else if (_pending.Count > MaximumFrameBytes)
            {
                _pending.Clear();
                _discarding = true;
            }
        }
        return frames;
    }
}
