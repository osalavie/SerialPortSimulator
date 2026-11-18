using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialPortSimulator.Services;

internal static partial class UiLanguage
{
    public static void AddResources(ResourceDictionary resources)
    {
        resources["SwitchLanguage"] = Text("English", "中文");
        resources["SimulatorTitle"] = Text("串口设备模拟器", "Serial Port Simulator");
        resources["SimulatorIntro"] = Text("模拟器定义串口参数，业务端通过 COM 口连接并按设备参数适配", "Set the port here and use the same settings in the client app.");
        resources["VirtualDevice"] = Text("虚拟设备", "Virtual device");
        resources["Connection"] = Text("设备连接", "Connection");
        resources["SelectVirtualPort"] = Text("选择本产品的虚拟串口", "Select a virtual port");
        resources["Refresh"] = Text("刷新设备", "Refresh");
        resources["OpenControlLink"] = Text("打开控制通道", "Open control link");
        resources["ClientPort"] = Text("业务端口", "Client port");
        resources["NoneSelected"] = Text("未选择", "None selected");
        resources["DeviceId"] = Text("设备实例", "Device ID");
        resources["Protocol"] = Text("控制协议", "Protocol");
        resources["NotRead"] = Text("未读取", "Not read");
        resources["ControlLinkHelp"] = Text("模拟器只占用独立控制接口；业务程序独占打开设备列表中显示的 COM 端口。", "The simulator uses a separate control link. The client app opens the COM port shown above.");
        resources["PortSettings"] = Text("模拟串口参数", "Port settings");
        resources["BaudRate"] = Text("波特率", "Baud rate");
        resources["DataBits"] = Text("数据位", "Data bits");
        resources["Parity"] = Text("校验位", "Parity");
        resources["None"] = Text("无", "None");
        resources["Odd"] = Text("奇校验", "Odd");
        resources["Even"] = Text("偶校验", "Even");
        resources["StopBits"] = Text("停止位", "Stop bits");
        resources["ApplySettings"] = Text("应用参数", "Apply");
        resources["LimitBaudRate"] = Text("按波特率限制双向传输速度", "Limit both directions to the baud rate");
        resources["AllowCts"] = Text("CTS 允许业务端发送", "Allow client to send (CTS)");
        resources["ApplyCts"] = Text("应用 CTS", "Apply CTS");
        resources["ClientLinesHelp"] = Text("业务控制线：打开控制通道后读取", "Open the control link to read client lines.");
        resources["SelectDeviceHelp"] = Text("选择设备后可读取并设置模拟串口参数。", "Select a device to view or change its port settings.");
        resources["ManageDevices"] = Text("设备管理", "Manage devices");
        resources["AdminHelp"] = Text("新建、更新和删除操作需要管理员权限；驱动资源已内嵌在管理员程序中。", "Adding or removing a device or updating its driver requires admin access. The admin tool includes the driver.");
        resources["AddDevice"] = Text("新建设备", "Add device");
        resources["UpdateDriver"] = Text("更新驱动", "Update driver");
        resources["RemoveDevice"] = Text("删除选中设备", "Remove device");
        resources["SendReceive"] = Text("模拟与收发", "Send & receive");
        resources["SimulatedData"] = Text("模拟数据", "Simulated data");
        resources["NoTarget"] = Text("注入目标：未连接", "Target: Not connected");
        resources["DataType"] = Text("数据模式", "Data type");
        resources["Counter"] = Text("递增计数", "Counter");
        resources["SineWave"] = Text("正弦波", "Sine wave");
        resources["Random"] = Text("随机值", "Random");
        resources["FixedText"] = Text("固定文本", "Fixed text");
        resources["Interval"] = Text("发送间隔（毫秒）", "Interval (ms)");
        resources["MessageFormat"] = Text("消息模板", "Message format");
        resources["EnterMessageFormat"] = Text("输入消息模板或固定文本", "Enter a format or fixed text");
        resources["TemplateHelp"] = Text("支持 {timestamp}、{counter}、{value}；数据按 UTF-8 注入。", "Use {timestamp}, {counter}, or {value}. Text is sent as UTF-8.");
        resources["LineEnding"] = Text("数据末尾", "Line ending");
        resources["StartSending"] = Text("开始周期注入", "Start");
        resources["Stop"] = Text("停止注入", "Stop");
        resources["SendOnce"] = Text("注入一次", "Send once");
        resources["ResetCount"] = Text("重置计数", "Reset");
        resources["ReplyRules"] = Text("请求响应规则", "Reply rules");
        resources["ReplyRuleHint"] = Text("每行一条：请求=响应", "One per line: request=reply");
        resources["ReplyRuleHelp"] = Text("业务端每行请求以 CRLF 结尾才触发匹配；响应末尾自动添加 CRLF。支持 \\r、\\n 转义。", "End each request with CRLF to match a rule. Replies end with CRLF. You can use \\r and \\n.");
        resources["Traffic"] = Text("收发数据", "Traffic");
        resources["ShowTime"] = Text("显示时间戳", "Show time");
        resources["ClearLog"] = Text("清空记录", "Clear log");
        resources["Disconnected"] = Text("未连接", "Not connected");
        resources["ClientTitle"] = Text("串口模拟器 · 业务测试端", "Serial Port Test Client");
        resources["ClientIntro"] = Text("通过 COM 口连接模拟设备，验证串口参数与收发行为", "Connect to a COM port to test settings and send or receive data.");
        resources["SelectPort"] = Text("选择串口设备", "Select a port");
        resources["OpenPort"] = Text("打开端口", "Open port");
        resources["RtsHelp"] = Text("RTS/CTS 流控开启时由串口栈自动控制", "The serial driver controls RTS when flow control is on.");
        resources["FlowControl"] = Text("RTS/CTS 流控", "RTS/CTS flow control");
        resources["FlowControlHelp"] = Text("开启后由串口栈自动控制 RTS，CTS 由模拟器端控制", "When on, the serial driver sets RTS and the simulator sets CTS.");
        resources["Hex"] = Text("十六进制", "Hex");
        resources["Timestamp"] = Text("时间戳", "Show time");
        resources["SendHint"] = Text("输入内容，Ctrl+Enter 发送", "Type a message. Press Ctrl+Enter to send.");
        resources["Send"] = Text("发送", "Send");
    }

    public static async Task PromptForChangeAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Text("切换为英文", "Switch to Chinese"),
            Content = Text("保存后，重新打开模拟器或测试端即可使用英文。", "Save, then reopen the simulator or test client to use Chinese."),
            PrimaryButtonText = Text("保存", "Save"),
            CloseButtonText = Text("取消", "Cancel")
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
                File.WriteAllText(PreferencePath, UseEnglish ? "zh-CN" : "en");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                dialog.Content = Text("无法保存语言设置，请重试。", "Could not save the language. Try again.");
            }
        };
        await dialog.ShowAsync();
    }
}
