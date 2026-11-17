using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialPortSimulator.Services;

namespace SerialPortSimulator;

public sealed partial class MainWindow : Window
{

    private readonly DataGenerator _generator = new();
    private readonly List<(DateTime Timestamp, string Data)> _dataRecords = new();
    private VirtualDeviceSession? _session;
    private CancellationTokenSource? _sendCancellation;
    private Task? _sendLoop;
    private bool _sendingOnce;
    private bool _deviceManagementBusy;
    private uint _selectedProtocolVersion;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 900));
        Closed += MainWindow_Closed;
        RefreshDevices();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private async void CreateDeviceButton_Click(object sender, RoutedEventArgs e) =>
        await RunDeviceManagerAsync("Create");

    private async void UpdateDriverButton_Click(object sender, RoutedEventArgs e) =>
        await RunDeviceManagerAsync("Update");

    private async void RemoveDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceComboItem selected) return;

        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = "删除虚拟设备",
            Content = $"将删除 {selected.Device.PortName}（{selected.Device.InstanceId}）。业务端随后无法再打开该端口；如果这是最后一个本产品设备，还会卸载驱动包并移除测试证书。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await RunDeviceManagerAsync("Remove", selected.Device.InstanceId);
    }

    private async Task RunDeviceManagerAsync(string action, string? instanceId = null)
    {
        string? helperPath = FindDeviceManagerExecutable();
        if (helperPath is null)
        {
            SetStatus(
                "找不到 SerialPortSimulator.DeviceManager.exe。请确认整个 ZIP 已解压，" +
                "且管理员程序位于主程序目录下的 DeviceManager 文件夹。",
                true);
            return;
        }

        string applicationDirectory = GetApplicationDirectory();
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = applicationDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(action.ToLowerInvariant());
        if (instanceId is not null)
        {
            startInfo.ArgumentList.Add("--instance-id");
            startInfo.ArgumentList.Add(instanceId);
        }
        startInfo.ArgumentList.Add("--app-root");
        startInfo.ArgumentList.Add(applicationDirectory);

        _deviceManagementBusy = true;
        UpdateControls();
        try
        {
            SetStatus(GetDeviceManagerProgress(action));
            using Process? process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException("无法启动管理员进程。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                if (action == "Remove") RefreshDevices();
                throw new InvalidOperationException(GetDeviceManagerError(process.ExitCode));
            }
            RefreshDevices();
            if (action != "Remove" && DeviceCombo.Items.Count == 0)
                SetStatus("驱动操作已完成，但设备控制接口尚未加载。请在设备管理器查看本产品虚拟串口的设备状态。", true);
            else
                SetStatus(GetDeviceManagerSuccess(action));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("管理员授权已取消");
        }
        catch (Exception ex)
        {
            SetStatus($"设备管理失败：{ex.Message}", true);
        }
        finally
        {
            _deviceManagementBusy = false;
            UpdateControls();
        }
    }

    private static string GetDeviceManagerProgress(string action) => action switch
    {
        "Create" => "等待管理员创建虚拟设备…",
        "Update" => "等待管理员更新虚拟串口驱动…",
        _ => "等待管理员删除虚拟设备…"
    };

    private static string GetDeviceManagerSuccess(string action) => action switch
    {
        "Create" => "虚拟设备已创建",
        "Update" => "虚拟串口驱动已更新",
        _ => "虚拟设备已删除"
    };

    private static string GetDeviceManagerError(int exitCode) => exitCode switch
    {
        10 => "设备管理参数无效。",
        11 => "驱动包不完整，请检查 DriverPackage 和 tools/devcon.exe。",
        12 => "测试证书导入失败。",
        13 => "创建虚拟设备失败。",
        14 => "安装虚拟串口驱动失败。",
        15 => "找不到指定虚拟设备。",
        16 => "拒绝删除非本产品的设备。",
        17 => "删除虚拟设备失败。",
        18 => "虚拟设备已删除，但驱动包或测试证书清理失败。",
        _ => $"设备管理程序异常退出（{exitCode}）。"
    } + " 详情请查看 C:\\ProgramData\\SerialPortSimulator\\device-manager.log。";

    private static string? FindDeviceManagerExecutable()
    {
        string applicationDirectory = GetApplicationDirectory();
        foreach (string directory in new[]
                 {
                     applicationDirectory,
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(directory, "SerialPortSimulator.DeviceManager.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            string packaged = Path.Combine(directory, "DeviceManager", "SerialPortSimulator.DeviceManager.exe");
            if (File.Exists(packaged)) return Path.GetFullPath(packaged);
        }

        string development = Path.GetFullPath(Path.Combine(
            applicationDirectory,
            "..", "..", "..", "..",
            "DeviceManager", "bin", "Debug",
            "net10.0-windows10.0.19041.0",
            "SerialPortSimulator.DeviceManager.exe"));
        return File.Exists(development) ? development : null;
    }

    private static string GetApplicationDirectory()
    {
        string? processPath = Environment.ProcessPath;
        string? directory = processPath is null ? null : Path.GetDirectoryName(processPath);
        return !string.IsNullOrEmpty(directory)
            ? Path.GetFullPath(directory)
            : Path.GetFullPath(AppContext.BaseDirectory);
    }

    private void RefreshDevices()
    {
        string? previous = (DeviceCombo.SelectedItem as DeviceComboItem)?.Device.InstanceId;
        DeviceCombo.Items.Clear();

        try
        {
            foreach (VirtualDeviceInfo device in VirtualDeviceClient.EnumerateDevices())
                DeviceCombo.Items.Add(new DeviceComboItem(device));
        }
        catch (Exception ex)
        {
            SetStatus($"枚举虚拟设备失败：{ex.Message}", true);
        }

        DeviceCombo.SelectedItem = DeviceCombo.Items
            .OfType<DeviceComboItem>()
            .FirstOrDefault(item => item.Device.InstanceId == previous) ??
            DeviceCombo.Items.FirstOrDefault();

        if (DeviceCombo.Items.Count == 0)
            SetStatus("未发现可用的模拟串口控制接口。若设备管理器中已有本产品端口，请检查其设备状态；启动失败时需更新驱动。", true);
        else if (_session is null)
            SetStatus("请选择虚拟设备并打开控制通道。");

        UpdateSelectedDeviceDetails();
        UpdateControls();
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortText is null) return;
        UpdateSelectedDeviceDetails();
        LoadSelectedSerialConfig();
    }

    private void UpdateSelectedDeviceDetails()
    {
        if (DeviceCombo.SelectedItem is not DeviceComboItem item)
        {
            PortText.Text = "未选择";
            DeviceIdText.Text = "未选择";
            return;
        }

        PortText.Text = item.Device.PortName;
        DeviceIdText.Text = item.Device.InstanceId;
        ProtocolText.Text = "读取中…";
    }

    private void LoadSelectedSerialConfig()
    {
        if (SerialParameterPanel is null) return;
        _selectedProtocolVersion = 0;
        if (DeviceCombo.SelectedItem is not DeviceComboItem selected)
        {
            SetSerialParameterControlsEnabled(false);
            SerialParameterStatusText.Text = "选择设备后可读取并设置模拟串口参数。";
            UpdateControls();
            return;
        }

        try
        {
            uint version;
            bool supportsConfig;
            SerialLineConfig? config = null;

            if (_session is { } session)
            {
                version = session.ProtocolVersion;
                supportsConfig = session.SupportsSerialConfig;
                if (supportsConfig) config = session.GetSerialConfig();
            }
            else
            {
                using var client = new VirtualDeviceClient(selected.Device.InterfacePath);
                version = client.ProtocolVersion;
                supportsConfig = client.SupportsSerialConfig;
                if (supportsConfig) config = client.GetSerialConfig();
            }

            _selectedProtocolVersion = version;
            ProtocolText.Text = $"版本 {version}";
            LimitToBaudRateCheck.IsEnabled = version >= 3;
            CtsOnCheck.IsEnabled = version >= 3;
            ApplyCtsButton.IsEnabled = version >= 3;
            if (version >= 3)
            {
                CtsOnCheck.IsChecked = _session is { } activeSession
                    ? activeSession.GetCts()
                    : GetSelectedCts(selected.Device.InterfacePath);
            }
            if (config is not null)
            {
                SetSerialConfigControls(config);
                SerialParameterStatusText.Text = version >= 3
                    ? "当前参数由模拟器设备提供；可开启线路限速并设置 CTS。"
                    : "已安装驱动仅支持基本串口参数；线路限速和 CTS 需要协议版本 3。请使用新版程序点击“更新驱动”。";
            }
            else
            {
                SerialParameterStatusText.Text = "当前驱动协议不支持模拟器参数配置，请点击“更新驱动”。";
            }
        }
        catch (Exception ex)
        {
            ProtocolText.Text = "读取失败";
            SerialParameterStatusText.Text = $"读取串口参数失败：{ex.Message}";
        }

        UpdateControls();
    }

    private void SetSerialParameterControlsEnabled(bool enabled)
    {
        BaudRateCombo.IsEnabled = enabled;
        DataBitsCombo.IsEnabled = enabled;
        ParityCombo.IsEnabled = enabled;
        StopBitsCombo.IsEnabled = enabled;
        ApplySerialParametersButton.IsEnabled = enabled;
        LimitToBaudRateCheck.IsEnabled = enabled && _selectedProtocolVersion >= 3;
        CtsOnCheck.IsEnabled = enabled && _selectedProtocolVersion >= 3;
        ApplyCtsButton.IsEnabled = enabled && _selectedProtocolVersion >= 3;
    }

    private void SetSerialConfigControls(SerialLineConfig config)
    {
        BaudRateCombo.Text = config.BaudRate.ToString();
        DataBitsCombo.SelectedIndex = config.DataBits - 5;
        ParityCombo.SelectedIndex = (int)config.Parity;
        StopBitsCombo.SelectedIndex = (int)config.StopBits;
        LimitToBaudRateCheck.IsChecked = config.LimitToBaudRate;
    }

    private void ApplySerialParametersButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceComboItem selected)
        {
            SetStatus("请先选择虚拟设备。", true);
            return;
        }

        try
        {
            if (!uint.TryParse(BaudRateCombo.Text, out uint baudRate))
                throw new ArgumentException("波特率必须为正整数。");
            byte dataBits = byte.Parse((string)DataBitsCombo.SelectedItem);
            var parity = (SimulatorParity)ParityCombo.SelectedIndex;
            var stopBits = (SimulatorStopBits)StopBitsCombo.SelectedIndex;
            var config = new SerialLineConfig(baudRate, dataBits, parity, stopBits,
                LimitToBaudRateCheck.IsChecked == true);
            config.Validate();

            if (_session is { } session)
            {
                session.SetSerialConfig(config);
            }
            else
            {
                using var client = new VirtualDeviceClient(selected.Device.InterfacePath);
                client.SetSerialConfig(config);
            }

            SerialParameterStatusText.Text =
                $"已应用 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}；线路限速{(config.LimitToBaudRate ? "开启" : "关闭")}。";
            AppendLog($"设备参数 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}");
            SetStatus("虚拟串口参数已更新");
        }
        catch (Exception ex)
        {
            SetStatus($"应用串口参数失败：{ex.Message}", true);
        }
    }

    private static bool GetSelectedCts(string interfacePath)
    {
        using var client = new VirtualDeviceClient(interfacePath);
        return client.GetCts();
    }

    private void ApplyCtsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceComboItem selected) return;
        try
        {
            bool enabled = CtsOnCheck.IsChecked == true;
            if (_session is { } session) session.SetCts(enabled);
            else
            {
                using var client = new VirtualDeviceClient(selected.Device.InterfacePath);
                client.SetCts(enabled);
            }
            AppendLog($"CTS {(enabled ? "有效" : "无效")}");
            SetStatus($"CTS 已设为{(enabled ? "有效" : "无效")}");
        }
        catch (Exception ex)
        {
            SetStatus($"设置 CTS 失败：{ex.Message}", true);
        }
    }

    private static string ParityText(SimulatorParity parity) => parity switch
    {
        SimulatorParity.None => "N",
        SimulatorParity.Odd => "O",
        SimulatorParity.Even => "E",
        SimulatorParity.Mark => "M",
        _ => "S"
    };

    private static string StopBitsText(SimulatorStopBits stopBits) => stopBits switch
    {
        SimulatorStopBits.One => "1",
        SimulatorStopBits.OnePointFive => "1.5",
        _ => "2"
    };

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            await StopSendingAsync();
            CloseSession();
            SetStatus("控制通道已关闭");
            UpdateControls();
            return;
        }

        if (DeviceCombo.SelectedItem is not DeviceComboItem selected)
        {
            SetStatus("请先选择虚拟设备。", true);
            return;
        }

        if (!TryParseRules(RuleBox.Text, out var rules, out string? ruleError))
        {
            SetStatus(ruleError!, true);
            return;
        }

        try
        {
            var session = new VirtualDeviceSession(selected.Device, rules);
            session.BusinessDataReceived += Session_BusinessDataReceived;
            session.ResponseSent += Session_ResponseSent;
            session.Faulted += Session_Faulted;
            _session = session;
            InjectionTargetText.Text = $"注入目标：{selected.Device.PortName}";
            AppendLog($"已打开控制通道 {selected.Device.PortName}");
            SetStatus($"控制通道已打开：{selected.Device.PortName}");
            LoadSelectedSerialConfig();
        }
        catch (Exception ex)
        {
            AppendLog($"打开控制通道失败：{ex.Message}");
            SetStatus($"打开控制通道失败：{ex.Message}", true);
        }

        UpdateControls();
    }

    private void Session_BusinessDataReceived(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) AppendLog(
                $"RX 业务端 [{_session?.Device.PortName ?? "未知端口"}] [{byteCount} B] {text}",
                $"RX [{byteCount} B] {text}");
        });

    private void Session_ResponseSent(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) AppendLog(
                $"TX 自动响应 [{_session?.Device.PortName ?? "未知端口"}] [{byteCount} B] {text}",
                $"TX [{byteCount} B] {text}");
        });

    private void Session_Faulted(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _sendCancellation?.Cancel();
            CloseSession();
            AppendLog($"控制通道异常：{exception.Message}");
            SetStatus($"控制通道异常：{exception.Message}", true);
            UpdateControls();
        });

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateLabel is null) return;
        bool fixedText = ModeCombo.SelectedIndex == (int)DataMode.Fixed;
        TemplateLabel.Text = fixedText ? "固定文本" : "消息模板";
        TemplateHint.Visibility = fixedText ? Visibility.Collapsed : Visibility.Visible;
        if (fixedText && TemplateBox.Text == "{timestamp},{counter},{value}")
            TemplateBox.Text = "模拟设备数据";
        else if (!fixedText && TemplateBox.Text == "模拟设备数据")
            TemplateBox.Text = "{timestamp},{counter},{value}";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInterval(out int interval)) return;
        _sendCancellation = new CancellationTokenSource();
        _sendLoop = SendLoopAsync(interval, _sendCancellation.Token);
        UpdateControls();
        SetStatus($"正在向 {_session?.Device.PortName ?? "虚拟设备"} 周期注入数据");
    }

    private async Task SendLoopAsync(int interval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendCurrentAsync();
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                AppendLog($"注入已停止：{ex.Message}");
                SetStatus($"注入已停止：{ex.Message}", true);
            }
        }
        finally
        {
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            _sendLoop = null;
            if (!_closing) UpdateControls();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopSendingAsync();
        if (!_closing) SetStatus("数据注入已停止");
    }

    private async Task StopSendingAsync()
    {
        _sendCancellation?.Cancel();
        if (_sendLoop is { } loop) await loop;
        if (!_closing) UpdateControls();
    }

    private async void SendOnceButton_Click(object sender, RoutedEventArgs e)
    {
        _sendingOnce = true;
        UpdateControls();
        try { await SendCurrentAsync(); }
        catch (Exception ex)
        {
            AppendLog($"注入失败：{ex.Message}");
            SetStatus($"注入失败：{ex.Message}", true);
        }
        finally
        {
            _sendingOnce = false;
            if (!_closing) UpdateControls();
        }
    }

    private async Task SendCurrentAsync()
    {
        VirtualDeviceSession session = _session ??
            throw new InvalidOperationException("控制通道尚未打开。");
        string portName = session.Device.PortName;
        string data = _generator.Next((DataMode)ModeCombo.SelectedIndex, TemplateBox.Text);
        data += LineEndingCombo.SelectedIndex switch
        {
            1 => "\r\n",
            2 => "\n",
            3 => "\r",
            _ => string.Empty
        };
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        await Task.Run(() => session.Inject(bytes));
        if (!_closing) AppendLog(
            $"TX 注入 [{portName}] [{bytes.Length} B] {EscapeForLog(data)}",
            $"TX [{bytes.Length} B] {EscapeForLog(data)}");
    }

    private bool TryGetInterval(out int interval)
    {
        if (int.TryParse(IntervalBox.Text, out interval) && interval is >= 10 and <= 3_600_000)
            return true;
        SetStatus("发送间隔须为 10 到 3,600,000 毫秒。", true);
        return false;
    }

    private static bool TryParseRules(
        string text,
        out IReadOnlyList<KeyValuePair<string, string>> rules,
        out string? error)
    {
        var parsed = new List<KeyValuePair<string, string>>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                rules = Array.Empty<KeyValuePair<string, string>>();
                error = $"响应规则格式错误：{line}";
                return false;
            }

            string request = DecodeEscapes(line[..separator].Trim());
            string response = DecodeEscapes(line[(separator + 1)..].Trim());
            if (request.Length == 0)
            {
                rules = Array.Empty<KeyValuePair<string, string>>();
                error = "响应规则的请求内容不能为空。";
                return false;
            }
            parsed.Add(new(request, response));
        }

        rules = parsed;
        error = null;
        return true;
    }

    private static string DecodeEscapes(string value) =>
        value.Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _generator.Reset();
        AppendLog("计数器已重置");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _dataRecords.Clear();
        LogBox.Text = string.Empty;
    }

    private void ShowLogTimestampCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LogBox is not null) RenderDataRecords();
    }

    private void RenderDataRecords()
    {
        bool showTimestamp = ShowLogTimestampCheck.IsChecked == true;
        LogBox.Text = string.Join("\n", _dataRecords.Select(record =>
            showTimestamp
                ? $"[{record.Timestamp:HH:mm:ss.fff}] {record.Data}"
                : record.Data));
        LogBox.SelectionStart = 0;
        LogBox.SelectionLength = 0;
    }

    private void UpdateControls()
    {
        bool connected = _session?.IsOpen == true;
        bool running = _sendLoop is not null;
        bool busy = running || _sendingOnce;
        DeviceCombo.IsEnabled = !connected;
        RefreshButton.IsEnabled = !connected;
        RuleBox.IsEnabled = !connected;
        SetSerialParameterControlsEnabled(
            DeviceCombo.SelectedItem is not null && _selectedProtocolVersion >= 2);
        CreateDeviceButton.IsEnabled = !connected && !busy && !_deviceManagementBusy;
        UpdateDriverButton.IsEnabled = !connected && !busy &&
            !_deviceManagementBusy && DeviceCombo.SelectedItem is not null;
        RemoveDeviceButton.IsEnabled = !connected && !busy &&
            !_deviceManagementBusy && DeviceCombo.SelectedItem is not null;
        ConnectButton.Content = connected ? "关闭控制通道" : "打开控制通道";
        ConnectButton.IsEnabled = !_sendingOnce;
        StartButton.IsEnabled = connected && !busy;
        StopButton.IsEnabled = running;
        SendOnceButton.IsEnabled = connected && !busy;
    }

    private void AppendLog(string message, string? displayData = null)
    {
        DateTime timestamp = DateTime.Now;
        string line = $"[{timestamp:HH:mm:ss.fff}] {message}";
        if (displayData is not null)
        {
            _dataRecords.Insert(0, (timestamp, displayData));
            if (_dataRecords.Count > 200) _dataRecords.RemoveAt(200);
            RenderDataRecords();
        }
        try
        {
            BoundedLogFile.AppendPortable("runtime.log", line);
        }
        catch
        {
            // Runtime logging must not interrupt serial simulation.
        }
    }

    private static string EscapeForLog(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private void SetStatus(string message, bool error = false) =>
        StatusText.Text = error ? $"错误：{message}" : message;

    private void CloseSession()
    {
        VirtualDeviceSession? session = _session;
        _session = null;
        if (!_closing) InjectionTargetText.Text = "注入目标：未连接";
        if (session is null) return;
        session.BusinessDataReceived -= Session_BusinessDataReceived;
        session.ResponseSent -= Session_ResponseSent;
        session.Faulted -= Session_Faulted;
        session.Dispose();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _sendCancellation?.Cancel();
        CloseSession();
    }

    private sealed record DeviceComboItem(VirtualDeviceInfo Device)
    {
        public override string ToString() => Device.DisplayName;
    }
}
