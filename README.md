> Release preparation: **0.1.0-beta.1**. See [release and recovery guide](docs/RELEASE.md) (Chinese), [changelog](CHANGELOG.md), and the [current Chinese feature guide](README.zh-CN.md). The self-contained ZIP requires no separate .NET installation. Only window dragging is enabled on first launch.
>
> Dock now manages applications and browser profiles through collections and sections. Browser launch parameters have a separate page. Desktop boxes include a unified desktop mode with a recovery helper. See the current guide for these newer features.

# MagiDesk

**English** | [中文](README.zh-CN.md)

A PowerToys-style desktop toolkit for Windows — a small collection of window &
browser productivity tools behind a single Fluent-design tray app.

Built with **C# / WPF / .NET 10** and [WPF-UI](https://github.com/lepoco/wpfui)
(Fluent + Mica, light/dark follows the system theme). Per-monitor-v2 DPI aware.

> Personal project, actively evolving. UI is in Simplified Chinese.

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
