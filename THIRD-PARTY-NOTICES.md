# Third-Party Notices

This file describes the licensing scope of this project and its third-party components.

## 1. Original Project Code

All original code outside the `VirtualSerial/` directory is licensed under the **PolyForm Noncommercial License 1.0.0**:

- Full license: [LICENSE](LICENSE)
- Chinese reference translation: [LICENSE.zh-CN.md](LICENSE.zh-CN.md)
- Noncommercial use is permitted; commercial use is not permitted.
- This is a **Source Available** license, not an OSI-approved open-source license.

Required Notice: Copyright (c) 2026 Fausto Feng.

## 2. VirtualSerial Driver

`VirtualSerial/` is based on the Microsoft Windows-driver-samples VirtualSerial2 sample and is licensed under the **Microsoft Public License (MS-PL)**:

- Source license: [VirtualSerial/LICENSE.microsoft.txt](VirtualSerial/LICENSE.microsoft.txt)
- MS-PL permits commercial use, modification, and redistribution.
- The project's noncommercial terms **do not apply** to `VirtualSerial/` or its compiled outputs.
- The driver DLL/CAT/INF included in the release package remains MS-PL-derived code; the release package also includes `LICENSE.microsoft.txt` at its root.

The project's noncommercial restriction therefore covers only the original application code and cannot restrict a recipient's MS-PL rights in the driver portion.

## 3. Third-Party Runtimes and Libraries

The release packages are self-contained and include:

- **.NET 10 Runtime**: MIT License
  <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- **Microsoft Windows App SDK**: Microsoft license terms
  <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- **System.IO.Ports**: MIT License
  <https://www.nuget.org/packages/System.IO.Ports/10.0.15#license>

WDK build tools, `devcon.exe`, and NuGet build dependencies are not distributed with the release package.

## 4. Commercial Licensing

For commercial use of the original project code, contact the project maintainer through the repository issues or maintainer page. MS-PL rights in the driver portion are not affected by this commercial licensing arrangement.
