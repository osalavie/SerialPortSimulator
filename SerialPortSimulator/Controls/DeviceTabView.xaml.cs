using System.Globalization;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SerialPortSimulator.Services;

namespace SerialPortSimulator.Controls;

/// <summary>One device tab: owns its control channel, sender, rules and traffic log.</summary>
public sealed partial class DeviceTabView : UserControl
{
    private readonly DataGenerator _generator = new();
    private readonly List<(DateTime Timestamp, string Data)> _dataRecords = new();
    private readonly List<TextBox> _fixedTextInputs = new();
    private readonly DispatcherQueueTimer _lineStateTimer;
    private VirtualDeviceSession? _session;
    private CancellationTokenSource? _sendCancellation;
    private Task? _sendLoop;
    private SerialLineConfig? _lastAppliedConfig;
    private uint _protocolVersion;
    private int _fixedTextIndex;
    private bool _initialized;
    private bool _shutdown;
    private bool _sendingOnce;
    private bool _refreshingLanguage;
    private bool _updatingCts;
    private bool _applyingConfig;
    private bool _deviceManagementBusy;

    public DeviceTabView(VirtualDeviceInfo device)
    {
        Device = device;
        InitializeComponent();
        _lineStateTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _lineStateTimer.Interval = TimeSpan.FromMilliseconds(300);
        _lineStateTimer.Tick += (_, _) => UpdateBusinessLineState();
        AddFixedTextRow();
        _initialized = true;
        UpdateModePanels();
        UpdateInjectionTargetText();
        UpdateBusinessLineState();
        UpdateControls();
        LoadSerialConfig();
    }

    public VirtualDeviceInfo Device { get; private set; }

    public bool IsChannelOpen => _session is not null;

    public event Action<string, bool>? StatusChanged;
    public event Action<DeviceTabView>? UpdateDriverRequested;

    public void UpdateDevice(VirtualDeviceInfo device)
    {
        Device = device;
        if (!_shutdown) UpdateInjectionTargetText();
    }

    public void SetDeviceManagementBusy(bool busy)
    {
        _deviceManagementBusy = busy;
        if (!_shutdown) UpdateControls();
    }

    public void ReloadSerialConfig()
    {
        if (!_shutdown) LoadSerialConfig();
    }

    public void RefreshLocalizedText()
    {
        if (_shutdown) return;
        _refreshingLanguage = true;
        try
        {
            UiText.RefreshSelection(ParityCombo);
            UiText.RefreshSelection(ModeCombo);
            UiText.RefreshSelection(LineEndingCombo);
        }
        finally
        {
            _refreshingLanguage = false;
        }
        UpdateModePanels();
        UpdateInjectionTargetText();
        LoadSerialConfig();
        UpdateBusinessLineState();
        UpdateControls();
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        await StopSendingAsync();
        CloseSession();
        _lineStateTimer.Stop();
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ToggleChannelAsync();
    }

    private async Task ToggleChannelAsync()
    {
        if (_shutdown || _deviceManagementBusy) return;
        if (_session is not null)
        {
            await CloseChannelAsync();
            StatusChanged?.Invoke(UiLanguage.Text("控制通道已关闭", "Control link closed"), false);
            return;
        }

        if (!TryParseRules(RuleBox.Text, out var rules, out string? ruleError))
        {
            StatusChanged?.Invoke(ruleError!, true);
            return;
        }

        try
        {
            var session = new VirtualDeviceSession(Device, rules);
            session.BusinessDataReceived += Session_BusinessDataReceived;
            session.ResponseSent += Session_ResponseSent;
            session.Faulted += Session_Faulted;
            _session = session;
            _lineStateTimer.Start();
            UpdateInjectionTargetText();
            AppendLog(UiLanguage.Text($"已打开控制通道 {Device.PortName}", $"Opened control link for {Device.PortName}"));
            LoadSerialConfig();
            StatusChanged?.Invoke(
                UiLanguage.Text($"控制通道已打开：{Device.PortName}", $"Control link open: {Device.PortName}"),
                false);
        }
        catch (Exception ex)
        {
            AppendLog(UiLanguage.Text($"打开控制通道失败：{ex.Message}", $"Could not open control link: {ex.Message}"));
            StatusChanged?.Invoke(
                UiLanguage.Text($"打开控制通道失败：{ex.Message}", $"Could not open control link: {ex.Message}"),
                true);
        }
        UpdateControls();
    }

    public async Task CloseChannelAsync()
    {
        await StopSendingAsync();
        if (_shutdown) return;
        CloseSession();
        UpdateInjectionTargetText();
        LoadSerialConfig();
        UpdateControls();
    }

    private void CloseSession()
    {
        _lineStateTimer.Stop();
        VirtualDeviceSession? session = _session;
        _session = null;
        if (!_shutdown)
            BusinessLineText.Text = UiLanguage.Text("业务控制线：打开控制通道后读取", "Open the control link to read client lines.");
        if (session is null) return;
        session.BusinessDataReceived -= Session_BusinessDataReceived;
        session.ResponseSent -= Session_ResponseSent;
        session.Faulted -= Session_Faulted;
        session.Dispose();
    }

    private void Session_BusinessDataReceived(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_shutdown) AppendLog(
                UiLanguage.Text($"RX 业务端 [{Device.PortName}] [{byteCount} B] {text}", $"RX Client [{Device.PortName}] [{byteCount} B] {text}"),
                $"RX [{byteCount} B] {text}");
        });

    private void Session_ResponseSent(string text, int byteCount) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_shutdown) AppendLog(
                UiLanguage.Text($"TX 自动响应 [{Device.PortName}] [{byteCount} B] {text}", $"TX Auto reply [{Device.PortName}] [{byteCount} B] {text}"),
                $"TX [{byteCount} B] {text}");
        });

    private void Session_Faulted(Exception exception) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_shutdown) return;
            _sendCancellation?.Cancel();
            CloseSession();
            AppendLog(UiLanguage.Text($"控制通道异常：{exception.Message}", $"Control link error: {exception.Message}"));
            StatusChanged?.Invoke(
                UiLanguage.Text($"控制通道异常：{exception.Message}", $"Control link error: {exception.Message}"),
                true);
            LoadSerialConfig();
            UpdateControls();
        });

    private void LoadSerialConfig()
    {
        if (_shutdown) return;
        _protocolVersion = 0;
        _lastAppliedConfig = null;
        try
        {
            uint version;
            SerialLineConfig? config = null;
            bool cts = false;

            if (_session is { } session)
            {
                version = session.ProtocolVersion;
                if (session.SupportsSerialConfig) config = session.GetSerialConfig();
                if (version >= 3) cts = session.GetCts();
            }
            else
            {
                using var client = new VirtualDeviceClient(Device.InterfacePath);
                version = client.ProtocolVersion;
                if (client.SupportsSerialConfig) config = client.GetSerialConfig();
                if (version >= 3) cts = client.GetCts();
            }

            _protocolVersion = version;
            if (version >= 3)
            {
                _updatingCts = true;
                try { CtsOnCheck.IsChecked = cts; }
                finally { _updatingCts = false; }
            }

            if (config is not null)
            {
                ApplyConfigToControls(config);
                _lastAppliedConfig = config;
                SerialParameterStatusText.Text = version >= 3
                    ? UiLanguage.Text("参数修改即时生效；可开启线路限速并设置 CTS。", "Changes apply immediately. You can limit speed and set CTS.")
                    : UiLanguage.Text("已安装驱动仅支持基本串口参数；线路限速和 CTS 需要协议版本 3。", "The driver only supports basic port settings. Update the driver for speed limits and CTS.");
            }
            else
            {
                SerialParameterStatusText.Text = UiLanguage.Text("当前驱动协议不支持模拟器参数配置，请更新驱动。", "Update the driver to change port settings.");
            }
        }
        catch (Exception ex)
        {
            SerialParameterStatusText.Text = UiLanguage.Text($"读取串口参数失败：{ex.Message}", $"Could not read port settings: {ex.Message}");
        }

        UpdateDriverButton.Visibility = _protocolVersion < 4 ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
    }

    private void ApplyConfigToControls(SerialLineConfig config)
    {
        _applyingConfig = true;
        try
        {
            BaudRateCombo.Text = config.BaudRate.ToString();
            DataBitsCombo.SelectedIndex = config.DataBits - 5;
            ParityCombo.SelectedIndex = (int)config.Parity;
            StopBitsCombo.SelectedIndex = (int)config.StopBits;
            LimitToBaudRateCheck.IsChecked = config.LimitToBaudRate;
        }
        finally
        {
            _applyingConfig = false;
        }
    }

    private void SerialConfigControl_Changed(object sender, RoutedEventArgs e) => OnSerialConfigEdited();

    private void OnSerialConfigEdited()
    {
        if (!_initialized || _applyingConfig || _refreshingLanguage || _shutdown) return;
        if (_protocolVersion < 2) return;
        if (DataBitsCombo.SelectedIndex < 0 || ParityCombo.SelectedIndex < 0 || StopBitsCombo.SelectedIndex < 0) return;

        if (!uint.TryParse(BaudRateCombo.Text, out uint baudRate))
        {
            SerialParameterStatusText.Text = UiLanguage.Text("波特率必须为正整数。", "Enter a positive whole number for the baud rate.");
            return;
        }

        byte dataBits = (byte)(DataBitsCombo.SelectedIndex + 5);
        var parity = (SimulatorParity)ParityCombo.SelectedIndex;
        var stopBits = (SimulatorStopBits)StopBitsCombo.SelectedIndex;
        var config = new SerialLineConfig(baudRate, dataBits, parity, stopBits,
            LimitToBaudRateCheck.IsChecked == true);
        try
        {
            config.Validate();
            if (config == _lastAppliedConfig) return;

            if (_session is { } session) session.SetSerialConfig(config);
            else
            {
                using var client = new VirtualDeviceClient(Device.InterfacePath);
                client.SetSerialConfig(config);
            }

            _lastAppliedConfig = config;
            SerialParameterStatusText.Text = UiLanguage.Text(
                $"已生效 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}；线路限速{(config.LimitToBaudRate ? "开启" : "关闭")}。",
                $"Applied {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}. Speed limit: {(config.LimitToBaudRate ? "on" : "off")}.");
            AppendLog(UiLanguage.Text($"设备参数 {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}", $"Port settings {baudRate} {dataBits}{ParityText(parity)}{StopBitsText(stopBits)}"));
            StatusChanged?.Invoke(UiLanguage.Text("虚拟串口参数已更新", "Port settings updated"), false);
        }
        catch (Exception ex)
        {
            SerialParameterStatusText.Text = UiLanguage.Text($"应用串口参数失败：{ex.Message}", $"Could not apply port settings: {ex.Message}");
            StatusChanged?.Invoke(
                UiLanguage.Text($"应用串口参数失败：{ex.Message}", $"Could not apply port settings: {ex.Message}"),
                true);
        }
    }

    private void BaudRateCombo_TextSubmitted(object sender, ComboBoxTextSubmittedEventArgs args) =>
        OnSerialConfigEdited();

    private void CtsOnCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingCts || _refreshingLanguage || _shutdown) return;
        if (_protocolVersion < 3) return;
        try
        {
            bool enabled = CtsOnCheck.IsChecked == true;
            if (_session is { } session) session.SetCts(enabled);
            else
            {
                using var client = new VirtualDeviceClient(Device.InterfacePath);
                client.SetCts(enabled);
            }
            AppendLog(UiLanguage.Text($"CTS {(enabled ? "有效" : "无效")}", $"CTS {(enabled ? "on" : "off")}"));
            StatusChanged?.Invoke(
                UiLanguage.Text($"CTS 已设为{(enabled ? "有效" : "无效")}", $"CTS is {(enabled ? "on" : "off")}"),
                false);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(UiLanguage.Text($"设置 CTS 失败：{ex.Message}", $"Could not set CTS: {ex.Message}"), true);
            _updatingCts = true;
            try { CtsOnCheck.IsChecked = CtsOnCheck.IsChecked != true; }
            finally { _updatingCts = false; }
        }
    }

    private void UpdateDriverButton_Click(object sender, RoutedEventArgs e) =>
        UpdateDriverRequested?.Invoke(this);

    private void RuleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _refreshingLanguage || _shutdown) return;
        if (TryParseRules(RuleBox.Text, out var rules, out _))
            _session?.UpdateRules(rules);
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _refreshingLanguage) return;
        if (GetSelectedMode() == DataMode.Fixed) _fixedTextIndex = 0;
        UpdateModePanels();
    }

    private DataMode GetSelectedMode() =>
        ModeCombo.SelectedIndex >= 0 ? (DataMode)ModeCombo.SelectedIndex : DataMode.Fixed;

    private void UpdateModePanels()
    {
        var mode = GetSelectedMode();
        bool fixedMode = mode == DataMode.Fixed;
        SineParameterRow.Visibility = mode == DataMode.Sine ? Visibility.Visible : Visibility.Collapsed;
        RandomParameterRow.Visibility = mode == DataMode.Random ? Visibility.Visible : Visibility.Collapsed;
        TemplatePanel.Visibility = fixedMode ? Visibility.Collapsed : Visibility.Visible;
        FixedTextPanel.Visibility = fixedMode ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Visibility = mode == DataMode.Counter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFixedTextButton_Click(object sender, RoutedEventArgs e) => AddFixedTextRow(focus: true);

    private void AddFixedTextRow(string text = "", bool focus = false)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            Text = text,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiText.SetKey(box, "FixedTextPlaceholder");
        box.Loaded += (_, _) => RemoveClearButton(box);

        var delete = new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }
        };
        UiText.SetTip(delete, "DeleteText");
        AutomationProperties.SetName(delete, UiLanguage.Get("DeleteText"));
        delete.Click += (_, _) => RemoveFixedTextRow(row, box);

        Grid.SetColumn(delete, 1);
        row.Children.Add(box);
        row.Children.Add(delete);
        _fixedTextInputs.Add(box);
        FixedTextList.Children.Add(row);
        if (focus) box.Focus(FocusState.Programmatic);
    }

    private void RemoveFixedTextRow(Grid row, TextBox box)
    {
        _fixedTextInputs.Remove(box);
        FixedTextList.Children.Remove(row);
        if (_fixedTextInputs.Count == 0) AddFixedTextRow();
    }

    private static void RemoveClearButton(TextBox box)
    {
        if (FindDescendant(box, "DeleteButton") is not Button clear) return;
        if (clear.Parent is Panel panel)
        {
            panel.Children.Remove(clear);
            return;
        }
        clear.Visibility = Visibility.Collapsed;
        clear.Width = 0;
        clear.MinWidth = 0;
        clear.Padding = new Thickness(0);
        clear.IsHitTestVisible = false;
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindDescendant(child, name) is { } found) return found;
        }
        return null;
    }

    private List<string> GetFixedTexts() =>
        _fixedTextInputs
            .Select(box => box.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

    private string NextFixedText()
    {
        var texts = GetFixedTexts();
        if (texts.Count == 0)
            throw new InvalidOperationException(UiLanguage.Text("固定文本列表为空，请先添加要发送的文本。", "Add at least one fixed text before sending."));
        if (_fixedTextIndex >= texts.Count) _fixedTextIndex = 0;
        string text = texts[_fixedTextIndex];
        if (_fixedTextIndex < texts.Count - 1) _fixedTextIndex++;
        else if (LoopFixedTextCheck.IsChecked == true) _fixedTextIndex = 0;
        return text;
    }

    private bool TryPrepareSending(out string? error)
    {
        error = null;
        if (GetSelectedMode() == DataMode.Fixed)
        {
            if (GetFixedTexts().Count > 0) return true;
            error = UiLanguage.Text("固定文本列表为空，请先添加要发送的文本。", "Add at least one fixed text before sending.");
            return false;
        }

        try
        {
            ApplySimulationSettings();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ApplySimulationSettings()
    {
        switch (GetSelectedMode())
        {
            case DataMode.Sine:
                _generator.SineAmplitude = ParseNumber(SineAmplitudeBox.Text, UiLanguage.Text("幅值", "Amplitude"));
                double period = ParseNumber(SinePeriodBox.Text, UiLanguage.Text("周期", "Period"));
                if (period <= 0)
                    throw new ArgumentException(UiLanguage.Text("周期必须大于 0。", "The period must be greater than zero."));
                _generator.SinePeriod = period;
                _generator.SineOffset = ParseNumber(SineOffsetBox.Text, UiLanguage.Text("偏移", "Offset"));
                break;
            case DataMode.Random:
                double minimum = ParseNumber(RandomMinimumBox.Text, UiLanguage.Text("最小值", "Minimum"));
                double maximum = ParseNumber(RandomMaximumBox.Text, UiLanguage.Text("最大值", "Maximum"));
                if (minimum > maximum)
                    throw new ArgumentException(UiLanguage.Text("最小值不能大于最大值。", "Minimum must not exceed maximum."));
                if (!int.TryParse(RandomPrecisionBox.Text, out int precision) || precision is < 0 or > 6)
                    throw new ArgumentException(UiLanguage.Text("精确度须为 0 到 6 的整数。", "Decimals must be an integer from 0 to 6."));
                _generator.RandomMinimum = minimum;
                _generator.RandomMaximum = maximum;
                _generator.RandomPrecision = precision;
                break;
        }
    }

    private static double ParseNumber(string text, string label)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value))
            return value;
        throw new ArgumentException(UiLanguage.Text($"{label}必须是数字。", $"{label} must be a number."));
    }

    private void UpdateInjectionTargetText() =>
        InjectionTargetText.Text = _session is not null
            ? UiLanguage.Text($"注入目标：{Device.PortName}", $"Target: {Device.PortName}")
            : UiLanguage.Text("注入目标：未连接", "Target: Not connected");

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInterval(out int interval)) return;
        if (!TryPrepareSending(out string? error))
        {
            StatusChanged?.Invoke(error!, true);
            return;
        }
        if (GetSelectedMode() == DataMode.Fixed) _fixedTextIndex = 0;
        _sendCancellation = new CancellationTokenSource();
        _sendLoop = SendLoopAsync(interval, _sendCancellation.Token);
        UpdateControls();
        StatusChanged?.Invoke(
            UiLanguage.Text($"正在向 {Device.PortName} 周期注入数据", $"Sending to {Device.PortName}"),
            false);
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
            if (!_shutdown)
            {
                AppendLog(UiLanguage.Text($"注入已停止：{ex.Message}", $"Sending stopped: {ex.Message}"));
                StatusChanged?.Invoke(UiLanguage.Text($"注入已停止：{ex.Message}", $"Sending stopped: {ex.Message}"), true);
            }
        }
        finally
        {
            _sendCancellation?.Dispose();
            _sendCancellation = null;
            _sendLoop = null;
            if (!_shutdown) UpdateControls();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopSendingAsync();
        if (!_shutdown) StatusChanged?.Invoke(UiLanguage.Text("数据注入已停止", "Sending stopped"), false);
    }

    private async Task StopSendingAsync()
    {
        _sendCancellation?.Cancel();
        if (_sendLoop is { } loop) await loop;
        if (!_shutdown) UpdateControls();
    }

    private async void SendOnceButton_Click(object sender, RoutedEventArgs e)
    {
        _sendingOnce = true;
        UpdateControls();
        try { await SendCurrentAsync(); }
        catch (Exception ex)
        {
            AppendLog(UiLanguage.Text($"注入失败：{ex.Message}", $"Could not send data: {ex.Message}"));
            StatusChanged?.Invoke(UiLanguage.Text($"注入失败：{ex.Message}", $"Could not send data: {ex.Message}"), true);
        }
        finally
        {
            _sendingOnce = false;
            if (!_shutdown) UpdateControls();
        }
    }

    private async Task SendCurrentAsync()
    {
        VirtualDeviceSession session = _session ??
            throw new InvalidOperationException(UiLanguage.Text("控制通道尚未打开。", "Open the control link first."));
        var mode = GetSelectedMode();
        string data;
        if (mode == DataMode.Fixed)
        {
            data = NextFixedText();
        }
        else
        {
            ApplySimulationSettings();
            data = _generator.Next(mode, TemplateBox.Text);
        }
        data += LineEndingCombo.SelectedIndex switch
        {
            1 => "\r\n",
            2 => "\n",
            3 => "\r",
            _ => string.Empty
        };
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        await Task.Run(() => session.Inject(bytes));
        if (!_shutdown) AppendLog(
            UiLanguage.Text($"TX 注入 [{Device.PortName}] [{bytes.Length} B] {EscapeForLog(data)}", $"TX Simulated [{Device.PortName}] [{bytes.Length} B] {EscapeForLog(data)}"),
            $"TX [{bytes.Length} B] {EscapeForLog(data)}");
    }

    private bool TryGetInterval(out int interval)
    {
        if (int.TryParse(IntervalBox.Text, out interval) && interval is >= 10 and <= 3_600_000)
            return true;
        StatusChanged?.Invoke(
            UiLanguage.Text("发送间隔须为 10 到 3,600,000 毫秒。", "Use an interval from 10 to 3,600,000 ms."),
            true);
        return false;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedMode() != DataMode.Counter) return;
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
        bool connected = _session is not null;
        bool running = _sendLoop is not null;
        bool busy = running || _sendingOnce;
        bool editable = !_deviceManagementBusy;
        bool supportsConfig = _protocolVersion >= 2;

        ConnectButton.Content = connected
            ? UiLanguage.Text("关闭控制通道", "Close control link")
            : UiLanguage.Text("打开控制通道", "Open control link");
        ConnectButton.IsEnabled = editable && !_sendingOnce;
        BaudRateCombo.IsEnabled = editable && supportsConfig;
        DataBitsCombo.IsEnabled = editable && supportsConfig;
        ParityCombo.IsEnabled = editable && supportsConfig;
        StopBitsCombo.IsEnabled = editable && supportsConfig;
        LimitToBaudRateCheck.IsEnabled = editable && supportsConfig && _protocolVersion >= 3;
        CtsOnCheck.IsEnabled = editable && _protocolVersion >= 3;
        UpdateDriverButton.IsEnabled = editable && !connected && !busy;
        StartButton.IsEnabled = connected && !busy;
        StopButton.IsEnabled = running;
        SendOnceButton.IsEnabled = connected && !busy;
    }

    private void UpdateBusinessLineState()
    {
        if (_shutdown) return;
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

    private static string EscapeForLog(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

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
}
