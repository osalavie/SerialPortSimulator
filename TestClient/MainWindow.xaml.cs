using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using SerialPortSimulator.Services;
using Windows.Graphics;
using Windows.UI.Core;

namespace SerialPortSimulator.TestClient;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly object _readGate = new();
    private readonly List<(DateTime Timestamp, string Direction, byte[] Bytes)> _records = new();
    private readonly Dictionary<string, string> _portDisplayToName = new(StringComparer.Ordinal);
    private SerialPort? _port;
    private string? _automationPort;
    private string? _automationLogPath;
    private int _automationSeconds = 5;
    private bool _automationRequested;
    private bool _closing;
    private bool _refreshingPorts;
    private long _rxBytes;
    private long _txBytes;

    private async void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        await UiLanguage.PromptForChangeAsync(((FrameworkElement)sender).XamlRoot);
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = UiLanguage.Get("ClientTitle");
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        TitleBarChrome.Attach(this, TitleBarRoot, TitleBarDragRegion, CaptionInsetColumn);
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _pollTimer.Tick += (_, _) => ReadAvailableBytes();

        ParseAutomationArguments();
        RefreshPorts();
        UpdateControlStates();
        UpdateStats();

        RootGrid.Loaded += RootGrid_Loaded;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        UiLanguage.Changed += UiLanguage_Changed;
    }

    private void UiLanguage_Changed()
    {
        if (_closing) return;
        if (_dispatcherQueue.HasThreadAccess) RefreshLocalizedUi();
        else _dispatcherQueue.TryEnqueue(RefreshLocalizedUi);
    }

    private void RefreshLocalizedUi()
    {
        if (_closing) return;
        UiText.RefreshAll();
        UiText.RefreshSelection(ParityCombo);
        Title = UiLanguage.Get("ClientTitle");
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        RefreshPorts(preserveSelection: true, updateStatus: false);
        UpdateControlStates();
        SetStatus(UiLanguage.Text("界面已切换为中文，语言设置已保存。", "UI language switched to English. Setting saved."));
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        ResizeCompactWindow();
        if (!_automationRequested || _automationPort is null) return;

        PrepareAutomation(_automationPort, _automationLogPath);
        TogglePort();
        await Task.Delay(TimeSpan.FromSeconds(_automationSeconds));
        ClosePort();
        Close();
    }

    private void ResizeCompactWindow()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int width = (int)Math.Round(800 * scale);
        int height = (int)Math.Round(600 * scale);
        DisplayArea? display = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is null)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            return;
        }

        RectInt32 work = display.WorkArea;
        int x = work.X + Math.Max(0, (work.Width - width) / 2);
        int y = work.Y + Math.Max(0, (work.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        UiLanguage.Changed -= UiLanguage_Changed;
        ClosePort();
        _pollTimer.Stop();
    }

    private void ParseAutomationArguments()
    {
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _automationPort = GetOption(arguments, "--port");
        if (_automationPort is null) return;

        _automationRequested = true;
        _automationSeconds = int.TryParse(
            GetOption(arguments, "--seconds"), out int seconds)
            ? Math.Clamp(seconds, 1, 60)
            : 5;
        _automationLogPath = GetOption(arguments, "--log");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private void PortCombo_DropDownOpened(object? sender, object e) =>
        RefreshPorts(preserveSelection: true, updateStatus: false);

    private void PortCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPorts) return;
        if (PortCombo.SelectedItem is string displayName)
            SetStatus(UiLanguage.Text($"已选择 {displayName}，设置参数后打开端口。", $"Selected {displayName}. Set the options, then open the port."));
    }

    private void MainWindow_Activated(
        object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState ==
            Microsoft.UI.Xaml.WindowActivationState.Deactivated) return;
        if (_port is { IsOpen: true }) return;
        RefreshPorts(preserveSelection: true, updateStatus: false);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) =>
        TogglePort();

    private void SendButton_Click(object sender, RoutedEventArgs e) =>
        SendText();

    private void SendBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CoreVirtualKeyStates control = InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if ((control & CoreVirtualKeyStates.Down) == 0) return;
        e.Handled = true;
        SendText();
    }

    private void DisplayOption_Changed(object sender, RoutedEventArgs e)
    {
        if (LogBox is not null) RenderRecords();
    }

    private void RtsCtsCheck_Changed(object sender, RoutedEventArgs e) =>
        UpdateControlStates();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _records.Clear();
        _rxBytes = 0;
        _txBytes = 0;
        LogBox.Text = string.Empty;
        UpdateStats();
        SetStatus(UiLanguage.Text("记录已清空", "Log cleared"));
    }

    private void RefreshPorts(bool preserveSelection = true, bool updateStatus = true)
    {
        _refreshingPorts = true;
        try
        {
            string? previousPort = preserveSelection ? GetSelectedPortName() : null;
            Dictionary<string, string> deviceNames = EnumerateSerialDeviceNames();
            var items = SerialPort.GetPortNames()
                .OrderBy(name => int.TryParse(name.AsSpan(3), out int number)
                    ? number
                    : int.MaxValue)
                .Select(portName => new
                {
                    PortName = portName,
                    DisplayName = $"{portName} · " +
                        (deviceNames.TryGetValue(portName, out string? deviceName)
                            ? deviceName
                            : UiLanguage.Text("串口设备", "Serial port"))
                })
                .ToArray();

            _portDisplayToName.Clear();
            PortCombo.Items.Clear();
            foreach (var item in items)
            {
                _portDisplayToName[item.DisplayName] = item.PortName;
                PortCombo.Items.Add(item.DisplayName);
            }

            PortCombo.SelectedItem = items.FirstOrDefault(item =>
                    item.PortName.Equals(
                        previousPort, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? items.FirstOrDefault()?.DisplayName;
        }
        finally
        {
            _refreshingPorts = false;
        }

        if (updateStatus)
            SetStatus(PortCombo.SelectedItem is string displayName
                ? UiLanguage.Text($"已选择 {displayName}，设置参数后打开端口。", $"Selected {displayName}. Set the options, then open the port.")
                : UiLanguage.Text("未发现串口，请先在模拟器中新建设备。", "No ports found. Add a device in the simulator first."));
    }

    private string? GetSelectedPortName() =>
        PortCombo.SelectedItem is string displayName &&
        _portDisplayToName.TryGetValue(displayName, out string? portName)
            ? portName
            : null;

    private void TogglePort()
    {
        if (_port is { IsOpen: true })
        {
            ClosePort();
            return;
        }

        RefreshPorts(preserveSelection: true, updateStatus: false);
        string? portName = GetSelectedPortName();
        if (portName is null)
        {
            SetStatus(UiLanguage.Text("未发现可用串口", "No ports available"), true);
            return;
        }

        if (!TryGetSerialSettings(
                out int baudRate,
                out int dataBits,
                out Parity parity,
                out StopBits stopBits,
                out string? settingsError))
        {
            SetStatus(settingsError!, true);
            return;
        }

        try
        {
            var port = new SerialPort(
                portName, baudRate, parity, dataBits, stopBits);
            port.Encoding = Encoding.UTF8;
            bool rtsCts = RtsCtsCheck.IsChecked == true;
            port.Handshake = rtsCts
                ? Handshake.RequestToSend
                : Handshake.None;
            port.DtrEnable = DtrCheck.IsChecked == true;
            if (!rtsCts) port.RtsEnable = RtsCheck.IsChecked == true;
            port.ReadTimeout = 1000;
            port.WriteTimeout = 1000;
            _port = port;
            port.Open();
            _pollTimer.Start();
            UpdateControlStates();
            SetStatus(
                UiLanguage.Text($"已打开 {portName} · ", $"Opened {portName} · ") +
                $"{baudRate} {dataBits}{ParityText(parity)}{StopText(stopBits)}");
        }
        catch (Exception ex)
        {
            _port?.Dispose();
            _port = null;
            _pollTimer.Stop();
            UpdateControlStates();
            string hresult = $"0x{ex.HResult:X8}";
            LogDiagnostic($"OPEN FAILED {hresult}: {ex}");
            SetStatus(
                IsAccessDenied(ex)
                    ? UiLanguage.Text("打开失败：需要管理员权限或重启后生效", "Could not open port. Admin access or a restart may be needed.")
                    : UiLanguage.Text($"打开失败：{ex.Message}", $"Could not open port: {ex.Message}"),
                true);
        }
    }

    private bool TryGetSerialSettings(
        out int baudRate,
        out int dataBits,
        out Parity parity,
        out StopBits stopBits,
        out string? error)
    {
        baudRate = 0;
        dataBits = 0;
        parity = Parity.None;
        stopBits = StopBits.One;
        error = null;

        string baudText = BaudRateCombo.Text?.Trim() ?? string.Empty;
        if (baudText.Length == 0)
            baudText = BaudRateCombo.SelectedItem as string ?? string.Empty;
        if (!int.TryParse(baudText, out baudRate) || baudRate <= 0)
        {
            error = UiLanguage.Text("请输入有效的波特率。", "Enter a valid baud rate.");
            return false;
        }

        dataBits = DataBitsCombo.SelectedIndex switch
        {
            0 => 5,
            1 => 6,
            2 => 7,
            3 => 8,
            _ => 0
        };
        if (dataBits == 0)
        {
            error = UiLanguage.Text("请选择数据位。", "Select the data bits.");
            return false;
        }

        if (ParityCombo.SelectedIndex is < 0 or > 4)
        {
            error = UiLanguage.Text("请选择校验位。", "Select parity.");
            return false;
        }
        parity = (Parity)ParityCombo.SelectedIndex;

        stopBits = StopBitsCombo.SelectedIndex switch
        {
            0 => StopBits.One,
            1 => StopBits.OnePointFive,
            2 => StopBits.Two,
            _ => (StopBits)(-1)
        };
        if ((int)stopBits < 0)
        {
            error = UiLanguage.Text("请选择停止位。", "Select the stop bits.");
            return false;
        }
        if (stopBits == StopBits.OnePointFive && dataBits != 5)
        {
            error = UiLanguage.Text("1.5 停止位只适用于 5 数据位。", "Use 1.5 stop bits with 5 data bits only.");
            return false;
        }
        return true;
    }

    private void SendText()
    {
        if (_port is not { IsOpen: true })
        {
            SetStatus(UiLanguage.Text("端口尚未打开", "Open the port first"), true);
            return;
        }

        if (SendBox.Text.Length == 0 && NewLineCheck.IsChecked != true)
        {
            SetStatus(UiLanguage.Text("请输入要发送的内容", "Enter a message to send"), true);
            return;
        }

        try
        {
            string text = SendBox.Text +
                (NewLineCheck.IsChecked == true ? "\r\n" : string.Empty);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _port.Write(bytes, 0, bytes.Length);
            AppendRecord("TX", bytes);
            SetStatus(UiLanguage.Text("发送成功", "Sent"));
            SendBox.Focus(FocusState.Programmatic);
            SendBox.SelectAll();
        }
        catch (Exception ex)
        {
            LogDiagnostic($"WRITE FAILED: {ex}");
            SetStatus(UiLanguage.Text($"发送失败：{ex.Message}", $"Send failed: {ex.Message}"), true);
        }
    }

    private void ReadAvailableBytes()
    {
        try
        {
            byte[]? received = null;
            lock (_readGate)
            {
                if (_port is not { IsOpen: true }) return;
                int count = _port.BytesToRead;
                if (count <= 0) return;
                var bytes = new byte[count];
                int read = _port.Read(bytes, 0, bytes.Length);
                if (read > 0) received = bytes[..read];
            }

            if (received is not null) AppendRecord("RX", received);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"READ FAILED: {ex}");
        }
    }

    private void AppendRecord(string direction, byte[] bytes)
    {
        var record = (Timestamp: DateTime.Now, Direction: direction, Bytes: bytes);
        _records.Insert(0, record);
        if (_records.Count > 200) _records.RemoveAt(200);
        if (direction == "TX") _txBytes += bytes.Length;
        else if (direction == "RX") _rxBytes += bytes.Length;

        RenderRecords();
        UpdateStats();
        WriteAutomationLog(FormatRecord(record, showTimestamp: true, hex: false));
    }

    private void RenderRecords()
    {
        bool showTimestamp = TimestampCheck.IsChecked == true;
        bool hex = HexCheck.IsChecked == true;
        LogBox.Text = string.Join('\n', _records.Select(record =>
            FormatRecord(record, showTimestamp, hex)));
        LogBox.Select(0, 0);
    }

    private static string FormatRecord(
        (DateTime Timestamp, string Direction, byte[] Bytes) record,
        bool showTimestamp,
        bool hex)
    {
        string data = hex
            ? BitConverter.ToString(record.Bytes).Replace('-', ' ')
            : Escape(Encoding.UTF8.GetString(record.Bytes));
        string prefix = showTimestamp
            ? $"[{record.Timestamp:HH:mm:ss.fff}] "
            : string.Empty;
        return $"{prefix}{record.Direction} [{record.Bytes.Length} B] {data}";
    }

    private void ClosePort()
    {
        _pollTimer.Stop();
        SerialPort? port = _port;
        _port = null;
        if (port is not null)
        {
            try { if (port.IsOpen) port.Close(); }
            catch { }
            port.Dispose();
        }

        if (!_closing)
        {
            UpdateControlStates();
            SetStatus(UiLanguage.Text("端口已关闭", "Port closed"));
        }
    }

    private void UpdateControlStates()
    {
        bool connected = _port is { IsOpen: true };
        PortCombo.IsEnabled = !connected;
        OpenButton.Content = connected ? UiLanguage.Text("关闭端口", "Close port") : UiLanguage.Text("打开端口", "Open port");
        SendButton.IsEnabled = connected;
        RtsCtsCheck.IsEnabled = !connected;
        DtrCheck.IsEnabled = !connected;
        RtsCheck.IsEnabled = !connected && RtsCtsCheck.IsChecked != true;
    }

    private void UpdateStats()
    {
        StatsText.Text =
            $"RX {FormatBytes(_rxBytes)}  ·  TX {FormatBytes(_txBytes)}";
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = error ? UiLanguage.Text($"错误：{message}", $"Error: {message}") : message;
    }

    private void PrepareAutomation(string portName, string? logPath)
    {
        _automationLogPath = logPath;
        string? displayName = _portDisplayToName
            .Where(pair => pair.Value.Equals(
                portName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (displayName is null)
        {
            displayName = UiLanguage.Text($"{portName} · 串口设备", $"{portName} · Serial port");
            _portDisplayToName[displayName] = portName;
            PortCombo.Items.Add(displayName);
        }
        PortCombo.SelectedItem = displayName;
    }

    private void LogDiagnostic(string text) =>
        WriteAutomationLog($"[{DateTime.Now:HH:mm:ss.fff}] {text}");

    private static Dictionary<string, string> EnumerateSerialDeviceNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        [
            @"SYSTEM\CurrentControlSet\Enum\ROOT\PORTS",
            @"SYSTEM\CurrentControlSet\Enum\USB",
            @"SYSTEM\CurrentControlSet\Enum\FTDIBUS",
            @"SYSTEM\CurrentControlSet\Enum\ACPI",
            @"SYSTEM\CurrentControlSet\Enum\SERENUM",
            @"SYSTEM\CurrentControlSet\Enum\BTHENUM"
        ];

        foreach (string rootPath in roots)
        {
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(rootPath);
                if (root is null) continue;
                foreach (string childName in root.GetSubKeyNames())
                {
                    using RegistryKey? child = root.OpenSubKey(childName);
                    if (child is null) continue;
                    AddPortMapping(result, child);
                    foreach (string nestedName in child.GetSubKeyNames())
                    {
                        using RegistryKey? nested = child.OpenSubKey(nestedName);
                        if (nested is not null) AddPortMapping(result, nested);
                    }
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or
                System.Security.SecurityException or
                IOException)
            {
                // Device metadata is optional; keep the port list usable.
            }
        }

        return result;
    }

    private static void AddPortMapping(
        Dictionary<string, string> result, RegistryKey deviceKey)
    {
        using RegistryKey? parameters = deviceKey.OpenSubKey("Device Parameters");
        if (parameters?.GetValue("PortName") is not string portName ||
            string.IsNullOrWhiteSpace(portName))
            return;

        string? friendlyName = deviceKey.GetValue("FriendlyName") as string;
        string? description = deviceKey.GetValue("DeviceDesc") as string;
        string name = !string.IsNullOrWhiteSpace(friendlyName)
            ? friendlyName
            : !string.IsNullOrWhiteSpace(description) && !description.StartsWith('@')
                ? description
                : UiLanguage.Text("串口设备", "Serial port");
        string stripped = StripPortSuffix(name, portName);
        result[portName] = stripped.Equals(portName, StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Text("串口设备", "Serial port")
            : stripped;
    }

    private static string StripPortSuffix(string name, string portName)
    {
        string trimmed = name.Trim();
        string suffix = $" ({portName})";
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length].Trim()
            : trimmed;
    }

    private void WriteAutomationLog(string line)
    {
        try
        {
            if (_automationLogPath is { } path)
                BoundedLogFile.Append(path, line);
            else
                BoundedLogFile.AppendPortable("test-client.log", line);
        }
        catch
        {
            // Interactive testing remains authoritative.
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MiB"
    };

    private static string Escape(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool IsAccessDenied(Exception exception) =>
        exception is System.ComponentModel.Win32Exception { NativeErrorCode: 5 } ||
        exception.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 };

    private static string ParityText(Parity parity) => parity switch
    {
        Parity.None => "N",
        Parity.Odd => "O",
        Parity.Even => "E",
        Parity.Mark => "M",
        _ => "S"
    };

    private static string StopText(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1"
    };
}
