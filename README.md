# MagiDesk

**English** | [中文](README.zh-CN.md)

A PowerToys-style desktop toolkit for Windows — a small collection of window &
browser productivity tools behind a single Fluent-design tray app.

Built with **C# / WPF / .NET 10** and [WPF-UI](https://github.com/lepoco/wpfui)
(Fluent + Mica, light/dark follows the system theme). Per-monitor-v2 DPI aware.

> Personal project, actively evolving. UI is in Simplified Chinese.

**[Download v0.1.0-beta.1](https://github.com/ninetyeights/MagiDesk/releases/tag/v0.1.0-beta.1)** · [All releases](https://github.com/ninetyeights/MagiDesk/releases) · [Changelog](CHANGELOG.md) · [Report an issue](https://github.com/ninetyeights/MagiDesk/issues)

## Download and get started

The first release is a **public beta**, primarily targeting Windows 11 x64. Installer and portable packages include .NET; no separate runtime installation is required.

| Package | Choose this for |
| --- | --- |
| `win-x64-Setup-*.exe` | Installation on Intel / AMD Windows PCs |
| `win-arm64-Setup-*.exe` | Windows ARM64 devices; real-device validation is still pending |
| `win-x64-*.zip` / `win-arm64-*.zip` | Portable use: extract the entire archive, then run `MagiDesk.exe` |

1. Download the package matching your device. Installers use a per-user directory and require no administrator access.
2. Launch MagiDesk. **Only Window Drag is enabled by default**; enable other tools as needed in the sidebar.
3. Hold **Alt + left-drag** to move a window, or **Alt + right-drag** to resize it.
4. Closing the main window normally leaves MagiDesk in the tray. Double-click its tray icon to reopen it; choose **退出 (Exit)** to quit.

Installer upgrades and uninstall preserve your configuration and desktop files. Back up `%APPDATA%\MagiDesk` before upgrading. The installer currently has no Windows Authenticode signature; signed update manifests are a separate integrity mechanism.

See the [installation and recovery guide](docs/RELEASE.md) (Chinese) for details.


## Tools

### 🖱️ Window Drag (AltSnap-style)
Hold a modifier (default **Alt**) and drag **anywhere** on a window to move it —
any visible window, not just the focused one. Modifier + **right-drag** resizes,
with the direction chosen by which quadrant of the window the cursor is in. The
move/resize modifiers are configurable.

### 🧲 Edge Snap (magnetic)
While moving a window with the drag modifier held, its edges snap to nearby
**monitor work-area edges**, **other windows' edges**, and **alignment lines**
(matching edges / centers). Uses the visible (DWM extended-frame) bounds so the
snap is pixel-accurate. Configurable snap distance and targets. (Lives on the
Window Drag page.)

### ▦ Zones (FancyZones-style)
Hold **Shift** while dragging to snap a window into a zone. A tree-based layout
editor lets you split / merge / delete zones and drag dividers, with local and
global cuts and conversion between them. Supports multiple named layouts, per-monitor assignment,
built-in templates, and edge-band merging of adjacent zones.

### ⊞ Quick Grid
A global hotkey (default **Ctrl+Shift+G**) pops up a rows×cols picker over your
monitors; drag a rectangle across cells and the active window tiles to that
range. Per-monitor grid density, one-click centering, all-monitors view.

### 🅑 Browser Badges
Floating per-profile avatar badges that follow each browser window, so you can
tell your many profiles apart at a glance. Multi-browser: Chrome, Edge, Brave,
Vivaldi, Opera. Avatars, colors, and visibility are customizable per profile.

### ⌂ Dock
Mix browser profiles and ordinary applications in collections and sections.
Content Management edits collections; the Project Library manages available
items. Includes running applications, window previews, pins, scrolling or
wrapping, icon sizing, floating placement and taskbar space reservation.

### Desktop Boxes
Organize desktop files with tabs, mapped folders, thumbnails and temporary
reveal. Unified desktop mode includes a helper to restore native desktop icon
visibility. Read the recovery guide before enabling it.

### Browser Launch Parameters
Open Browser → Launch Parameters in the sidebar. Settings apply to launches
from Dock; focusing an existing window does not reapply arguments.

## Extras
- System tray icon with close-to-tray; single-instance (a second launch surfaces
  the running window).
- Optional launch-with-Windows (per-user `HKCU\...\Run`).
- JSON config at `%APPDATA%\MagiDesk\config.json` with atomic writes, a `.bak`
  fallback, and rolling daily backups.
- Diagnostic log at `%TEMP%\magidesk.log`.

## Updates and beta limitations

Check for updates from the About page. Automatic checking is off by default. Downloads and the installer helper verify the signed update manifest and package hash before installation. Updating a portable copy through this entry point installs the per-user edition.

- Cross-DPI restoration of File Explorer may briefly show a blank transition.
- Windows 10, ARM64, remote desktop reconnects and monitor hot-plugging have not been comprehensively validated on real devices.
- The first release passed 346 headless tests and dependency/secret scans; these do not replace desktop and installation testing.

When reporting a problem, include the app version, Windows version, monitor scaling and reproduction steps. Review `%TEMP%\magidesk.log` for personal information before sharing relevant excerpts.

## Build & Run

Development builds require the **.NET 10 SDK** on Windows. See the release guide
for the tested platform scope; self-contained packages include the runtime.

```powershell
dotnet run --project MagiDesk\MagiDesk.csproj
```

The app minimizes to the tray on close; use the tray menu's **退出 (Exit)** to
quit fully.

### Tests

```powershell
dotnet run --project MagiDesk.Tests -- --headless # no windows or real desktop changes
# Interactive --real-world tests manipulate actual windows; schedule separately.
```

## Project layout

```
MagiDesk/
  Features/        # one folder per tool (AltDragger, Zones, QuickGrid,
                   #   BrowserBadges, ProfileDock, EdgeSnap) + TrayService, etc.
  Pages/           # WPF-UI pages, one per tool + Settings / About
  Config/          # AppConfig (JSON persistence)
  Native/          # P/Invoke declarations
  Hooks/           # low-level mouse hook
MagiDesk.Tests/    # console integration tests
```

## Release builds

The SDK is selected by `global.json`, and NuGet dependencies use checked-in lock files. `scripts/Publish-Release.ps1 -Installer` runs security checks and headless tests before packaging; installer builds require Inno Setup 6.3+.

Pushing a matching version tag triggers GitHub Actions to build x64 and ARM64 packages and create a draft release. Installers are signed through an offline update manifest before publication. See the [signing guide](docs/UPDATE-SIGNING.md). Private signing keys are never uploaded to GitHub.
