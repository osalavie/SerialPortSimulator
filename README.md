# SerialPortSimulator

[简体中文](README.zh-CN.md)

A Windows x64 tool for creating virtual COM ports, sending simulated data, and automatically replying to serial requests. It includes a test client for checking communication without physical hardware.

## Use a release

1. Download the Windows x64 ZIP from [GitHub Releases](https://github.com/osalavie/SerialPortSimulator/releases/latest) and extract it to a writable folder.
2. Run `SerialPortSimulator.exe`. If no virtual device exists, the app requests one automatically; you can also click **New Device** (新建设备) at the end of the device tab strip and approve the Windows administrator prompt. The bundled driver is test-signed, so Windows must allow its installation.
3. Run `TestClient\SerialPortSimulator.TestClient.exe`, select the new COM port, match the simulator's serial settings, and click **Open Port** (打开端口).
4. On that device's tab in the simulator, open the control link, send some text with the **Simulated data** card, and check that it appears in the test client. Close the device tab and confirm to remove the virtual device.

## Build from source

Requires Windows x64, the .NET 10 SDK, and Visual Studio with C++ and the Windows Driver Kit (WDK).

```powershell
git clone https://github.com/osalavie/SerialPortSimulator.git
cd SerialPortSimulator
.\tools\Package-Release.ps1
```

The script builds and test-signs the driver and creates a ZIP under `dist`. Extract it and follow steps 2–4 above.

## License

[Application code under MIT](LICENSE) · [Virtual serial driver under MS-PL](VirtualSerial/LICENSE.microsoft.txt)
