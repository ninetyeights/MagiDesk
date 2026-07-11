# MagiDesk

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
editor lets you split / merge / delete zones and drag dividers (a full grid line
moves together). Supports multiple named layouts, per-monitor assignment,
built-in templates, and edge-band merging of adjacent zones.

### ⊞ Quick Grid
A global hotkey (default **Ctrl+Shift+G**) pops up a rows×cols picker over your
monitors; drag a rectangle across cells and the active window tiles to that
range. Per-monitor grid density, one-click centering, all-monitors view.

### 🅑 Browser Badges
Floating per-profile avatar badges that follow each browser window, so you can
tell your many profiles apart at a glance. Multi-browser: Chrome, Edge, Brave,
Vivaldi, Opera. Avatars, colors, and visibility are customizable per profile.

### ⌂ Profile Dock
A taskbar-like floating strip of browser-profile avatars — click to launch,
focus, or cycle through a profile's windows. Supports named groups, single- or
all-monitors, and either a free-floating overlay or an AppBar pinned to a screen
edge (reserving space like the system taskbar).

## Extras
- System tray icon with close-to-tray; single-instance (a second launch surfaces
  the running window).
- Optional launch-with-Windows (per-user `HKCU\...\Run`).
- JSON config at `%APPDATA%\MagiDesk\config.json` with atomic writes, a `.bak`
  fallback, and rolling daily backups.
- Diagnostic log at `%TEMP%\magidesk.log`.

## Build & Run

Requires the **.NET 10 SDK** and Windows 10/11.

```powershell
dotnet run --project MagiDesk\MagiDesk.csproj
```

The app minimizes to the tray on close; use the tray menu's **退出 (Exit)** to
quit fully.

### Tests

```powershell
dotnet run --project MagiDesk.Tests            # unit tests (injects real input)
dotnet run --project MagiDesk.Tests -- --real-world   # drive the real exe
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
