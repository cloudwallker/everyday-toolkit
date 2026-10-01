# 第三方声明 / Third-party notices

项目自有代码的 MIT 许可不替换第三方组件和 Windows 系统组件的条款。下表按实际实现列出运行依赖；发布脚本根据实际自包含 `.deps.json` 中的运行包标识/版本，从已解析的对应 NuGet 运行包保留原始许可和可用的第三方完整声明。

The project's MIT license does not replace the terms of third-party or Windows components. Packaging uses the published self-contained `.deps.json` runtime identities and versions to preserve original licenses and available third-party notices from the exact resolved NuGet runtime packages.

| 组件 / Component | 使用方式 / Use | 许可 / Terms |
| --- | --- | --- |
| .NET 10 runtime / WPF / Windows Forms | x64 自包含发布中随应用分发 / Distributed with the self-contained app | MIT，Copyright (c) .NET Foundation and Contributors；其包含的第三方代码保留各自声明 / Bundled third-party code retains its own notices |
| SQLite | 调用 Windows 自带 `winsqlite3.dll`，不复制系统 DLL / Uses Windows-provided DLL, not redistributed by this project | SQLite 上游代码属于公共领域 / SQLite upstream is public domain |
| Windows .NET Framework / WinForms / WScript.Shell / DPAPI / Win32 | 安装器和系统适配调用 Windows 自带组件 / Uses installed Windows components | 随 Windows 的条款提供，不把系统组件改授 MIT / Governed by Windows terms; not relicensed under this project's MIT |

.NET 官方许可：[runtime LICENSE.TXT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)、[WPF LICENSE.TXT](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)、[WinForms LICENSE.TXT](https://github.com/dotnet/winforms/blob/main/LICENSE.TXT)。实际发行包中的 MIT 全文保存为 `licenses/<运行包标识>/<已解析版本>/LICENSE.txt`，该运行包提供的第三方声明保存为同目录 `THIRD-PARTY-NOTICES.txt`；`licenses/runtime-packages.json` 记录本次真实运行包与声明路径。核心运行包必须保留第三方声明，Windows Desktop 当前运行包只随附 MIT 许可，没有单独第三方声明。不以 SDK 根目录的 .NET Library 商业许可替代运行包许可。

Each runtime package's license is included as `licenses/<package-id>/<resolved-version>/LICENSE.txt`, with its supplied third-party notices beside it. `licenses/runtime-packages.json` records the actual package/version and file paths. Core runtime notices are required; the current Windows Desktop package supplies its MIT license without a separate third-party notice file. Packaging does not substitute the SDK-root .NET Library commercial terms for the runtime package license. Review the actual notices rather than treating all embedded dependencies as MIT.

[SQLite 官方公共领域声明](https://www.sqlite.org/copyright.html)适用于 SQLite 原始代码；本项目使用的 Windows 系统 DLL 不随发行包分发，不承诺与上游最新版本一致。

[SQLite's public-domain dedication](https://www.sqlite.org/copyright.html) applies to original SQLite code. This project does not ship the Windows system DLL or assume it matches the latest upstream version.

首版未加入 NuGet 运行依赖、OCR 引擎、模型、字体或第三方图标素材。增加依赖时必须同时更新此声明并保留原始版权信息。

This version adds no NuGet runtime dependencies, OCR engines, models, fonts or third-party icon assets. Update these notices and preserve original attribution when adding dependencies.
