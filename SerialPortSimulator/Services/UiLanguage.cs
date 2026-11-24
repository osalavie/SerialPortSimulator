namespace SerialPortSimulator.Services;

internal static partial class UiLanguage
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SerialPortSimulator", "language.txt");

    private static readonly Dictionary<string, (string Chinese, string English)> Strings =
        new(StringComparer.Ordinal)
        {
            ["SwitchLanguage"] = ("English", "中文"),
            ["SimulatorTitle"] = ("串口设备模拟器", "Serial Port Simulator"),
            ["SimulatorIntro"] = ("模拟器定义串口参数，业务端通过 COM 口连接并按设备参数适配", "Set the port here and use the same settings in the client app."),
            ["VirtualDevice"] = ("虚拟设备", "Virtual device"),
            ["Connection"] = ("设备连接", "Connection"),
            ["SelectVirtualPort"] = ("选择本产品的虚拟串口", "Select a virtual port"),
            ["Refresh"] = ("刷新设备", "Refresh"),
            ["OpenControlLink"] = ("打开控制通道", "Open control link"),
            ["ClientPort"] = ("业务端口", "Client port"),
            ["NoneSelected"] = ("未选择", "None selected"),
            ["DeviceId"] = ("设备实例", "Device ID"),
            ["ControlLinkHelp"] = ("模拟器只占用独立控制接口；业务程序独占打开设备列表中显示的 COM 端口。", "The simulator uses a separate control link. The client app opens the COM port shown above."),
            ["PortSettings"] = ("模拟串口参数", "Port settings"),
            ["BaudRate"] = ("波特率", "Baud rate"),
            ["DataBits"] = ("数据位", "Data bits"),
            ["Parity"] = ("校验位", "Parity"),
            ["None"] = ("无", "None"),
            ["Odd"] = ("奇校验", "Odd"),
            ["Even"] = ("偶校验", "Even"),
            ["StopBits"] = ("停止位", "Stop bits"),
            ["ApplySettings"] = ("应用参数", "Apply"),
            ["LimitBaudRate"] = ("按波特率限制双向传输速度", "Limit both directions to the baud rate"),
            ["AllowCts"] = ("CTS 允许业务端发送", "Allow client to send (CTS)"),
            ["ClientLinesHelp"] = ("业务控制线：打开控制通道后读取", "Open the control link to read client lines."),
            ["SelectDeviceHelp"] = ("选择设备后可读取并设置模拟串口参数。", "Select a device to view or change its port settings."),
            ["ManageDevices"] = ("设备管理", "Manage devices"),
            ["AdminHelp"] = ("新建、更新和删除操作需要管理员权限；驱动资源已内嵌在管理员程序中。", "Adding or removing a device or updating its driver requires admin access. The admin tool includes the driver."),
            ["AddDevice"] = ("新建设备", "Add device"),
            ["UpdateDriver"] = ("更新驱动", "Update driver"),
            ["RemoveDevice"] = ("删除选中设备", "Remove device"),
            ["SendReceive"] = ("模拟与收发", "Send & receive"),
            ["SimulatedData"] = ("模拟数据", "Simulated data"),
            ["NoTarget"] = ("注入目标：未连接", "Target: Not connected"),
            ["DataType"] = ("数据模式", "Data type"),
            ["Counter"] = ("递增计数", "Counter"),
            ["SineWave"] = ("正弦波", "Sine wave"),
            ["Random"] = ("随机值", "Random"),
            ["FixedText"] = ("固定文本", "Fixed text"),
            ["Interval"] = ("发送间隔（毫秒）", "Interval (ms)"),
            ["MessageFormat"] = ("消息模板", "Message format"),
            ["EnterMessageFormat"] = ("输入消息模板或固定文本", "Enter a format or fixed text"),
            ["TemplateHelp"] = ("支持 {timestamp}、{counter}、{value}；数据按 UTF-8 注入。", "Use {timestamp}, {counter}, or {value}. Text is sent as UTF-8."),
            ["LineEnding"] = ("数据末尾", "Line ending"),
            ["StartSending"] = ("开始周期注入", "Start"),
            ["Stop"] = ("停止注入", "Stop"),
            ["SendOnce"] = ("注入一次", "Send once"),
            ["ResetCount"] = ("重置计数", "Reset"),
            ["ReplyRules"] = ("请求响应规则", "Reply rules"),
            ["ReplyRuleHint"] = ("每行一条：请求=响应", "One per line: request=reply"),
            ["ReplyRuleHelp"] = ("业务端每行请求以 CRLF 结尾才触发匹配；响应末尾自动添加 CRLF。支持 \\r、\\n 转义。", "End each request with CRLF to match a rule. Replies end with CRLF. You can use \\r and \\n."),
            ["Traffic"] = ("收发数据", "Traffic"),
            ["ShowTime"] = ("显示时间戳", "Show time"),
            ["ClearLog"] = ("清空记录", "Clear log"),
            ["Disconnected"] = ("未连接", "Not connected"),
            ["ClientTitle"] = ("串口模拟器 · 业务测试端", "Serial Port Test Client"),
            ["ClientIntro"] = ("通过 COM 口连接模拟设备，验证串口参数与收发行为", "Connect to a COM port to test settings and send or receive data."),
            ["SelectPort"] = ("选择串口设备", "Select a port"),
            ["OpenPort"] = ("打开端口", "Open port"),
            ["RtsHelp"] = ("RTS/CTS 流控开启时由串口栈自动控制", "The serial driver controls RTS when flow control is on."),
            ["FlowControl"] = ("RTS/CTS 流控", "RTS/CTS flow control"),
            ["FlowControlHelp"] = ("开启后由串口栈自动控制 RTS，CTS 由模拟器端控制", "When on, the serial driver sets RTS and the simulator sets CTS."),
            ["Hex"] = ("十六进制", "Hex"),
            ["Timestamp"] = ("时间戳", "Show time"),
            ["SendHint"] = ("输入内容，Ctrl+Enter 发送", "Type a message. Press Ctrl+Enter to send."),
            ["Send"] = ("发送", "Send")
        };

    private static bool _useEnglish = ReadPreference();

    public static event Action? Changed;

    public static string Text(string chinese, string english) => _useEnglish ? english : chinese;

    public static string Get(string key) => Strings.TryGetValue(key, out var text)
        ? (_useEnglish ? text.English : text.Chinese)
        : key;

    public static void SavePreference(bool useEnglish)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
        File.WriteAllText(PreferencePath, useEnglish ? "en" : "zh-CN");
    }

    public static void ApplyLanguage(bool useEnglish)
    {
        if (_useEnglish == useEnglish) return;
        _useEnglish = useEnglish;
        Changed?.Invoke();
    }

    private static bool ReadPreference()
    {
        try
        {
            return File.ReadAllText(PreferencePath).Trim() == "en";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
