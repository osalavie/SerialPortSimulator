# 串口设备模拟器

[English](README.md)

在 Windows x64 上创建虚拟 COM 端口、发送模拟数据并自动回复串口请求。附带测试客户端，无需实体串口设备即可联调。

## 使用发行版

1. 从 [GitHub Releases](https://github.com/osalavie/SerialPortSimulator/releases/latest) 下载 Windows x64 ZIP，完整解压到可写目录。
2. 打开 `SerialPortSimulator.exe`，在「虚拟设备」页点击「新建设备」，确认 Windows 管理员提示。随包驱动使用测试签名，目标 Windows 需允许安装该驱动。
3. 打开 `TestClient\SerialPortSimulator.TestClient.exe`，选择刚创建的 COM 口，按模拟器设置填写串口参数，再点击「打开端口」。
4. 在模拟器的「模拟与收发」页发送一段文本，检查测试客户端是否收到。

## 从源码构建

需要 Windows x64、.NET 10 SDK，以及装有 C++ 和 Windows 驱动程序工具包（WDK）的 Visual Studio。

```powershell
git clone https://github.com/osalavie/SerialPortSimulator.git
cd SerialPortSimulator
.\tools\Package-Release.ps1
```

脚本会构建并测试签名驱动，在 `dist` 下生成 ZIP。解压后按上文第 2 至 4 步操作。

## 许可证

[应用代码采用 MIT](LICENSE) · [虚拟串口驱动采用 MS-PL](VirtualSerial/LICENSE.microsoft.txt)
