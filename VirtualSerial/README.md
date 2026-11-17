# Virtual Serial Driver

本目录包含串口设备模拟器使用的 UMDF 2 虚拟串口驱动。

## 组成

- `driver.c` / `device.c`：WDF 驱动和设备初始化。
- `queue.c`：串口读写、串口 IOCTL、模拟器控制协议和接收事件。
- `ringbuffer.c`：双向环形缓冲区。
- `simulator-protocol.h`：模拟器控制协议版本 3。
- `VirtualSerial2um.vcxproj`：x64 驱动构建工程。
- `virtualserial2um.inx`：根枚举虚拟串口 INF。

## 设备能力

- 标准 COM 符号链接和 `GUID_DEVINTERFACE_COMPORT`。
- 模拟器独立控制接口。
- 模拟器权威串口参数：波特率、数据位、校验位、停止位。
- 双向 65535 字节有效环形缓冲区。
- RXCHAR 接收事件通知。
- 模拟器注入、业务数据读取和规则响应。

## 构建

在仓库根目录运行：

```powershell
.\tools\Build-Driver.ps1 -Configuration Debug -TestSign
```

构建需要 Visual Studio C++、Windows Driver Kit 和 Spectre 库。工具组件清单位于 `tools/driver-build.vsconfig`。

驱动基于 Microsoft Windows-driver-samples 的 VirtualSerial2 示例，许可证见 `LICENSE.microsoft.txt`。
