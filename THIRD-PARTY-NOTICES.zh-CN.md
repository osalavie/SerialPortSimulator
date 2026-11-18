# 第三方声明

本文件说明本项目的许可证范围和第三方组件。

## 1. 项目自有代码

除 `VirtualSerial/` 目录外，本项目自有代码采用 **MIT**：

- 许可证全文：[LICENSE](LICENSE)
- 中文参考翻译：[LICENSE.zh-CN.md](LICENSE.zh-CN.md)

Copyright (c) 2026 Fausto Feng.

## 2. VirtualSerial 驱动

`VirtualSerial/` 基于 Microsoft Windows-driver-samples 的 VirtualSerial2 示例，采用 **Microsoft Public License (MS-PL)**：

- 源码许可证：[VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt)
- MS-PL 允许商业使用、修改和再分发。
- 发行包中包含的驱动 DLL/CAT/INF 仍是 MS-PL 派生代码；发行包根目录同时携带 `LICENSE.microsoft.txt`。

## 3. 第三方运行时和库

发行包以自包含方式包含以下组件：

- **.NET 10 Runtime**：MIT License
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- **Microsoft Windows App SDK**：Microsoft 许可条款
  <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- **System.IO.Ports**：MIT License
  <https://www.nuget.org/packages/System.IO.Ports/10.0.15#license>

WDK 的构建工具、`devcon.exe`、NuGet 构建依赖不随发行包分发。
