using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SerialPortSimulator.Controls;
using SerialPortSimulator.Services;

namespace SerialPortSimulator;

public sealed partial class MainWindow : Window
{
    private readonly SelfResourceMonitor _resourceMonitor = new();
    private readonly DispatcherQueueTimer _resourceTimer;
    private bool _deviceManagementBusy;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Title = UiLanguage.Get("SimulatorTitle");
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        TitleBarChrome.Attach(this, TitleBarRoot, TitleBarDragRegion, CaptionInsetColumn);
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 900));
        _resourceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _resourceTimer.Interval = TimeSpan.FromSeconds(1);
        _resourceTimer.Tick += (_, _) => UpdateResourceText();
        _resourceTimer.Start();
        Closed += MainWindow_Closed;
        UiLanguage.Changed += UiLanguage_Changed;
        InitializeDeviceTabs();
    }

    private async void LanguageButton_Click(object sender, RoutedEventArgs e) =>
        await UiLanguage.PromptForChangeAsync(((FrameworkElement)sender).XamlRoot);

    private void UiLanguage_Changed()
    {
        if (_closing) return;
        if (DispatcherQueue.HasThreadAccess) RefreshLocalizedUi();
        else DispatcherQueue.TryEnqueue(RefreshLocalizedUi);
    }

    private void RefreshLocalizedUi()
    {
        if (_closing) return;
        UiText.RefreshAll();
        Title = UiLanguage.Get("SimulatorTitle");
        AutomationProperties.SetName(LanguageButton, UiLanguage.Get("SwitchLanguage"));
        foreach (TabViewItem item in DeviceTabs.TabItems)
            if (item.Content is DeviceTabView view) view.RefreshLocalizedText();
        UpdateTitleDeviceText();
        SetStatus(UiLanguage.Text("界面已切换为中文，语言设置已保存。", "UI language switched to English. Setting saved."));
    }

    private void InitializeDeviceTabs()
    {
        if (!RefreshDeviceTabs(selectNewest: false)) return;
        if (DeviceTabs.TabItems.Count > 0)
        {
            SetStatus(UiLanguage.Text("选择设备标签页并打开控制通道。", "Select a device tab and open its control link."));
            return;
        }

        SetStatus(UiLanguage.Text("未发现虚拟设备，正在请求创建…", "No virtual device found. Requesting device creation…"));
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RunDeviceManagerAsync("Create"));
    }

    private bool RefreshDeviceTabs(bool selectNewest)
    {
        IReadOnlyList<VirtualDeviceInfo> devices;
        try
        {
            devices = VirtualDeviceClient.EnumerateDevices();
        }
        catch (Exception ex)
        {
            SetStatus(UiLanguage.Text($"枚举虚拟设备失败：{ex.Message}", $"Could not list virtual devices: {ex.Message}"), true);
            return false;
        }

        var existing = new Dictionary<string, TabViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (TabViewItem item in DeviceTabs.TabItems)
            if (item.Content is DeviceTabView view) existing[view.Device.InstanceId] = item;

        var added = new List<TabViewItem>();
        foreach (VirtualDeviceInfo device in devices)
        {
            if (existing.TryGetValue(device.InstanceId, out TabViewItem? item))
            {
                if (item.Content is DeviceTabView view && view.Device != device)
                {
                    view.UpdateDevice(device);
                    item.Header = device.PortName;
                }
                continue;
            }
            added.Add(AddDeviceTab(device));
        }

        if (selectNewest && added.Count > 0)
            DeviceTabs.SelectedItem = added[^1];
        else if (DeviceTabs.SelectedItem is null && DeviceTabs.TabItems.Count > 0)
            DeviceTabs.SelectedItem = DeviceTabs.TabItems[0];

        UpdateTitleDeviceText();
        return true;
    }

    private TabViewItem AddDeviceTab(VirtualDeviceInfo device)
    {
        var view = new DeviceTabView(device);
        view.StatusChanged += OnDeviceTabStatusChanged;
        view.UpdateDriverRequested += OnDeviceTabUpdateDriverRequested;
        view.SetDeviceManagementBusy(_deviceManagementBusy);
        var item = new TabViewItem
        {
            Header = device.PortName,
            Content = view,
            IsClosable = true
        };
        DeviceTabs.TabItems.Add(item);
        return item;
    }

    private void OnDeviceTabStatusChanged(string message, bool error)
    {
        if (!_closing) SetStatus(message, error);
    }

    private async void OnDeviceTabUpdateDriverRequested(DeviceTabView view)
    {
        if (_closing || _deviceManagementBusy) return;
        if (AnyChannelOpen())
        {
            SetStatus(UiLanguage.Text("更新驱动前请先关闭所有控制通道。", "Close all control links before updating the driver."), true);
            return;
        }
        await RunDeviceManagerAsync("Update");
    }

    private bool AnyChannelOpen()
    {
        foreach (TabViewItem item in DeviceTabs.TabItems)
            if (item.Content is DeviceTabView view && view.IsChannelOpen) return true;
        return false;
    }

    private void DeviceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTitleDeviceText();

    private void UpdateTitleDeviceText()
    {
        TitleDeviceText.Text = DeviceTabs.SelectedItem is TabViewItem item && item.Content is DeviceTabView view
            ? $"{view.Device.PortName} · {view.Device.InstanceId}"
            : string.Empty;
    }

    private async void CreateDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceManagementBusy) return;
        await RunDeviceManagerAsync("Create");
    }

    private async void DeviceTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (_closing) return;
        if (_deviceManagementBusy)
        {
            SetStatus(UiLanguage.Text("设备管理操作进行中，请稍候。", "A device operation is in progress."), true);
            return;
        }
        if (args.Tab.Content is not DeviceTabView view) return;

        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = UiLanguage.Text("删除虚拟设备", "Remove virtual device"),
            Content = UiLanguage.Text(
                $"将删除 {view.Device.PortName}（{view.Device.InstanceId}）。控制通道会先关闭，业务端随后无法再打开该端口。如果这是最后一个本产品设备，还会卸载驱动包并移除测试证书。",
                $"Remove {view.Device.PortName} ({view.Device.InstanceId})? The control link will be closed and the client will lose access to this port. If this is the last simulator device, the driver package and test certificate are removed too."),
            PrimaryButtonText = UiLanguage.Text("删除", "Remove"),
            CloseButtonText = UiLanguage.Text("取消", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await view.CloseChannelAsync();
        if (!await RunDeviceManagerAsync("Remove", view.Device.InstanceId)) return;

        await view.ShutdownAsync();
        sender.TabItems.Remove(args.Tab);
        if (DeviceTabs.SelectedItem is null && DeviceTabs.TabItems.Count > 0)
            DeviceTabs.SelectedItem = DeviceTabs.TabItems[0];
        UpdateTitleDeviceText();
        SetStatus(UiLanguage.Text("虚拟设备已删除", "Virtual device removed"));
    }

    private async Task<bool> RunDeviceManagerAsync(string action, string? instanceId = null)
    {
        string? helperPath = FindDeviceManagerExecutable();
        if (helperPath is null)
        {
            SetStatus(
                UiLanguage.Text("找不到 SerialPortSimulator.DeviceManager.exe。请确认整个 ZIP 已解压，", "SerialPortSimulator.DeviceManager.exe is missing. Extract the full ZIP. ") +
                UiLanguage.Text("且管理员程序位于主程序目录下的 DeviceManager 文件夹。", "The DeviceManager folder must be next to the main app."),
                true);
            return false;
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
        UpdateDeviceManagementControls();
        try
        {
            SetStatus(GetDeviceManagerProgress(action));
            using Process? process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException(UiLanguage.Text("无法启动管理员进程。", "Could not start the admin tool."));
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                if (action == "Remove" && process.ExitCode == 15)
                {
                    SetStatus(UiLanguage.Text("虚拟设备已不存在，已从标签栏移除。", "The virtual device no longer exists."));
                    return true;
                }
                throw new InvalidOperationException(GetDeviceManagerError(process.ExitCode));
            }

            if (action == "Create")
            {
                RefreshDeviceTabs(selectNewest: true);
                if (DeviceTabs.TabItems.Count == 0)
                    SetStatus(UiLanguage.Text("驱动操作已完成，但设备控制接口尚未加载。请在设备管理器查看本产品虚拟串口的设备状态。", "The driver operation finished, but the control link is unavailable. Check the port status in Device Manager."), true);
                else
                    SetStatus(GetDeviceManagerSuccess(action));
            }
            else if (action == "Update")
            {
                RefreshDeviceTabs(selectNewest: false);
                foreach (TabViewItem item in DeviceTabs.TabItems)
                    if (item.Content is DeviceTabView view) view.ReloadSerialConfig();
                SetStatus(GetDeviceManagerSuccess(action));
            }
            else
            {
                SetStatus(GetDeviceManagerSuccess(action));
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus(UiLanguage.Text("管理员授权已取消", "Admin request canceled"));
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(UiLanguage.Text($"设备管理失败：{ex.Message}", $"Device operation failed: {ex.Message}"), true);
            return false;
        }
        finally
        {
            _deviceManagementBusy = false;
            UpdateDeviceManagementControls();
        }
    }

    private void UpdateDeviceManagementControls()
    {
        CreateDeviceButton.IsEnabled = !_deviceManagementBusy && !_closing;
        foreach (TabViewItem item in DeviceTabs.TabItems)
            if (item.Content is DeviceTabView view) view.SetDeviceManagementBusy(_deviceManagementBusy);
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

    private void SetStatus(string message, bool error = false) =>
        StatusText.Text = error ? UiLanguage.Text($"错误：{message}", $"Error: {message}") : message;

    private void UpdateResourceText()
    {
        if (_closing) return;
        _resourceMonitor.Sample();
        ResourceText.Text = $"CPU {_resourceMonitor.CpuPercent:F1}% · MEM {_resourceMonitor.WorkingSetMB} MB";
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        UiLanguage.Changed -= UiLanguage_Changed;
        _resourceTimer.Stop();
        foreach (TabViewItem item in DeviceTabs.TabItems)
            if (item.Content is DeviceTabView view) _ = view.ShutdownAsync();
    }
}
