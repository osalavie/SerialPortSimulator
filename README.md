# Serial Port Simulator

A Windows desktop application built on Microsoft WinUI 3 for creating and managing virtual COM devices, injecting simulated data, and responding to requests sent by business applications.

[English](README.md) | [中文](README.zh-CN.md)

## Features

- Enumerate, create, and remove virtual COM devices created by this product without touching physical serial ports or devices from other vendors.
- Use a separate control interface so business applications can open the COM port exclusively.
- The simulator is the authority for serial parameters. It can set baud rate, data bits, parity, and stop bits; parameters sent by the business application do not override the simulator configuration.
- Optionally limit bidirectional byte throughput by baud rate and frame format; when disabled, transfers remain immediate in memory. The simulator can toggle CTS; when the business application enables RTS/CTS handshake, an inactive CTS pauses the business transmit queue.
- The main window uses two tabs: **Virtual Device** and **Simulation & I/O**, with wrapping card layouts that fill empty slots.
- Inject UTF-8 bytes periodically or once, with incrementing counter, sine wave, random values, or fixed text.
- Template variables: `{timestamp}`, `{counter}`, `{value}`. Line endings can be none, CRLF, LF, or CR, and can be changed while periodic sending runs.
- Match CRLF-framed `request=response` rules and reply automatically.
- Show business writes, injected data, and automatic responses, keeping the latest 200 records.
- Display the current injection target COM port, and include the port name in logs.
- A standalone WinUI 3 `TestClient/SerialPortSimulator.TestClient.exe` opens a COM port with manually selected baud rate, data bits, parity, and stop bits; it does not read the simulator control interface or serial parameters, and refreshes the port list automatically. It uses the same `RX/TX [bytes B] content` format as the simulator, keeps the latest 200 records at the top, supports optional timestamps and hex display, DTR/RTS, and optional RTS/CTS hardware flow control, and writes diagnostics to `Logs/test-client.log` next to the executable. Receiving uses 50 ms polling.

## Build

Requirements: Windows 10 1809 or later, .NET 10 SDK, Visual Studio C++ tools, and the Windows Driver Kit (WDK).

`SerialPortSimulator.sln` contains `SerialPortSimulator`, `TestClient`, `DeviceManager`, `DriverSmoke`, and `VirtualSerial`. Use the `Debug|x64` configuration in Visual Studio, or build individual projects from the command line.

```powershell
.\tools\Build-Driver.ps1 -Configuration Debug -TestSign
dotnet restore .\SerialPortSimulator\SerialPortSimulator.csproj
dotnet build .\SerialPortSimulator\SerialPortSimulator.csproj -c Debug -p:Platform=x64
```

`-TestSign` creates or reuses `tools\.certs\SerialPortSimulatorTest.pfx`, signs `wudf.cat`, and copies the matching public certificate into the driver output. The private key and password stay local and are ignored by `.gitignore`; release packages contain only the public certificate. `Package-Release.ps1` refuses to package an unsigned catalog or a certificate that does not match the catalog signer.

The administrator device manager embeds the driver DLL, INF, CAT, and test certificate, and uses Windows SetupAPI / Configuration Manager to create, update, and remove devices. Click **Create Device** in the application; Windows shows a UAC prompt. The helper stages embedded files under `C:\ProgramData\SerialPortSimulator\DevicePackage`.

Driver operations use a unique staging directory and delete extracted files at the end. The helper also removes same-named files left by older versions in the `DevicePackage` root. Removing the last simulator device also removes matching driver packages and the embedded test certificate. The release package no longer contains the WDK `devcon.exe`.

Extract the whole ZIP. The main application is at the root, the administrator helper is under `DeviceManager`, and the business test client is under `TestClient`.

Create a portable release:

```powershell
.\tools\Package-Release.ps1
```

Example: `.\tools\Package-Release.ps1 -Version 1.0.0 -SkipDriverBuild`. Add `-NoRestore` when dependencies are already restored and the machine is offline. Output is written to `dist`; each application is published as a self-contained single-file EXE, while the driver package is embedded in the administrator helper. At runtime, single-file EXEs extract native dependencies.

Device creation and removal run in the separate administrator helper. The driver is currently test-signed; replace it with a company code-signing certificate before production delivery.

If installation logs show `0x800B0100`, `No signature was present`, or “driver package is unsigned”, the CAT is unsigned or the certificate does not match. Re-run `.\tools\Build-Driver.ps1 -Configuration Debug -TestSign` and rebuild the package. The target machine must trust the test certificate or allow test signing.

Normal business applications that open the COM port should query the actual parameters with Windows `GetCommState` or standard serial query IOCTLs before parsing data. Do not assume the `9600 8N1` values used to open the port are the simulator's actual parameters. The test client deliberately does not read the simulator control interface; it opens the port with the user-selected baud rate, data bits, parity, and stop bits. The simulator still treats its own configuration as authoritative.

The test client's DTR/RTS values are written to business-side output lines when the port opens. Driver protocol version 4 displays DTR, RTS, and RTS/CTS handshake state in the simulator parameter card. When **RTS/CTS flow control** is checked, the test client uses `Handshake.RequestToSend`; RTS is controlled by the serial stack and CTS is controlled by the simulator's **CTS allows business transmit** option. With flow control disabled, DTR/RTS are manual static levels.

## Run and Test

```powershell
.\SerialPortSimulator\bin\x64\Debug\net10.0-windows10.0.19041.0\SerialPortSimulator.exe
.\tools\Test-VirtualDevice.ps1
```

End-to-end tests cover exclusive COM access, parallel control-channel operation, UTF-8 injection, business writes, and CRLF automatic responses. See [docs/virtual-device-implementation.md](docs/virtual-device-implementation.md) for implementation details and current verification status.

After updating the driver to protocol version 4, run `dotnet run --project .\DriverSmoke\DriverSmoke.csproj -- --line-test` to verify line-rate limiting and CTS pause/resume. DTR/RTS and RTS/CTS state can be observed in the simulator parameter card. Software simulation is limited by Windows timer precision and does not produce real RS-232/TTL voltages or hardware TXD/RXD waveforms.

See [docs/serial-port-knowledge.md](docs/serial-port-knowledge.md) for serial-port concepts, data flow, and buffering strategy.

Diagnostic logs are always written under `Logs` next to the executable. The portable ZIP must be extracted to a writable directory; if the application directory is read-only, logs are not redirected elsewhere.

## Files on the User Machine

| Location | Contents and cleanup |
|---|---|
| Extracted application directory | Three single-file applications and documentation; main and test-client logs live under `Logs`, and the administrator helper writes `DeviceManager\Logs\device-manager.log`. Each log is capped at about 1 MiB, and only the latest crash is retained. After removing all devices, the extracted directory can be deleted. |
| `%ProgramData%\SerialPortSimulator\DevicePackage` | Driver staging directory for administrator operations; no logs are written here. The helper cleans staging files when the process exits and retains them only when cleanup fails for diagnosis. |
| `%TEMP%\.net\SerialPortSimulator` and similar product subdirectories | .NET native-library cache for single-file EXEs, not a log. Delete this product-specific subdirectory after exit; do not delete the whole `%TEMP%\.net` directory. Deleting the application directory does not automatically clear these caches. |

Windows Driver Store entries, virtual-device registry state, and the test certificate imported into the system trust stores are installation state, not temporary files. Remove all devices in the application first; removing only the ZIP or extracted directory does not uninstall these system components.

For business integration, run the simulator and `TestClient\SerialPortSimulator.TestClient.exe` together, select the same COM port in the test client, set the baud rate, data bits, parity, and stop bits, and click **Open Port**.

## License

This project uses split licensing:

- Original code outside `VirtualSerial/`: **PolyForm Noncommercial License 1.0.0**. See [LICENSE](LICENSE) and the Chinese reference [LICENSE.zh-CN.md](LICENSE.zh-CN.md). This is a Source Available license, not an OSI-approved open-source license; commercial use is not permitted.
- `VirtualSerial/`: **Microsoft Public License (MS-PL)**, see [VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt). MS-PL permits commercial use, modification, and redistribution. The project's noncommercial terms do not apply to this directory or its compiled outputs.
- Commercial licensing for the original code is available through the project repository issues or the maintainer page. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party components (Chinese: [THIRD-PARTY-NOTICES.zh-CN.md](THIRD-PARTY-NOTICES.zh-CN.md)) and [README.zh-CN.md](README.zh-CN.md) for the Chinese documentation.
