# Third-Party Notices

This file describes the licensing scope of this project and its third-party components.

## 1. Original Project Code

All original code outside the `VirtualSerial/` directory is licensed under the **MIT**:

- Full license: [LICENSE](LICENSE)
- Chinese reference translation: [LICENSE.zh-CN.md](LICENSE.zh-CN.md)

Copyright (c) 2026 Fausto Feng.

## 2. VirtualSerial Driver

`VirtualSerial/` is based on the Microsoft Windows-driver-samples VirtualSerial2 sample and is licensed under the **Microsoft Public License (MS-PL)**:

- Source license: [VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt)
- MS-PL permits commercial use, modification, and redistribution.
- The driver DLL/CAT/INF included in the release package remains MS-PL-derived code; the release package also includes `LICENSE.microsoft.txt` at its root.

## 3. Third-Party Runtimes and Libraries

The release packages are self-contained and include:

- **.NET 10 Runtime**: MIT License
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- **Microsoft Windows App SDK**: Microsoft license terms
  <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- **System.IO.Ports**: MIT License
  <https://www.nuget.org/packages/System.IO.Ports/10.0.15#license>

WDK build tools, `devcon.exe`, and NuGet build dependencies are not distributed with the release package.
