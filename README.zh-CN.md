# 串口设备模拟器

[English](README.md) | [中文](README.zh-CN.md)

基于 Microsoft WinUI 3 的 Windows 桌面程序，用于创建和管理虚拟 COM 设备、注入模拟数据，并响应业务程序发送的串口请求。

## 功能

- 枚举、创建和删除本产品创建的虚拟 COM 设备，不会操作物理串口或其他厂商设备。
- 模拟器使用独立控制接口，业务程序可以独占打开 COM 端口。
- 模拟器是串口参数的权威来源，可设置波特率、数据位、校验位和停止位；业务端打开端口时发送的参数不会覆盖模拟器配置。
- 可选按波特率和帧格式限制双向字节吞吐；关闭时保持即时内存传输。模拟器可切换 CTS，业务端启用 RTS/CTS 握手后，CTS 无效会暂停业务端发送队列。
- 主界面分为“虚拟设备”和“模拟与收发”两个标签页；所有卡片使用自动换行、自动补齐空位的流动布局。
- 周期或单次注入 UTF-8 字节，支持递增计数、正弦波、随机值和固定文本。
- 模板变量：`{timestamp}`、`{counter}`、`{value}`；数据末尾可选无、CRLF、LF 或 CR，周期发送时可实时切换。
- 按 CRLF 分帧并精确匹配 `请求=响应` 规则，自动回复业务端请求。
- 显示业务端写入、模拟器注入和自动响应记录，最多保留 200 条。
- 模拟页显示当前注入目标 COM，收发记录和运行日志会带上端口名。
- 提供独立的 WinUI 3 `TestClient\SerialPortSimulator.TestClient.exe`，用于手动选择或输入串口参数、打开 COM、接收注入数据、发送请求和查看十六进制内容；测试端不读取模拟器控制接口或串口参数，端口列表自动刷新。收发数据与模拟器使用一致的 `RX/TX [字节数 B] 内容` 格式，最新记录显示在顶部，时间戳可选；完整诊断异常写入该程序所在目录的 `Logs\test-client.log`，接收使用 50 ms 轮询。

## 构建

需要 Windows 10 1809 或更新版本、.NET 10 SDK、Visual Studio C++ 与 WDK 工具集。

仓库根目录的 `SerialPortSimulator.slnx` 统一组织 `SerialPortSimulator`、`TestClient`、`DeviceManager`、`DriverSmoke` 和 `VirtualSerial` 五个平级项目目录。Visual Studio 使用 `Debug|x64` 配置构建完整解决方案；命令行也可以单独构建各项目。

```powershell
.\tools\Build-Driver.ps1 -Configuration Debug -TestSign
dotnet restore .\SerialPortSimulator\SerialPortSimulator.csproj
dotnet build .\SerialPortSimulator\SerialPortSimulator.csproj -c Debug -p:Platform=x64
```

`-TestSign` 会在本机生成或复用 `tools\.certs\SerialPortSimulatorTest.pfx`，用它签名 `wudf.cat`，并把对应的公钥证书复制到驱动输出目录。私钥和密码只保存在本机，且已被 `.gitignore` 忽略；发行包只携带公钥证书。`Package-Release.ps1` 会拒绝打包未签名或证书与 CAT 不匹配的驱动。

管理员设备管理程序内嵌驱动 DLL、INF、CAT 和测试证书，并通过 Windows SetupAPI / Configuration Manager 完成设备创建、驱动更新和删除。运行程序后点击“新建设备”即可完成首次驱动安装和设备创建，Windows 会显示 UAC 授权提示；辅助程序会将内嵌文件暂存到 `C:\ProgramData\SerialPortSimulator\DevicePackage`。

设备管理操作使用 `DevicePackage` 下的独立暂存目录；操作结束后会删除本次解压的驱动文件。新版辅助程序也会清理旧版本遗留在 `DevicePackage` 根目录的同名文件。删除最后一个本产品虚拟设备后，还会卸载匹配本产品硬件 ID 的驱动包，并移除内嵌测试证书。发行包不再携带 WDK 的 `devcon.exe`。

客户端必须完整解压 ZIP。主程序位于解压目录根部，管理员辅助程序位于 `DeviceManager` 子目录，业务测试端位于 `TestClient` 子目录。

生成完整便携包：

```powershell
.\tools\Package-Release.ps1
```

示例：`.\tools\Package-Release.ps1 -Version 1.0.0 -SkipDriverBuild`。离线且已还原依赖时可加 `-NoRestore`。输出位于 `dist`；三个程序分别发布为自包含单文件 EXE，驱动包仍嵌入管理员程序。单文件运行时会解压原生依赖。

设备新建/删除由 `SerialPortSimulator.DeviceManager.exe` 在独立管理员进程中完成。应用当前使用测试签名驱动，生产交付前需要换用公司代码签名证书。

如果安装日志出现 `0x800B0100`、`No signature was present` 或“驱动包未签名”，说明 CAT 未签名或证书不匹配。重新运行 `.\tools\Build-Driver.ps1 -Configuration Debug -TestSign` 后再生成发行包；当前测试签名驱动还要求目标机允许测试签名或信任随包证书。

普通业务程序打开 COM 口后，应使用 Windows `GetCommState` 或标准串口查询 IOCTL 读取模拟器当前参数，再按查询结果解析数据。不要假设打开端口时传入的 `9600 8N1` 等默认值就是实际参数。本项目的测试端为了验证普通串口程序行为，不读取模拟器控制接口，而是由用户在界面上选择或输入波特率、数据位、校验位和停止位后直接打开端口；模拟器内部参数仍以模拟器设置为准。

测试端的 DTR/RTS 在打开端口时写入业务侧输出线。驱动协议版本 4 会在模拟器“模拟串口参数”卡片中显示 DTR、RTS 和 RTS/CTS 流控状态；勾选测试端“RTS/CTS 流控”后使用 `Handshake.RequestToSend`，RTS 由串口栈自动控制，CTS 由模拟器的“CTS 允许业务端发送”控制。关闭流控时，DTR/RTS 为手工静态电平。

## 运行与测试

```powershell
.\SerialPortSimulator\bin\x64\Debug\net10.0-windows10.0.19041.0\SerialPortSimulator.exe
.\tools\Test-VirtualDevice.ps1
```

端到端测试覆盖业务端独占打开 COM、控制通道并行工作、UTF-8 注入、业务写入和 CRLF 自动响应。详细实现和当前验证状态见 [docs/virtual-device-implementation.md](docs/virtual-device-implementation.md)。

更新到协议版本 4 的驱动后，可用 `dotnet run --project .\DriverSmoke\DriverSmoke.csproj -- --line-test` 验证线路限速和 CTS 暂停/恢复；DTR/RTS 和 RTS/CTS 流控状态可在模拟器参数卡片中观察。软件模拟受 Windows 定时器精度影响，无法产生真实 RS-232/TTL 电压或硬件 TXD/RXD 波形。

串口概念、工作原理、数据流程和缓存策略见 [docs/serial-port-knowledge.md](docs/serial-port-knowledge.md)。

打开 [docs/serial-communication-animation.html](docs/serial-communication-animation.html) 可查看按真实串口时间高倍率放慢的连续字节流交互动画；每个实际问题只对应一个明确解决方案。

运行诊断日志固定写入主程序旁的 `Logs\runtime.log`。便携包必须解压到可写目录；如果程序目录只读，日志不会改写到其他位置。

## 用户电脑上的文件

| 位置 | 内容与清理方式 |
|---|---|
| 解压应用的目录 | 三个单文件程序及文档；主程序和测试端日志在 `Logs`，管理员日志在 `DeviceManager\Logs\device-manager.log`。日志各最多约 1 MiB，崩溃日志只保留最近一次异常。卸载设备后可删除整个解压目录。 |
| `%ProgramData%\SerialPortSimulator\DevicePackage` | 管理员驱动操作的暂存目录，不写日志。进程结束时清理暂存文件；只有清理失败时才保留供排查。 |
| `%TEMP%\.net\SerialPortSimulator` 等本产品子目录 | 单文件 EXE 运行时的 .NET 原生库缓存，不是日志。程序退出后可删除本产品专属子目录；不要删除整个 `%TEMP%\.net`。删除程序目录不会自动清除这些缓存。 |

Windows 的驱动存储、虚拟设备注册信息以及导入到系统信任存储区的测试证书是安装状态，不属于临时文件。请先在程序里删除全部本产品设备：删除最后一个设备时会尝试清理本产品驱动包和测试证书；仅删除 ZIP 或解压目录不会卸载这些系统组件。

业务端联调时同时运行模拟器和 `TestClient\SerialPortSimulator.TestClient.exe`，在测试程序中选择同一 COM 端口、设置波特率/数据位/校验位/停止位并点击“打开端口”。

## 许可证

本项目采用分范围许可：

- 除 `VirtualSerial/` 外的自有代码采用 **PolyForm Noncommercial 1.0.0**，见 [LICENSE](LICENSE)；中文参考翻译见 [LICENSE.zh-CN.md](LICENSE.zh-CN.md)。该许可允许非商业使用，禁止商业使用；它是 **Source Available（源码可见）** 许可，不属于 OSI 定义的开源许可证。
- `VirtualSerial/` 基于 Microsoft Windows-driver-samples 的 VirtualSerial2 示例，采用 **Microsoft Public License (MS-PL)**，见 [VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt)。MS-PL 允许商业使用、修改和再分发；本项目的非商用条款不适用于该目录及其编译产物。
- 商业授权请通过项目仓库 Issue 或维护者主页联系。第三方组件声明见 [THIRD-PARTY-NOTICES.zh-CN.md](THIRD-PARTY-NOTICES.zh-CN.md)；英文声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)，英文说明见 [README.md](README.md)。
