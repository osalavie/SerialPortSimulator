using System.Text;

namespace SerialPortSimulator.Services;

/// <summary>Frames UTF-8 CRLF requests independently of serial read chunk boundaries.</summary>
public sealed class RequestResponder
{
    private readonly List<byte> _pending = new();
    private volatile Dictionary<string, string> _rules = new(StringComparer.Ordinal);
    private const int MaximumFrameBytes = 65536;
    private bool _discarding;
    private byte _previous;

    /// <summary>Replaces the rule set atomically; safe while the receive loop reads it.</summary>
    public void SetRules(IEnumerable<KeyValuePair<string, string>> rules)
    {
        var updated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules) updated[rule.Key] = rule.Value;
        _rules = updated;
    }

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
                var rules = _rules;
                frames.Add((request, rules.TryGetValue(request, out var response)
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
