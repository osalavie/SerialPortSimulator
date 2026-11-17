using System.Text;

namespace SerialPortSimulator.Services;

/// <summary>Owns one control channel and processes business input in the background.</summary>
public sealed class VirtualDeviceSession : IDisposable
{
    private readonly VirtualDeviceClient _client;
    private readonly RequestResponder _responder;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _receiveLoop;
    private bool _faulted;

    public VirtualDeviceSession(VirtualDeviceInfo device, IEnumerable<KeyValuePair<string, string>> rules)
    {
        Device = device;
        _client = new VirtualDeviceClient(device.InterfacePath);
        _responder = new RequestResponder();
        foreach (var rule in rules)
            _responder.SetRule(rule.Key, rule.Value);

        _receiveLoop = ReceiveLoopAsync(_cancellation.Token);
    }

    public VirtualDeviceInfo Device { get; }
    public bool IsOpen => !_cancellation.IsCancellationRequested && !_faulted;
    public uint ProtocolVersion => _client.ProtocolVersion;
    public bool SupportsSerialConfig => _client.SupportsSerialConfig;
    public bool SupportsLineState => _client.SupportsLineState;

    public event Action<string, int>? BusinessDataReceived;
    public event Action<string, int>? ResponseSent;
    public event Action<Exception>? Faulted;

    public void Inject(byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        _client.Inject(bytes);
    }

    public void Inject(string text) => Inject(Encoding.UTF8.GetBytes(text));

    public SerialLineConfig GetSerialConfig() => _client.GetSerialConfig();
    public void SetSerialConfig(SerialLineConfig config) => _client.SetSerialConfig(config);
    public bool GetCts() => _client.GetCts();
    public void SetCts(bool enabled) => _client.SetCts(enabled);
    public SimulatorLineState GetLineState() => _client.GetLineState();

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] data = _client.Receive();
                if (data.Length > 0)
                {
                    BusinessDataReceived?.Invoke(ToDisplayText(data), data.Length);
                    foreach (var frame in _responder.Feed(data))
                    {
                        if (frame.Response is null) continue;
                        _client.Inject(frame.Response);
                        ResponseSent?.Invoke(ToDisplayText(frame.Response), frame.Response.Length);
                    }
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _faulted = true;
            Faulted?.Invoke(ex);
        }
    }

    public async Task StopAsync()
    {
        if (!_cancellation.IsCancellationRequested)
            _cancellation.Cancel();
        try { await _receiveLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (!_cancellation.IsCancellationRequested)
            _cancellation.Cancel();
        try { _receiveLoop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _client.Dispose();
        _cancellation.Dispose();
    }

    private static string ToDisplayText(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
