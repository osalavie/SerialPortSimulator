# 第三方声明

本文件说明本项目的许可证范围和第三方组件。

## 1. 项目自有代码

除 `VirtualSerial/` 目录外，本项目自有代码采用 **PolyForm Noncommercial 1.0.0**：

- 许可证全文：[LICENSE](LICENSE)
- 中文参考翻译：[LICENSE.zh-CN.md](LICENSE.zh-CN.md)
- 该许可允许非商业使用，不允许商业使用。
- 这是 **Source Available（源码可见）** 许可，不属于 OSI 定义的开源许可证。

Required Notice: Copyright (c) 2026 Fausto Feng.

## 2. VirtualSerial 驱动

`VirtualSerial/` 基于 Microsoft Windows-driver-samples 的 VirtualSerial2 示例，采用 **Microsoft Public License (MS-PL)**：

- 源码许可证：[VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt)
- MS-PL 允许商业使用、修改和再分发。
- 本项目的非商业条款**不适用于** `VirtualSerial/` 目录及其编译产物。
- 发行包中包含的驱动 DLL/CAT/INF 仍是 MS-PL 派生代码；发行包根目录同时携带 `LICENSE.microsoft.txt`。

因此，本项目的“禁止商用”只覆盖自有应用代码，不能限制接收方依据 MS-PL 使用驱动部分。

## 3. 第三方运行时和库

发行包以自包含方式包含以下组件：

- **.NET 10 Runtime**：MIT License
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- **Microsoft Windows App SDK**：Microsoft 许可条款
  <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- **System.IO.Ports**：MIT License
  <https://www.nuget.org/packages/System.IO.Ports/10.0.15#license>

WDK 的构建工具、`devcon.exe`、NuGet 构建依赖不随发行包分发。

## 4. 商业授权

如需将本项目自有代码用于商业用途，请通过项目仓库 Issue 或维护者主页联系商业授权。驱动部分的 MS-PL 权利不受此商业授权安排限制。
