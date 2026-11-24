using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SerialPortSimulator.Services;

namespace SerialPortSimulator;

public sealed partial class MainWindow : Window
{

    private readonly DataGenerator _generator = new();
    private readonly List<(DateTime Timestamp, string Data)> _dataRecords = new();
    private readonly DispatcherQueueTimer _lineStateTimer;
    private readonly SelfResourceMonitor _resourceMonitor = new();
    private readonly DispatcherQueueTimer _resourceTimer;
    private VirtualDeviceSession? _session;
    private CancellationTokenSource? _sendCancellation;
    private Task? _sendLoop;
    private bool _sendingOnce;
    private bool _deviceManagementBusy;
    private bool _refreshingLanguage;
    private bool _updatingCts;
    private uint _selectedProtocolVersion;
    private bool _closing;

    private async void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        await UiLanguage.PromptForChangeAsync(((FrameworkElement)sender).XamlRoot);
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = UiLanguage.Get("SimulatorTitle");
        TitleBarChrome.Attach(this, TitleBarRoot, TitleBarDragRegion, CaptionInsetColumn);
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 900));
        _lineStateTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lineStateTimer.Interval = TimeSpan.FromMilliseconds(300);
        _lineStateTimer.Tick += (_, _) => UpdateBusinessLineState();
        _lineStateTimer.Start();
        _resourceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _resourceTimer.Interval = TimeSpan.FromSeconds(1);
        _resourceTimer.Tick += (_, _) => UpdateResourceText();
        _resourceTimer.Start();
        Closed += MainWindow_Closed;
        UiLanguage.Changed += UiLanguage_Changed;
        RefreshDevices();
    }

    private void UiLanguage_Changed()
    {
        if (_closing) return;
        if (DispatcherQueue.HasThreadAccess) RefreshLocalizedUi();
        else DispatcherQueue.TryEnqueue(RefreshLocalizedUi);
    }

    private void RefreshLocalizedUi()
    {
        if (_closing) return;
        _refreshingLanguage = true;
        UiText.RefreshAll();
        UiText.RefreshSelection(ParityCombo);
        UiText.RefreshSelection(ModeCombo);
        UiText.RefreshSelection(LineEndingCombo);
        _refreshingLanguage = false;
        Title = UiLanguage.Get("SimulatorTitle");
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        UpdateTemplateLabel();
        UpdateInjectionTargetText();
        UpdateSelectedDeviceDetails();
        LoadSelectedSerialConfig();
        UpdateBusinessLineState();
        SetStatus(UiLanguage.Text("界面已切换为中文，语言设置已保存。", "UI language switched to English. Setting saved."));
        UpdateControls();
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
            Title = UiLanguage.Text("删除虚拟设备", "Remove virtual device"),
            Content = UiLanguage.Text($"将删除 {selected.Device.PortName}（{selected.Device.InstanceId}）。业务端随后无法再打开该端口；如果这是最后一个本产品设备，还会卸载驱动包并移除测试证书。", $"Remove {selected.Device.PortName} ({selected.Device.InstanceId})? The client will lose access to this port. If this is the last device, the driver package and test certificate will also be removed."),
            PrimaryButtonText = UiLanguage.Text("删除", "Remove"),
            CloseButtonText = UiLanguage.Text("取消", "Cancel"),
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
                UiLanguage.Text("找不到 SerialPortSimulator.DeviceManager.exe。请确认整个 ZIP 已解压，", "SerialPortSimulator.DeviceManager.exe is missing. Extract the full ZIP. ") +
                UiLanguage.Text("且管理员程序位于主程序目录下的 DeviceManager 文件夹。", "The DeviceManager folder must be next to the main app."),
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
        _deviceManagementBusy = true;
        UpdateControls();
        try
        {
            SetStatus(GetDeviceManagerProgress(action));
            using Process? process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException(UiLanguage.Text("无法启动管理员进程。", "Could not start the admin tool."));
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                if (action == "Remove") RefreshDevices();
                throw new InvalidOperationException(GetDeviceManagerError(process.ExitCode));
            }
            RefreshDevices();
            if (action != "Remove" && DeviceCombo.Items.Count == 0)
                SetStatus(UiLanguage.Text("驱动操作已完成，但设备控制接口尚未加载。请在设备管理器查看本产品虚拟串口的设备状态。", "The driver operation finished, but the control link is unavailable. Check the port status in Device Manager."), true);
            else
                SetStatus(GetDeviceManagerSuccess(action));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus(UiLanguage.Text("管理员授权已取消", "Admin request canceled"));
        }
        catch (Exception ex)
        {
            SetStatus(UiLanguage.Text($"设备管理失败：{ex.Message}", $"Device operation failed: {ex.Message}"), true);
        }
        finally
        {
            _deviceManagementBusy = false;
            UpdateControls();
        }
    }

    private static string GetDeviceManagerProgress(string action) => action switch
    {
        "Create" => UiLanguage.Text("等待管理员创建虚拟设备…", "Waiting for admin to add device…"),
        "Update" => UiLanguage.Text("等待管理员更新虚拟串口驱动…", "Waiting for admin to update driver…"),
        _ => UiLanguage.Text("等待管理员删除虚拟设备…", "Waiting for admin to remove device…")
    };

    private static string GetDeviceManagerSuccess(string action) => action switch
    {
        "Create" => UiLanguage.Text("虚拟设备已创建", "Virtual device added"),
        "Update" => UiLanguage.Text("虚拟串口驱动已更新", "Driver updated"),
        _ => UiLanguage.Text("虚拟设备已删除", "Virtual device removed")
    };

    private static string GetDeviceManagerError(int exitCode)
    {
        string message = exitCode switch
        {
            10 => UiLanguage.Text("设备管理参数无效。", "Invalid device options."),
            11 => UiLanguage.Text("驱动包不完整，请重新生成发行包。", "The driver package is incomplete. Rebuild the release."),
            12 => UiLanguage.Text("测试证书导入失败。", "Could not import the test certificate."),
            13 => UiLanguage.Text("创建虚拟设备失败。", "Could not add the virtual device."),
            14 => UiLanguage.Text("安装虚拟串口驱动失败。", "Could not install the driver."),
            15 => UiLanguage.Text("找不到指定虚拟设备。", "Virtual device not found."),
            16 => UiLanguage.Text("拒绝删除非本产品的设备。", "Only simulator devices can be removed."),
            17 => UiLanguage.Text("删除虚拟设备失败。", "Could not remove the virtual device."),
            18 => UiLanguage.Text("虚拟设备已删除，但驱动包或测试证书清理失败。", "Device removed, but driver or certificate cleanup failed."),
            19 => UiLanguage.Text("驱动包未签名或签名证书不匹配，请重新生成发行包。", "The driver has no valid signature for this certificate. Rebuild the release."),
            _ => UiLanguage.Text($"设备管理程序异常退出（{exitCode}）。", $"The admin tool quit with code {exitCode}.")
        };
        return UiLanguage.Text($"{message} 详情请查看 {GetDeviceManagerLogPath()}。", $"{message} See {GetDeviceManagerLogPath()} for details.");
    }

    private static string GetDeviceManagerLogPath()
    {
        string? helper = FindDeviceManagerExecutable();
        string directory = helper is not null
            ? Path.GetDirectoryName(helper)!
            : GetApplicationDirectory();
        return Path.Combine(directory, "Logs", "device-manager.log");
    }

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
            SetStatus(UiLanguage.Text($"枚举虚拟设备失败：{ex.Message}", $"Could not list virtual devices: {ex.Message}"), true);
        }

        DeviceCombo.SelectedItem = DeviceCombo.Items
            .OfType<DeviceComboItem>()
            .FirstOrDefault(item => item.Device.InstanceId == previous) ??
            DeviceCombo.Items.FirstOrDefault();

        if (DeviceCombo.Items.Count == 0)
            SetStatus(UiLanguage.Text("未发现可用的模拟串口控制接口。若设备管理器中已有本产品端口，请检查其设备状态；启动失败时需更新驱动。", "No control link found. Check the port status in Device Manager. Update the driver if the device failed to start."), true);
        else if (_session is null)
            SetStatus(UiLanguage.Text("请选择虚拟设备并打开控制通道。", "Select a device and open its control link."));

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
            PortText.Text = UiLanguage.Text("未选择", "None selected");
            DeviceIdText.Text = UiLanguage.Text("未选择", "None selected");
            return;
        }

        PortText.Text = item.Device.PortName;
        DeviceIdText.Text = item.Device.InstanceId;
    }

    private void LoadSelectedSerialConfig()
    {
        if (SerialParameterPanel is null) return;
        _selectedProtocolVersion = 0;
        if (DeviceCombo.SelectedItem is not DeviceComboItem selected)
        {
            SetSerialParameterControlsEnabled(false);
            SerialParameterStatusText.Text = UiLanguage.Text("选择设备后可读取并设置模拟串口参数。", "Select a device to view or change its port settings.");
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
            LimitToBaudRateCheck.IsEnabled = version >= 3;
            CtsOnCheck.IsEnabled = version >= 3;
            if (version >= 3)
            {
                _updatingCts = true;
                try
                {
                    CtsOnCheck.IsChecked = _session is { } activeSession
                        ? activeSession.GetCts()
                        : GetSelectedCts(selected.Device.InterfacePath);
                }
                finally
                {
                    _updatingCts = false;
                }
            }
            if (config is not null)
            {
                SetSerialConfigControls(config);
                SerialParameterStatusText.Text = version >= 3
                    ? UiLanguage.Text("当前参数由模拟器设备提供；可开启线路限速并设置 CTS。", "You can limit the speed and set CTS.")
                    : UiLanguage.Text("已安装驱动仅支持基本串口参数；线路限速和 CTS 需要协议版本 3。请使用新版程序点击“更新驱动”。", "The driver only supports basic port settings. Update the driver for speed limits and CTS (protocol version 3).");
            }
            else
            {
                SerialParameterStatusText.Text = UiLanguage.Text("当前驱动协议不支持模拟器参数配置，请点击“更新驱动”。", "Update the driver to change port settings.");
            }
        }
        catch (Exception ex)
        {
            SerialParameterStatusText.Text = UiLanguage.Text($"读取串口参数失败：{ex.Message}", $"Could not read port settings: {ex.Message}");
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
            SetStatus(UiLanguage.Text("请先选择虚拟设备。", "Select a virtual device first."), true);
            return;
        }

        try
        {
            if (!uint.TryParse(BaudRateCombo.Text, out uint baudRate))
                throw new ArgumentException(UiLanguage.Text("波特率必须为正整数。", "Enter a positive whole number for the baud rate."));
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
                UiLanguage.Text($"已应用 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}；线路限速{(config.LimitToBaudRate ? "开启" : "关闭")}。", $"Applied {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}. Speed limit: {(config.LimitToBaudRate ? "on" : "off")}.");
            AppendLog(UiLanguage.Text($"设备参数 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}", $"Port settings {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}"));
            SetStatus(UiLanguage.Text("虚拟串口参数已更新", "Port settings updated"));
        }
        catch (Exception ex)
        {
            SetStatus(UiLanguage.Text($"应用串口参数失败：{ex.Message}", $"Could not apply port settings: {ex.Message}"), true);
        }
    }

    private static bool GetSelectedCts(string interfacePath)
    {
        using var client = new VirtualDeviceClient(interfacePath);
        return client.GetCts();
    }

    private void CtsOnCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingCts) return;
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
            AppendLog(UiLanguage.Text($"CTS {(enabled ? "有效" : "无效")}", $"CTS {(enabled ? "on" : "off")}"));
            SetStatus(UiLanguage.Text($"CTS 已设为{(enabled ? "有效" : "无效")}", $"CTS is {(enabled ? "on" : "off")}"));
        }
        catch (Exception ex)
        {
            SetStatus(UiLanguage.Text($"设置 CTS 失败：{ex.Message}", $"Could not set CTS: {ex.Message}"), true);
            _updatingCts = true;
            try { CtsOnCheck.IsChecked = CtsOnCheck.IsChecked != true; }
            finally { _updatingCts = false; }
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
            SetStatus(UiLanguage.Text("控制通道已关闭", "Control link closed"));
            UpdateControls();
            return;
        }

        if (DeviceCombo.SelectedItem is not DeviceComboItem selected)
        {
            SetStatus(UiLanguage.Text("请先选择虚拟设备。", "Select a virtual device first."), true);
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
            UpdateInjectionTargetText();
            AppendLog(UiLanguage.Text($"已打开控制通道 {selected.Device.PortName}", $"Opened control link for {selected.Device.PortName}"));
            SetStatus(UiLanguage.Text($"控制通道已打开：{selected.Device.PortName}", $"Control link open: {selected.Device.PortName}"));
            LoadSelectedSerialConfig();
        }
        catch (Exception ex)
        {
            AppendLog(UiLanguage.Text($"打开控制通道失败：{ex.Message}", $"Could not open control link: {ex.Message}"));
            SetStatus(UiLanguage.Text($"打开控制通道失败：{ex.Message}", $"Could not open control link: {ex.Message}"), true);
        }

        UpdateControls();
    }

    private void Session_BusinessDataReceived(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) AppendLog(
                UiLanguage.Text($"RX 业务端 [{_session?.Device.PortName ?? "未知端口"}] [{byteCount} B] {text}", $"RX Client [{_session?.Device.PortName ?? "Unknown port"}] [{byteCount} B] {text}"),
                $"RX [{byteCount} B] {text}");
        });

    private void Session_ResponseSent(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) AppendLog(
                UiLanguage.Text($"TX 自动响应 [{_session?.Device.PortName ?? "未知端口"}] [{byteCount} B] {text}", $"TX Auto reply [{_session?.Device.PortName ?? "Unknown port"}] [{byteCount} B] {text}"),
                $"TX [{byteCount} B] {text}");
        });

    private void Session_Faulted(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _sendCancellation?.Cancel();
            CloseSession();
            AppendLog(UiLanguage.Text($"控制通道异常：{exception.Message}", $"Control link error: {exception.Message}"));
            SetStatus(UiLanguage.Text($"控制通道异常：{exception.Message}", $"Control link error: {exception.Message}"), true);
            UpdateControls();
        });

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateLabel is null || _refreshingLanguage) return;
        bool fixedText = ModeCombo.SelectedIndex == (int)DataMode.Fixed;
        UpdateTemplateLabel();
        if (fixedText && TemplateBox.Text == "{timestamp},{counter},{value}")
            TemplateBox.Text = "模拟设备数据";
        else if (!fixedText && TemplateBox.Text == "模拟设备数据")
            TemplateBox.Text = "{timestamp},{counter},{value}";
    }

    private void UpdateTemplateLabel()
    {
        bool fixedText = ModeCombo.SelectedIndex == (int)DataMode.Fixed;
        TemplateLabel.Text = fixedText ? UiLanguage.Text("固定文本", "Fixed text") : UiLanguage.Text("消息模板", "Message format");
        TemplateHint.Visibility = fixedText ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInjectionTargetText() =>
        InjectionTargetText.Text = _session is { } session
            ? UiLanguage.Text($"注入目标：{session.Device.PortName}", $"Target: {session.Device.PortName}")
            : UiLanguage.Text("注入目标：未连接", "Target: Not connected");

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInterval(out int interval)) return;
        _sendCancellation = new CancellationTokenSource();
        _sendLoop = SendLoopAsync(interval, _sendCancellation.Token);
        UpdateControls();
        SetStatus(UiLanguage.Text($"正在向 {_session?.Device.PortName ?? "虚拟设备"} 周期注入数据", $"Sending to {_session?.Device.PortName ?? "virtual device"}"));
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
                AppendLog(UiLanguage.Text($"注入已停止：{ex.Message}", $"Sending stopped: {ex.Message}"));
                SetStatus(UiLanguage.Text($"注入已停止：{ex.Message}", $"Sending stopped: {ex.Message}"), true);
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
        if (!_closing) SetStatus(UiLanguage.Text("数据注入已停止", "Sending stopped"));
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
            AppendLog(UiLanguage.Text($"注入失败：{ex.Message}", $"Could not send data: {ex.Message}"));
            SetStatus(UiLanguage.Text($"注入失败：{ex.Message}", $"Could not send data: {ex.Message}"), true);
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
            throw new InvalidOperationException(UiLanguage.Text("控制通道尚未打开。", "Open the control link first."));
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
            UiLanguage.Text($"TX 注入 [{portName}] [{bytes.Length} B] {EscapeForLog(data)}", $"TX Simulated [{portName}] [{bytes.Length} B] {EscapeForLog(data)}"),
            $"TX [{bytes.Length} B] {EscapeForLog(data)}");
    }

    private bool TryGetInterval(out int interval)
    {
        if (int.TryParse(IntervalBox.Text, out interval) && interval is >= 10 and <= 3_600_000)
            return true;
        SetStatus(UiLanguage.Text("发送间隔须为 10 到 3,600,000 毫秒。", "Use an interval from 10 to 3,600,000 ms."), true);
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
                error = UiLanguage.Text($"响应规则格式错误：{line}", $"Invalid reply rule: {line}");
                return false;
            }

            string request = DecodeEscapes(line[..separator].Trim());
            string response = DecodeEscapes(line[(separator + 1)..].Trim());
            if (request.Length == 0)
            {
                rules = Array.Empty<KeyValuePair<string, string>>();
                error = UiLanguage.Text("响应规则的请求内容不能为空。", "A reply rule needs a request.");
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
        AppendLog(UiLanguage.Text("计数器已重置", "Count reset"));
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
        ConnectButton.Content = connected ? UiLanguage.Text("关闭控制通道", "Close control link") : UiLanguage.Text("打开控制通道", "Open control link");
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
        StatusText.Text = error ? UiLanguage.Text($"错误：{message}", $"Error: {message}") : message;

    private void UpdateResourceText()
    {
        if (_closing) return;
        _resourceMonitor.Sample();
        ResourceText.Text = $"CPU {_resourceMonitor.CpuPercent:F1}% · MEM {_resourceMonitor.WorkingSetMB} MB";
    }

    private void UpdateBusinessLineState()
    {
        if (_closing) return;
        if (_session is not { } session)
        {
            BusinessLineText.Text = UiLanguage.Text("业务控制线：打开控制通道后读取", "Open the control link to read client lines.");
            return;
        }
        if (!session.SupportsLineState)
        {
            BusinessLineText.Text = UiLanguage.Text("业务控制线：当前驱动不支持 DTR/RTS 读取，请更新驱动", "Update the driver to read DTR and RTS.");
            return;
        }

        try
        {
            SimulatorLineState state = session.GetLineState();
            BusinessLineText.Text =
                UiLanguage.Text($"业务控制线：DTR {(state.Dtr ? "有效" : "无效")} · ", $"Client lines: DTR {(state.Dtr ? "on" : "off")} · ") +
                UiLanguage.Text($"RTS {(state.Rts ? "有效" : "无效")} · ", $"RTS {(state.Rts ? "on" : "off")} · ") +
                UiLanguage.Text($"RTS/CTS 流控 {(state.CtsHandshake ? "开启" : "关闭")}", $"RTS/CTS flow control {(state.CtsHandshake ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            BusinessLineText.Text = UiLanguage.Text($"业务控制线读取失败：{ex.Message}", $"Could not read client lines: {ex.Message}");
        }
    }

    private void CloseSession()
    {
        VirtualDeviceSession? session = _session;
        _session = null;
        if (!_closing)
        {
            UpdateInjectionTargetText();
            BusinessLineText.Text = UiLanguage.Text("业务控制线：打开控制通道后读取", "Open the control link to read client lines.");
        }
        if (session is null) return;
        session.BusinessDataReceived -= Session_BusinessDataReceived;
        session.ResponseSent -= Session_ResponseSent;
        session.Faulted -= Session_Faulted;
        session.Dispose();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        UiLanguage.Changed -= UiLanguage_Changed;
        _lineStateTimer.Stop();
        _resourceTimer.Stop();
        _sendCancellation?.Cancel();
        CloseSession();
    }

    private sealed record DeviceComboItem(VirtualDeviceInfo Device)
    {
        public override string ToString() => Device.DisplayName;
    }
}
