# Everyday Toolkit / 日用工具箱

### Clipboard history, snippets and text cleanup for Windows

**Everyday Toolkit is an open-source Windows 11 x64 productivity app with text and static-image clipboard history, favorites, reusable text snippets and text cleanup. A Chinese quick panel connects copying, finding, organizing and reusing content. Core features work offline without an account.**

English | [中文](README_ZH.md) · [User guide (Chinese)](docs/user-guide.md) · [v0.1.0 release notes](docs/releases/v0.1.0.md)

[Features](#features) · [Build and run](#development-and-packaging) · [Documentation](docs/user-guide.md)

![Clipboard and image history with synthetic demo content](docs/screenshots/clipboard.png)

The screenshots show the actual Chinese interface with synthetic content. [Dark theme](docs/screenshots/clipboard-dark.png) · [Snippets](docs/screenshots/snippets.png) · [Text cleanup](docs/screenshots/cleanup.png).

## Features

| Tool | Capabilities |
| --- | --- |
| Clipboard | Text search; image thumbnails and full preview; type, time and favorite filters; deduplication; favorites; copy; attempt paste to the original window; save PNG |
| Snippets | Names, categories, editing and search; save history text as a template; copy/paste; JSON import/export |
| Text cleanup | Trim, remove blank lines, deduplicate lines, join lines directly or with spaces, convert ASCII full/half width; editable result alongside the original |

The default shortcut is `Ctrl+Alt+V`. Closing the panel returns to the tray; use the tray menu to exit. Recording starts disabled and requires your choice on first run. Pausing preserves existing content. Start with Windows is enabled only through your explicit setting.

## Install and use

This repository publishes source code. Prebuilt GitHub Release downloads are not available yet. Build locally using the instructions below; packaging produces the two Windows x64 formats in `artifacts/releases`. See the release notes for verification limits.

1. Run `EverydayToolkit-0.1.0-Setup.exe`. It installs for the current user into `%LOCALAPPDATA%\Programs\EverydayToolkit` and creates a Start menu entry. No administrator account or separate .NET installation is required.
2. Alternatively, fully extract `EverydayToolkit-0.1.0-win-x64.zip` and run `EverydayToolkit.App.exe`. Keep all bundled DLLs and license files together.
3. Read the first-run explanation and choose to enable recording. Copy synthetic text or a static image, then find it through the quick panel. Use Copy followed by manual `Ctrl+V`, or Paste to original window.

The ZIP edition also defaults to `%LOCALAPPDATA%\EverydayToolkit` for data. Settings can select a local empty directory or an existing directory with a valid toolkit marker. Saving a changed location exits the app; reopen it to use that profile. Original content and settings stay in place, with no automatic migration or merging. An empty profile asks for recording consent again. An explicit `EverydayToolkit.App.exe --data-dir "D:\ToolkitData"` takes priority and makes the settings location read-only. The encrypted database is not portable across Windows accounts.

Compare `Get-FileHash .\EverydayToolkit-0.1.0-Setup.exe -Algorithm SHA256` against `SHA256SUMS.txt` alongside the release assets.

## Storage and limits

History defaults to 1,000 entries and 200 MiB of encrypted payload. Unfavorited entries expire after seven days from last use. Favorites are exempt from automatic expiration but share the quota; new captures are blocked when favorites fill it. Snippets have an independent 1,000-entry / 10 MiB quota. Each text entry allows 256 KiB UTF-8; each image allows 8 MiB encoded PNG and 64 MiB decoded pixels.

Text, snippet names/categories, PNGs and thumbnails are encrypted using Windows current-user DPAPI. Necessary metadata includes type, times, dimensions, favorite status, accounting and content hashes. Settings contain no content. Default files are `content.db`, SQLite logs, `settings.json` and a data marker. Database/decryption errors do not trigger automatic database deletion.

Snippet JSON export and Save PNG produce plaintext files by explicit action. Use snippet export/import for migration; encrypted database backups are intended for restoration on the same machine and Windows account. Other processes running with your account's permissions may access the data. The app does not claim to detect every password; pause recording, exclude applications or delete data when needed.

Image history stores static clipboard pixels. File lists alone are not read as image files. OCR, animation history, cloud synchronization and AI rewriting are outside this release. Automatic paste depends on the target window, focus, permissions and supported formats. When it cannot confirm the destination it keeps the clipboard and asks you to paste manually. Enter does not trigger automatic paste.

Exit the app from its tray before uninstalling. Windows Installed apps or `Uninstall.exe` offers a choice to retain or delete default user data. Deletion requires a valid data marker and removes only known application files. Unknown files/directories are preserved, and custom data locations must be managed manually. Modified or busy installation files may stop removal; back up and inspect them first.

## Development and packaging

Use Windows 11 x64, the .NET 10 SDK and Windows' built-in .NET Framework compiler. Scripts prefer `.tools/dotnet/dotnet.exe`, falling back to `dotnet` on PATH.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
# Optional cross-process synthetic receiver and foreground switching
.\scripts\test.ps1 -IncludeDesktop
# Synthetic performance/capacity measurements; use dotnet for a PATH SDK
dotnet run --project tests/EverydayToolkit.App.Tests -c Release -- --perf
.\scripts\package.ps1
.\scripts\verify-package.ps1
# Actual self-contained startup; Windows PowerShell 5.1 and a desktop required
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\smoke-release.ps1
```

Core provides rules and storage; Windows provides OS adapters; App provides the WPF UI. By default, `test.ps1` runs four behavioral console suites, App `--settings-ui` / `--ui` WPF integration, real installer process tests and six release-privacy scanner regressions. Failures return a nonzero exit code. Settings UI tests load the real App.xaml while explicitly suppressing personal-profile startup. Windows/full UI tests temporarily use the real clipboard and restore its previous contents; run serially without other clipboard activity. Actual current-user DPAPI and isolated registry tests may require authorized execution outside a restricted sandbox.

`-IncludeDesktop` additionally runs a cross-process synthetic receiver scenario. That scenario has not passed on this machine because Windows rejected foreground switching. Passing the default suites does not establish automatic paste acceptance or success of this optional scenario.

`--perf` separately measures synthetic workloads; conditions and actual numbers belong in the [verification report](docs/verification/v0.1.0.md). `smoke-release.ps1` starts the actual published App with isolated artifacts data and installed-runtime lookup disabled, checking local coreclr/hostfxr, initialized WPF with recording paused, SQLite creation and equivalent-directory second-instance exit. Use `-ApplicationDirectory` for an existing installed copy inside artifacts. This does not replace testing on a clean machine without an SDK.

Packaging runs all tests by default, including serial App UI integration. It publishes the .NET 10 self-contained `artifacts/publish/win-x64` directory, preserves and verifies licenses/notices from the actual resolved runtime package identities and versions, then produces the ZIP, an embedding .NET Framework WinForms installer, and SHA256 checksums. Trimming and Native AOT are disabled. `-SkipTests` is only for repackaging an already verified build. Existing publish output is refused to prevent stale files being merged into a release; review and move older artifacts first.

Installer tests use synthetic data inside `artifacts`. Registration tests create an isolated temporary HKCU test key and an artifacts shortcut, without touching real startup registrations or Start menu entries. A restricted sandbox may need permission to write that isolated key. Controlled installer CLI usage is documented in the user guide.

Clean-machine installation, real Office workflows, Chinese IME, multiple displays and 100%/150%/200% scaling require separate acceptance. Local automated tests do not establish those outcomes. The [verification report in the source tree](docs/verification/v0.1.0.md) records actual final evidence and remaining acceptance items.

## License and contribution

Original code uses [MIT](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for .NET and system SQLite, and [CONTRIBUTING](CONTRIBUTING.md) for changes and issue reports. Submit synthetic samples only: never upload personal clipboard content, templates, databases, credentials or work screenshots.
