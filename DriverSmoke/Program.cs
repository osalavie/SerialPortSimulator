using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SerialPortSimulator.Services;

VirtualDeviceInfo device = VirtualDeviceClient.EnumerateDevices().FirstOrDefault()
    ?? throw new InvalidOperationException("No Serial Port Simulator device was found.");

Console.WriteLine($"Testing {device.PortName} ({device.InstanceId})");

if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
{
    foreach (var item in VirtualDeviceClient.EnumerateDevices())
    {
        using var controller = new VirtualDeviceClient(item.InterfacePath);
        Console.WriteLine($"{item.PortName}: protocol {controller.ProtocolVersion}, " +
            $"serial config {controller.SupportsSerialConfig}, CTS {controller.SupportsModemLines}");
    }
    return;
}

if (args.Length > 0 && args[0].Equals("--inject-loop", StringComparison.OrdinalIgnoreCase))
{
    int seconds = args.Length > 1 && int.TryParse(args[1], out int parsed)
        ? Math.Clamp(parsed, 1, 60)
        : 5;
    using var injector = new VirtualDeviceClient(device.InterfacePath);
    DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
    int sequence = 0;
    while (DateTime.UtcNow < deadline)
    {
        injector.Inject(System.Text.Encoding.UTF8.GetBytes($"loop-{++sequence}\r\n"));
        await Task.Delay(250);
    }
    Console.WriteLine($"Injected {sequence} messages.");
    return;
}

if (args.Length > 0 && args[0].Equals("--line-test", StringComparison.OrdinalIgnoreCase))
{
    using var controller = new VirtualDeviceClient(device.InterfacePath);
    if (controller.ProtocolVersion < 3)
        throw new InvalidOperationException("Line tests require protocol version 3. Update the installed driver.");
    var original = controller.GetSerialConfig();
    bool originalCts = controller.GetCts();
    try
    {
        controller.SetCts(true);
        controller.SetSerialConfig(new SerialLineConfig(1200, 8, SimulatorParity.None,
            SimulatorStopBits.One, true));
        using var port = new SerialPort(device.PortName, 1200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 3000,
            WriteTimeout = 3000,
            Handshake = Handshake.None
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        controller.Receive();
        byte[] payload = Enumerable.Repeat((byte)0x5A, 120).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        controller.Inject(payload);
        await Task.Delay(250);
        int early = port.BytesToRead;
        double theoretical = clock.Elapsed.TotalSeconds * 120;
        if (early == 0 || early > theoretical + 15 || early >= payload.Length)
            throw new InvalidOperationException($"RX baud limit failed: {early} bytes after {clock.ElapsedMilliseconds} ms.");
        byte[] received = new byte[payload.Length];
        int offset = 0;
        while (offset < received.Length)
            offset += port.Read(received, offset, received.Length - offset);
        if (!received.SequenceEqual(payload))
            throw new InvalidOperationException("RX payload changed during baud-limited transfer.");

        controller.SetSerialConfig(new SerialLineConfig(9600, 8, SimulatorParity.None,
            SimulatorStopBits.One));
        port.Handshake = Handshake.RequestToSend;
        controller.SetCts(false);
        port.Write(payload, 0, 16);
        await Task.Delay(100);
        if (controller.Receive().Length != 0)
            throw new InvalidOperationException("TX passed through while CTS was low.");
        controller.SetCts(true);
        await Task.Delay(100);
        if (!controller.Receive().SequenceEqual(payload.AsSpan(0, 16).ToArray()))
            throw new InvalidOperationException("TX did not resume after CTS became active.");
        Console.WriteLine("PASS: 1200 8N1 receive pacing and CTS transmit hold/resume.");
    }
    finally
    {
        controller.SetCts(originalCts);
        controller.SetSerialConfig(original);
    }
    return;
}

var expectedConfig = new SerialLineConfig(
    19200,
    7,
    SimulatorParity.Even,
    SimulatorStopBits.Two);
var responseSent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
string token = Guid.NewGuid().ToString("N");
string injectedText = $"hello-{token}";
string request = $"PING-{token}";
string expectedResponse = $"PONG-{token}";

using var session = new VirtualDeviceSession(device,
    new[] { new KeyValuePair<string, string>(request, expectedResponse) });
session.ResponseSent += (text, _) => responseSent.TrySetResult(text);
session.SetSerialConfig(expectedConfig);
if (session.GetSerialConfig() != expectedConfig)
    throw new InvalidOperationException("Simulator serial configuration did not round-trip.");
string preOpenText = $"pre-open-{token}";
session.Inject(preOpenText + "\r\n");

using (var port = new SerialPort(device.PortName, 9600, Parity.None, 8, StopBits.One)
       {
           Encoding = System.Text.Encoding.UTF8,
           Handshake = Handshake.None,
           NewLine = "\r\n",
           ReadTimeout = 3000,
           WriteTimeout = 3000,
           DtrEnable = false,
           RtsEnable = false
       })
{
    port.Open();
    string preOpenData = port.ReadLine();
    if (preOpenData != preOpenText)
        throw new InvalidOperationException(
            $"Pre-open injection mismatch: {Escape(preOpenData)}");
    port.DiscardInBuffer();
    port.DiscardOutBuffer();

    session.Inject(injectedText + "\r\n");
    string injected = port.ReadLine();
    if (injected != injectedText)
        throw new InvalidOperationException($"Injected data mismatch: {Escape(injected)}");

    port.Write(request + "\r\n");
    Task completed = await Task.WhenAny(responseSent.Task, Task.Delay(3000));
    if (completed != responseSent.Task)
        throw new TimeoutException("The request responder did not send a response.");

    string response = port.ReadLine();
    if (response != expectedResponse)
        throw new InvalidOperationException($"Response mismatch: {Escape(response)}");
}

using SafeFileHandle serialHandle = CreateFile(
    @"\\?\" + device.PortName,
    0xC0000000,
    0,
    IntPtr.Zero,
    3,
    0,
    IntPtr.Zero);
if (serialHandle.IsInvalid)
    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

var baudBytes = new byte[4];
if (!DeviceIoControl(serialHandle, 0x1B0050, null, 0, baudBytes, baudBytes.Length, out _, IntPtr.Zero))
    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
uint actualBaud = BitConverter.ToUInt32(baudBytes);

var lineControl = new byte[3];
if (!DeviceIoControl(serialHandle, 0x1B0054, null, 0, lineControl, lineControl.Length, out _, IntPtr.Zero))
    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

if (actualBaud != expectedConfig.BaudRate)
    throw new InvalidOperationException($"Baud query mismatch: {actualBaud}");
if (lineControl[0] != 2 || lineControl[1] != 2 || lineControl[2] != 7)
    throw new InvalidOperationException(
        $"Line control mismatch: stop={lineControl[0]} parity={lineControl[1]} data={lineControl[2]}");

session.SetSerialConfig(new SerialLineConfig(
    9600,
    8,
    SimulatorParity.None,
    SimulatorStopBits.One));

Console.WriteLine(
    "PASS: exclusive COM open, simulator-owned 19200 7E2, business-side settings ignored, UTF-8 I/O, and CRLF response.");

static string Escape(string value) =>
    value.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
    uint creation, uint flags, IntPtr template);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputLength,
    byte[] output, int outputLength, out int returned, IntPtr overlapped);
