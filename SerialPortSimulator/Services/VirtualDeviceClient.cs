using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SerialPortSimulator.Services;

public sealed record VirtualDeviceInfo(string InterfacePath, string InstanceId, string PortName)
{
    public string DisplayName => $"{PortName}  ·  {InstanceId}";
}

public enum SimulatorParity : byte
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4
}

public enum SimulatorStopBits : byte
{
    One = 0,
    OnePointFive = 1,
    Two = 2
}

public sealed record SerialLineConfig(
    uint BaudRate,
    byte DataBits,
    SimulatorParity Parity,
    SimulatorStopBits StopBits,
    bool LimitToBaudRate = false)
{
    public void Validate()
    {
        if (BaudRate is 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(BaudRate), UiLanguage.Text("波特率必须为 1 到 2,147,483,647。", "Use a baud rate from 1 to 2,147,483,647."));
        if (DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(DataBits), UiLanguage.Text("数据位必须为 5 到 8。", "Use 5 to 8 data bits."));
        if (!Enum.IsDefined(Parity))
            throw new ArgumentOutOfRangeException(nameof(Parity));
        if (!Enum.IsDefined(StopBits))
            throw new ArgumentOutOfRangeException(nameof(StopBits));
        if (StopBits == SimulatorStopBits.OnePointFive && DataBits != 5)
            throw new ArgumentOutOfRangeException(nameof(StopBits), UiLanguage.Text("1.5 停止位只适用于 5 数据位。", "Use 1.5 stop bits with 5 data bits only."));
        if (StopBits == SimulatorStopBits.Two && DataBits == 5)
            throw new ArgumentOutOfRangeException(nameof(StopBits), UiLanguage.Text("5 数据位不能使用 2 停止位。", "5 data bits cannot use 2 stop bits."));
    }
}

public sealed record SimulatorLineState(
    bool Cts,
    bool Dsr,
    bool Dcd,
    bool Dtr,
    bool Rts,
    bool CtsHandshake);

/// <summary>Binary control transport for our UMDF device; never opens a COM name.</summary>
public sealed class VirtualDeviceClient : IDisposable
{
    public static readonly Guid InterfaceId = new("878a35e1-b89e-4a11-9145-8d4b3e7c1a22");
    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();
    private static readonly uint InjectCode = CtlCode(0x800, 2);
    private static readonly uint ReceiveCode = CtlCode(0x801, 1);
    private static readonly uint VersionCode = CtlCode(0x802, 1);
    private static readonly uint GetSerialConfigCode = CtlCode(0x803, 1);
    private static readonly uint SetSerialConfigCode = CtlCode(0x804, 2);
    private static readonly uint GetModemLinesCode = CtlCode(0x805, 1);
    private static readonly uint SetModemLinesCode = CtlCode(0x806, 2);
    private static readonly uint GetLineStateCode = CtlCode(0x807, 1);
    private const uint CtsOn = 0x10;
    private const uint DsrOn = 0x20;
    private const uint DcdOn = 0x80;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeSerialConfig
    {
        public uint BaudRate;
        public byte DataBits;
        public byte Parity;
        public byte StopBits;
        public byte FlowControl;
    }

    public static IReadOnlyList<VirtualDeviceInfo> EnumerateDevices()
    {
        return EnumeratePaths()
            .Select(path =>
            {
                string instanceId = GetInstanceId(path);
                return (Path: path, InstanceId: instanceId, PortName: ReadPortName(instanceId));
            })
            .Where(device => device.PortName is not null)
            .Select(device => new VirtualDeviceInfo(device.Path, device.InstanceId, device.PortName!))
            .OrderBy(device => PortNumber(device.PortName))
            .ToArray();
    }

    public static string[] EnumeratePaths()
    {
        var id = InterfaceId;
        for (int attempt = 0; attempt < 3; attempt++) {
            uint result = CM_Get_Device_Interface_List_Size(out uint length, ref id, null, 0);
            if (result != 0) throw new InvalidOperationException(UiLanguage.Text($"枚举控制接口失败：CONFIGRET {result}", $"Could not list control links: CONFIGRET {result}"));
            var buffer = new char[length];
            result = CM_Get_Device_Interface_List(ref id, null, buffer, length, 0);
            if (result == 0) return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (result != 0x1A) throw new InvalidOperationException(UiLanguage.Text($"读取控制接口失败：CONFIGRET {result}", $"Could not read control links: CONFIGRET {result}"));
        }
        throw new InvalidOperationException(UiLanguage.Text("设备列表持续变化，请重试。", "The device list keeps changing. Try again."));
    }

    private static string GetInstanceId(string interfacePath)
    {
        const string prefix = @"\\?\";
        const string interfaceSeparator = "#{";
        int start = interfacePath.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : 0;
        int end = interfacePath.IndexOf(interfaceSeparator, start, StringComparison.Ordinal);
        string encoded = end > start ? interfacePath[start..end] : interfacePath[start..];
        return encoded.Replace('#', '\\');
    }

    private static string? ReadPortName(string instanceId)
    {
        string keyPath = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters";
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("PortName") as string;
    }

    private static int PortNumber(string name) =>
        name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name.AsSpan(3), out int number)
            ? number
            : int.MaxValue;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid id, string? device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static extern uint CM_Get_Device_Interface_List(ref Guid id, string? device, [Out] char[] buffer, uint length, uint flags);

    public VirtualDeviceClient(string deviceInterfacePath)
    {
        _handle = CreateFile(deviceInterfacePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_handle.IsInvalid) {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
        try {
            var version = new byte[4];
            if (Call(VersionCode, Array.Empty<byte>(), version) != 4)
                throw new InvalidOperationException(UiLanguage.Text("虚拟串口驱动协议版本不兼容。", "The driver protocol is not compatible."));
            ProtocolVersion = BitConverter.ToUInt32(version);
            if (ProtocolVersion is < 1 or > 4)
                throw new InvalidOperationException(UiLanguage.Text($"不支持的虚拟串口驱动协议版本：{ProtocolVersion}。", $"Driver protocol version {ProtocolVersion} is not supported."));
        }
        catch { _handle.Dispose(); throw; }
    }

    public uint ProtocolVersion { get; }
    public bool SupportsSerialConfig => ProtocolVersion >= 2;
    public bool SupportsModemLines => ProtocolVersion >= 3;
    public bool SupportsLineState => ProtocolVersion >= 4;

    public bool GetCts()
    {
        RequireModemLinesSupport();
        var output = new byte[4];
        if (Call(GetModemLinesCode, Array.Empty<byte>(), output) != 4)
            throw new InvalidOperationException(UiLanguage.Text("读取 CTS 状态失败。", "Could not read CTS."));
        return (BitConverter.ToUInt32(output) & CtsOn) != 0;
    }

    public void SetCts(bool enabled)
    {
        RequireModemLinesSupport();
        var output = new byte[4];
        if (Call(GetModemLinesCode, Array.Empty<byte>(), output) != 4)
            throw new InvalidOperationException(UiLanguage.Text("读取线路状态失败。", "Could not read line status."));
        uint lines = BitConverter.ToUInt32(output);
        lines = enabled ? lines | CtsOn : lines & ~CtsOn;
        Call(SetModemLinesCode, BitConverter.GetBytes(lines), Array.Empty<byte>());
    }

    public SimulatorLineState GetLineState()
    {
        RequireLineStateSupport();
        var output = new byte[16];
        if (Call(GetLineStateCode, Array.Empty<byte>(), output) != output.Length)
            throw new InvalidOperationException(UiLanguage.Text("读取业务控制线状态失败。", "Could not read client lines."));

        uint modem = BitConverter.ToUInt32(output, 0);
        uint dtr = BitConverter.ToUInt32(output, 4);
        uint rts = BitConverter.ToUInt32(output, 8);
        uint handshake = BitConverter.ToUInt32(output, 12);
        return new SimulatorLineState(
            (modem & CtsOn) != 0,
            (modem & DsrOn) != 0,
            (modem & DcdOn) != 0,
            dtr != 0,
            rts != 0,
            handshake != 0);
    }

    public void Inject(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return;
        if (bytes.Length > 65535) throw new ArgumentOutOfRangeException(nameof(bytes));
        Call(InjectCode, bytes, Array.Empty<byte>());
    }

    /// <summary>Nonblocking: empty means no business data is queued.</summary>
    public byte[] Receive()
    {
        var buffer = new byte[65535];
        int count = Call(ReceiveCode, Array.Empty<byte>(), buffer);
        return buffer[..count];
    }

    public SerialLineConfig GetSerialConfig()
    {
        RequireSerialConfigSupport();
        int size = Marshal.SizeOf<NativeSerialConfig>();
        var output = new byte[size];
        if (Call(GetSerialConfigCode, Array.Empty<byte>(), output) != size)
            throw new InvalidOperationException(UiLanguage.Text("读取虚拟串口参数失败。", "Could not read port settings."));
        NativeSerialConfig config = MemoryMarshal.Read<NativeSerialConfig>(output);
        return new SerialLineConfig(
            config.BaudRate,
            config.DataBits,
            (SimulatorParity)config.Parity,
            (SimulatorStopBits)config.StopBits,
            ProtocolVersion >= 3 && (config.FlowControl & 1) != 0);
    }

    public void SetSerialConfig(SerialLineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        RequireSerialConfigSupport();
        if (config.LimitToBaudRate && ProtocolVersion < 3)
            throw new InvalidOperationException(UiLanguage.Text("当前驱动版本不支持线路速率限制，请更新驱动。", "Update the driver to limit line speed."));
        var native = new NativeSerialConfig
        {
            BaudRate = config.BaudRate,
            DataBits = config.DataBits,
            Parity = (byte)config.Parity,
            StopBits = (byte)config.StopBits,
            FlowControl = config.LimitToBaudRate ? (byte)1 : (byte)0
        };
        var input = new byte[Marshal.SizeOf<NativeSerialConfig>()];
        MemoryMarshal.Write(input, in native);
        Call(SetSerialConfigCode, input, Array.Empty<byte>());
    }

    private void RequireSerialConfigSupport()
    {
        if (!SupportsSerialConfig)
            throw new InvalidOperationException(UiLanguage.Text("当前虚拟串口驱动版本过旧，请更新驱动以配置串口参数。", "Update the driver to change port settings."));
    }

    private void RequireModemLinesSupport()
    {
        if (!SupportsModemLines)
            throw new InvalidOperationException(UiLanguage.Text("当前驱动版本不支持 CTS 模拟，请更新驱动。", "Update the driver to set CTS."));
    }

    private void RequireLineStateSupport()
    {
        if (!SupportsLineState)
            throw new InvalidOperationException(UiLanguage.Text("当前驱动版本不支持读取 DTR/RTS，请先更新驱动。", "Update the driver to read DTR and RTS."));
    }

    private int Call(uint code, byte[] input, byte[] output)
    {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (!DeviceIoControl(_handle, code, input, input.Length, output, output.Length, out int count, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return count;
        }
    }

    private static uint CtlCode(uint function, uint access) =>
        (0x22u << 16) | (access << 14) | (function << 2);

    public void Dispose() { lock (_gate) _handle.Dispose(); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
