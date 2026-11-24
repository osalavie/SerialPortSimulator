# 虚拟设备实现

## 目标

应用安装并管理本产品创建的虚拟 COM 设备。业务程序通过标准 COM 口读写，模拟器通过独立设备接口注入数据并读取业务写入，不需要占用业务端的 COM 句柄。

## 已实现

- UMDF 2 虚拟串口驱动，公开 `GUID_DEVINTERFACE_COMPORT` 和独立控制接口 `878a35e1-b89e-4a11-9145-8d4b3e7c1a22`。
- 控制协议版本 1：
  - `IOCTL_SIM_INJECT`：原始字节写入业务接收缓冲区。
  - `IOCTL_SIM_RECEIVE`：非阻塞读取业务端已写入数据。
  - `IOCTL_SIM_VERSION`：返回协议版本。
- 控制协议版本 2 增加：
  - `IOCTL_SIM_GET_SERIAL_CONFIG`：读取模拟器权威串口参数。
  - `IOCTL_SIM_SET_SERIAL_CONFIG`：设置波特率、数据位、校验位、停止位。
- 控制协议版本 3 增加：
  - 串口配置的 `FlowControl` 位 0 控制双向按波特率限速。驱动每 10 ms 根据起始位、数据位、校验位、停止位计算传输额度，从待传输队列释放字节；两个方向独立计时和缓存。
  - `IOCTL_SIM_GET_MODEM_LINES` 与 `IOCTL_SIM_SET_MODEM_LINES` 让模拟器读取和设置 CTS、DSR、DCD 状态。标准 `GET_MODEMSTATUS` 返回这些状态。
  - 标准 `SET_HANDFLOW`/`GET_HANDFLOW` 保存业务端设置；启用 CTS 握手且 CTS 无效时，业务端写入的数据保留在待传输队列，CTS 恢复后继续释放。
- 控制协议版本 4 增加：
  - `IOCTL_SIM_GET_LINE_STATE` 返回 CTS/DSR/DCD 输入线、业务侧 DTR/RTS 输出线和 RTS/CTS 握手状态。
  - 标准 `SET_DTR`/`CLR_DTR`/`SET_RTS`/`CLR_RTS` 和 `SET_MODEM_CONTROL` 更新并保存 DTR/RTS，供模拟器观测。
- 模拟器拥有串口参数所有权。标准 `IOCTL_SERIAL_SET_BAUD_RATE` 和 `IOCTL_SERIAL_SET_LINE_CONTROL` 保持兼容成功返回，但不会覆盖模拟器配置。
- 标准 `GET_BAUD_RATE`、`GET_LINE_CONTROL` 返回模拟器当前参数，业务端应在打开端口后查询。
- 注入缓冲区容量为 65535 字节；空间不足时返回错误，不静默截断。
- 开启线路限速后，两个方向各增加一个 65535 字节待传输队列；`PURGE_RXCLEAR` 和 `PURGE_TXCLEAR` 会同时清除对应方向的可读队列与待传输队列。
- 主界面枚举本产品设备，并从设备实例读取 `PortName`，显示业务端口。
- 主界面使用“虚拟设备”和“模拟与收发”两个标签页组织设备配置与运行操作。
- 两个标签页使用自定义 `FlowPanel`：按可用宽度计算等宽列数，卡片自动换行；设备连接卡占满整行，模拟数据和规则并排，收发记录占满下一行。
- 界面支持周期/单次注入、UTF-8 编码、计数器重置和最多 200 条收发数据；数据卡片使用 180 像素高的可滚动区域，最新记录显示在最上方。卡片显示 RX/TX 方向、实际字节数和数据内容，时间戳可在卡片中开关并即时重排已有记录；连接状态、参数变更及错误写入主程序旁的 `Logs\runtime.log`。
- 模拟数据末尾可选择无、CRLF、LF 或 CR；周期注入的每条消息按发送时的当前选项生成，运行中切换从下一条消息生效。
- 界面支持设备在线读取和应用波特率、数据位、校验位、停止位。
- 界面可开启双向线路限速、切换 CTS 状态并显示业务侧 DTR/RTS；线路限速和 CTS 要求协议版本 3，DTR/RTS 观测要求协议版本 4。
- 模拟页显示当前注入目标 COM，诊断日志固定写入程序目录的 `Logs`；便携包应解压到可写目录，不再自动回退到其他位置。
- 独立 WinUI 3 业务测试端不读取模拟器控制接口，由用户选择或输入波特率、数据位、校验位和停止位后直接打开端口；支持文本/十六进制接收和发送，并打印完整的打开失败异常。
- 业务测试端与模拟器使用一致的收发格式 `RX/TX [字节数 B] 内容`，最多显示 200 条，最新记录在顶部；时间戳和十六进制显示可即时切换，诊断错误留在状态区及自动化日志。
- 业务测试端使用 50 ms 轮询接收，支持 DTR、RTS 手工输出线以及可选的 RTS/CTS 硬件流控。
- 请求响应规则按行配置为 `请求=响应`，支持 `\r`、`\n` 转义。
- `RequestResponder` 支持 CRLF 分帧、跨读取分片、多条请求粘连、UTF-8 多字节拆分、超长帧丢弃和未知请求。
- 管理员辅助程序将驱动 DLL、INF、CAT 和测试证书作为嵌入资源打包，使用 SetupAPI / Configuration Manager 完成设备操作。
- 主程序通过独立的 `SerialPortSimulator.DeviceManager.exe` 新建、更新或删除设备。
- 管理员辅助程序使用 UAC 清单按需提权，负责解压嵌入式驱动包、证书导入、文件校验、设备操作、硬件 ID 校验和日志。
- 管理员辅助程序为每次操作建立独立的驱动暂存目录，操作完成或失败后清理本次文件；日志统一限制为约 1 MiB，主程序与业务测试端的运行日志也有相同上限。
- 删除前校验硬件 ID 必须为 `ROOT\SerialPortSimulator`，不会删除物理串口或其他厂商设备。
- 创建设备前会尽力清理本产品未启动的旧设备节点；清理使用不限定设备类别的设备信息集，避免安装失败的旧节点因类别不匹配而无法清理。节点若在枚举后消失或清理仍失败，不会阻断新设备创建。
- 删除最后一个本产品设备后，辅助程序卸载匹配硬件 ID、提供者和 Ports 类的 OEM 驱动包，并从系统证书存储中移除内嵌测试证书。驱动暂存文件在辅助进程结束时清理。

## 验证状态

`2026-11-17` 在本机完成：

- 驱动 Debug x64 构建、测试签名和首次安装成功，设备为 `COM3`。
- 协议版本 3 的驱动和主程序已构建成功；新增线路限速与 CTS 的实际端到端测试需更新已安装驱动后运行 `DriverSmoke --line-test`。
- 首个线路模拟发布包曾使用 UMDF 不兼容的周期性被动定时器，设备出现 `CM_PROB_FAILED_ADD`（Code 31）。现已改为单次被动定时器并重新构建签名；原发布包已移除。当前已安装设备仍需用修复版更新，更新后再执行运行时验证。
- 控制接口枚举、协议版本、控制端注入和业务端原始读取通过。
- 业务端原始写入 `PING\r\n`、控制端读取、`RequestResponder` 自动回复 `PONG\r\n` 通过。
- WinUI 主程序构建无警告，启动冒烟通过。
- 更新测试签名驱动后，`System.IO.Ports.SerialPort` 可独占打开；控制通道同时工作。
- 端到端测试使用唯一载荷，清除残留缓冲区后验证 UTF-8 注入、业务写入、动态 `PING-*`/`PONG-*` 自动响应。
- `tools/Test-VirtualDevice.ps1` 连续运行两次均通过。
- 模拟器设置为 `19200 7E2`、业务端以 `9600 8N1` 打开后，标准串口查询仍返回 `19200 7E2`，数据收发和自动响应正常。
- 便携包使用自包含单文件发布；运行时会在 `%TEMP%\.net` 解压原生依赖，关闭程序后可清理本产品专属缓存目录。
- 发布包只引用 Windows App SDK 的 WinUI 和 DWrite 组件，排除未使用的 AI、ML、Search、Widgets 运行时；主程序和业务测试端启用单文件压缩，管理员辅助程序使用裁剪发布，以减小便携包体积。
- 管理员辅助程序成功从嵌入资源解压驱动到 `C:\ProgramData\SerialPortSimulator\DevicePackage` 并完成驱动更新。
- 发布目录将主程序、管理员辅助程序和业务测试端分别放在根目录、`DeviceManager` 和 `TestClient`；主程序从真实 EXE 所在目录定位辅助程序，不依赖启动时的工作目录。

复测命令：

```powershell
.\tools\Test-VirtualDevice.ps1
```

驱动源码变化并重新构建驱动包后，可由管理员辅助程序的 `update` 命令更新设备。正常客户端通过“新建设备”触发辅助程序。

线路模拟只限制字节交付速度，并不产生真实电压、TXD/RXD 电气波形或微秒级 UART 时序。CTS 握手目前控制业务端到模拟器的队列释放；DTR/RTS 已通过协议版本 4 控制接口观测，DSR/DCD 握手和 XON/XOFF 暂未实现。

## 发布

```powershell
.\tools\Package-Release.ps1 -Version 1.0.0
```

发布目录包含：

- 根目录中的自包含单文件 WinUI 主程序。
- `DeviceManager` 子目录中的单文件管理员辅助程序；驱动包作为嵌入资源携带。
- `TestClient` 子目录中的单文件业务测试端。
- README、实现文档和微软示例许可证。

## 后续验证

1. 接收超时、取消、缓冲区满、停止模拟、端口关闭、设备删除和重启。
2. 设备创建、删除与多设备端口映射。
3. 请求分片、粘连、UTF-8 多字节拆分、超长帧和未知请求的持续回归测试。
4. 内部证书信任和目标公司的安全策略下安装并加载。

## 构建

```powershell
.\tools\Build-Driver.ps1 -Configuration Debug -TestSign
dotnet restore .\SerialPortSimulator\SerialPortSimulator.csproj
dotnet build .\SerialPortSimulator\SerialPortSimulator.csproj -c Debug -p:Platform=x64
```

构建完成后运行调试版主程序，点击“新建设备”触发管理员辅助程序。驱动源码变化时重新运行 `Build-Driver.ps1`，再通过辅助程序创建或更新设备。
