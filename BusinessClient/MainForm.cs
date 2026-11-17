using System.IO.Ports;
using System.Text;
using SerialPortSimulator.Services;

namespace SerialPortSimulator.BusinessTest;

internal sealed class MainForm : Form
{
    private readonly ComboBox _portCombo = new();
    private readonly Button _refreshButton = new();
    private readonly Button _openButton = new();
    private readonly Label _configLabel = new();
    private readonly TextBox _receiveBox = new();
    private readonly TextBox _sendBox = new();
    private readonly CheckBox _newLineCheck = new();
    private readonly CheckBox _hexCheck = new();
    private readonly CheckBox _timestampCheck = new();
    private readonly CheckBox _dtrCheck = new();
    private readonly CheckBox _rtsCheck = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 50 };
    private readonly object _readGate = new();
    private readonly List<(DateTime Timestamp, string Direction, byte[] Bytes)> _records = new();
    private string? _automationLogPath;
    private SerialPort? _port;

    public MainForm()
    {
        Text = "串口模拟器业务端测试";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(900, 680);
        BuildLayout();
        RefreshPorts();
        _pollTimer.Tick += (_, _) => ReadAvailableBytes();
        FormClosing += (_, _) => ClosePort();
    }

    public void PrepareAutomation(string portName, string? logPath)
    {
        _automationLogPath = logPath;
        if (!_portCombo.Items.Contains(portName)) _portCombo.Items.Add(portName);
        _portCombo.SelectedItem = portName;
        LoadDeviceConfig();
    }

    public void OpenPortForAutomation() => TogglePort();
    public void ClosePortForAutomation() => ClosePort();

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var deviceBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 5,
            Margin = new Padding(0, 0, 0, 10)
        };
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        deviceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));

        deviceBar.Controls.Add(new Label { Text = "串口", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _portCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _portCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        deviceBar.Controls.Add(_portCombo, 1, 0);
        _refreshButton.Text = "刷新";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshPorts();
        deviceBar.Controls.Add(_refreshButton, 2, 0);
        _openButton.Text = "打开端口";
        _openButton.AutoSize = true;
        _openButton.Click += (_, _) => TogglePort();
        deviceBar.Controls.Add(_openButton, 3, 0);
        deviceBar.Controls.Add(_configLabel, 4, 0);
        root.Controls.Add(deviceBar, 0, 0);

        _receiveBox.Dock = DockStyle.Fill;
        _receiveBox.Multiline = true;
        _receiveBox.ReadOnly = true;
        _receiveBox.ScrollBars = ScrollBars.Both;
        _receiveBox.WordWrap = false;
        _receiveBox.Font = new Font("Consolas", 10F);
        root.Controls.Add(_receiveBox, 0, 1);

        var sendBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 6,
            Margin = new Padding(0, 10, 0, 10)
        };
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sendBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sendBox.Dock = DockStyle.Fill;
        _sendBox.PlaceholderText = "输入要发送到模拟器的内容";
        sendBar.Controls.Add(_sendBox, 0, 0);

        var sendButton = new Button { Text = "发送", AutoSize = true };
        sendButton.Click += (_, _) => SendText();
        sendBar.Controls.Add(sendButton, 1, 0);
        _newLineCheck.Text = "追加 CRLF";
        _newLineCheck.AutoSize = true;
        _newLineCheck.Checked = true;
        sendBar.Controls.Add(_newLineCheck, 2, 0);
        _hexCheck.Text = "十六进制显示";
        _hexCheck.AutoSize = true;
        _hexCheck.CheckedChanged += (_, _) => RenderRecords();
        sendBar.Controls.Add(_hexCheck, 3, 0);
        _timestampCheck.Text = "显示时间戳";
        _timestampCheck.AutoSize = true;
        _timestampCheck.Checked = true;
        _timestampCheck.CheckedChanged += (_, _) => RenderRecords();
        sendBar.Controls.Add(_timestampCheck, 4, 0);
        var clearButton = new Button { Text = "清空" };
        clearButton.Click += (_, _) =>
        {
            _records.Clear();
            _receiveBox.Clear();
        };
        sendBar.Controls.Add(clearButton, 5, 0);
        root.Controls.Add(sendBar, 0, 2);

        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false
        };
        _dtrCheck.Text = "DTR";
        _dtrCheck.AutoSize = true;
        statusBar.Controls.Add(_dtrCheck);
        _rtsCheck.Text = "RTS";
        _rtsCheck.AutoSize = true;
        statusBar.Controls.Add(_rtsCheck);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(16, 7, 0, 0);
        statusBar.Controls.Add(_statusLabel);
        root.Controls.Add(statusBar, 0, 3);

        Controls.Add(root);
        _portCombo.SelectedIndexChanged += (_, _) => LoadDeviceConfig();
        _receiveBox.BackColor = Color.White;
        _receiveBox.ForeColor = Color.Black;
    }

    private void RefreshPorts()
    {
        string? previous = _portCombo.SelectedItem as string;
        _portCombo.Items.Clear();
        foreach (string portName in SerialPort.GetPortNames()
                     .OrderBy(name => int.TryParse(name.AsSpan(3), out int number) ? number : int.MaxValue))
            _portCombo.Items.Add(portName);
        _portCombo.SelectedItem = previous is not null && _portCombo.Items.Contains(previous)
            ? previous
            : _portCombo.Items.Cast<string>().FirstOrDefault();
        LoadDeviceConfig();
    }

    private void LoadDeviceConfig()
    {
        if (_portCombo.SelectedItem is not string portName)
        {
            _configLabel.Text = "未发现串口";
            return;
        }

        try
        {
            VirtualDeviceInfo? device = VirtualDeviceClient.EnumerateDevices()
                .FirstOrDefault(info => info.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                _configLabel.Text = "非模拟器串口";
                return;
            }

            using var client = new VirtualDeviceClient(device.InterfacePath);
            SerialLineConfig config = client.GetSerialConfig();
            _configLabel.Text =
                $"模拟器参数：{config.BaudRate} {config.DataBits}{ParityText(config.Parity)}{StopText(config.StopBits)}";
        }
        catch (Exception ex)
        {
            _configLabel.Text = $"参数读取失败：{ex.Message}";
        }
    }

    private void TogglePort()
    {
        if (_port is { IsOpen: true })
        {
            ClosePort();
            return;
        }

        if (_portCombo.SelectedItem is not string portName)
        {
            SetStatus("请选择串口", true);
            return;
        }

        try
        {
            VirtualDeviceInfo? device = VirtualDeviceClient.EnumerateDevices()
                .FirstOrDefault(info => info.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            SerialLineConfig? config = null;
            if (device is not null)
            {
                using var client = new VirtualDeviceClient(device.InterfacePath);
                config = client.GetSerialConfig();
            }

            var port = config is null
                ? new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                : new SerialPort(
                    portName,
                    checked((int)config.BaudRate),
                    (Parity)(int)config.Parity,
                    config.DataBits,
                    config.StopBits switch
                    {
                        SimulatorStopBits.OnePointFive => StopBits.OnePointFive,
                        SimulatorStopBits.Two => StopBits.Two,
                        _ => StopBits.One
                    });
            port.Encoding = Encoding.UTF8;
            port.Handshake = Handshake.None;
            port.DtrEnable = _dtrCheck.Checked;
            port.RtsEnable = _rtsCheck.Checked;
            port.ReadTimeout = 1000;
            port.WriteTimeout = 1000;
            port.DataReceived += Port_DataReceived;
            _port = port;
            port.Open();
            _pollTimer.Start();
            _openButton.Text = "关闭端口";
            _portCombo.Enabled = false;
            _refreshButton.Enabled = false;
            SetStatus($"已打开 {portName}");
        }
        catch (Exception ex)
        {
            _port?.Dispose();
            _port = null;
            _openButton.Text = "打开端口";
            _portCombo.Enabled = true;
            _refreshButton.Enabled = true;
            string hresult = $"0x{ex.HResult:X8}";
            LogDiagnostic($"OPEN FAILED {hresult}: {ex}");
            SetStatus($"打开失败：{ex.Message}", true);
        }
    }

    private void Port_DataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        ReadAvailableBytes();
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
            if (received is null) return;
            if (IsDisposed) return;
            if (InvokeRequired)
                BeginInvoke((Action)(() => AppendReceived(received)));
            else
                AppendReceived(received);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                BeginInvoke((Action)(() => LogDiagnostic($"READ FAILED: {ex}")));
        }
    }

    private void AppendReceived(byte[] bytes)
    {
        AppendRecord("RX", bytes);
    }

    private void SendText()
    {
        if (_port is not { IsOpen: true })
        {
            SetStatus("端口尚未打开", true);
            return;
        }

        try
        {
            string text = _sendBox.Text + (_newLineCheck.Checked ? "\r\n" : string.Empty);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _port.Write(bytes, 0, bytes.Length);
            AppendRecord("TX", bytes);
            SetStatus("发送成功");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"WRITE FAILED: {ex}");
            SetStatus($"发送失败：{ex.Message}", true);
        }
    }

    private void ClosePort()
    {
        _pollTimer.Stop();
        SerialPort? port = _port;
        _port = null;
        if (port is not null)
        {
            port.DataReceived -= Port_DataReceived;
            try { if (port.IsOpen) port.Close(); }
            catch { }
            port.Dispose();
        }
        _openButton.Text = "打开端口";
        _portCombo.Enabled = true;
        _refreshButton.Enabled = true;
        SetStatus("端口已关闭");
    }

    private void AppendRecord(string direction, byte[] bytes)
    {
        var record = (Timestamp: DateTime.Now, Direction: direction, Bytes: bytes);
        _records.Insert(0, record);
        if (_records.Count > 200) _records.RemoveAt(200);
        RenderRecords();
        WriteAutomationLog(FormatRecord(record, showTimestamp: true, hex: false));
    }

    private void RenderRecords()
    {
        _receiveBox.Text = string.Join(Environment.NewLine, _records.Select(record =>
            FormatRecord(record, _timestampCheck.Checked, _hexCheck.Checked)));
        _receiveBox.Select(0, 0);
        _receiveBox.ScrollToCaret();
    }

    private static string FormatRecord(
        (DateTime Timestamp, string Direction, byte[] Bytes) record,
        bool showTimestamp,
        bool hex)
    {
        string data = hex
            ? BitConverter.ToString(record.Bytes).Replace('-', ' ')
            : Escape(Encoding.UTF8.GetString(record.Bytes));
        string prefix = showTimestamp ? $"[{record.Timestamp:HH:mm:ss.fff}] " : string.Empty;
        return $"{prefix}{record.Direction} [{record.Bytes.Length} B] {data}";
    }

    private void LogDiagnostic(string text) =>
        WriteAutomationLog($"[{DateTime.Now:HH:mm:ss.fff}] {text}");

    private void WriteAutomationLog(string line)
    {
        try
        {
            if (_automationLogPath is { } path)
                BoundedLogFile.Append(path, line);
            else
                BoundedLogFile.AppendPortable("business-test.log", line);
        }
        catch
        {
            // The interactive receive display remains authoritative.
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        _statusLabel.Text = error ? $"错误：{message}" : message;
        _statusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
    }

    private static string Escape(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string ParityText(SimulatorParity parity) => parity switch
    {
        SimulatorParity.None => "N",
        SimulatorParity.Odd => "O",
        SimulatorParity.Even => "E",
        SimulatorParity.Mark => "M",
        _ => "S"
    };

    private static string StopText(SimulatorStopBits stopBits) => stopBits switch
    {
        SimulatorStopBits.One => "1",
        SimulatorStopBits.OnePointFive => "1.5",
        _ => "2"
    };
}
