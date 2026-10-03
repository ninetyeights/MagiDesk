using System.Windows.Controls;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Infrastructure;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

public sealed partial class ProfileDockService
{
    private readonly DispatcherTimer _applicationTimer;
    private bool _applicationScanPending;
    private readonly Dictionary<string, int> _lastApplicationMatchCounts = new();
    private List<DockApplicationRuntime.Window> _applicationWindows = new();
    private List<DockItem> _runningItems = new();
    private Dictionary<IntPtr, string> _browserWindowSnapshot = new();

    private Dictionary<IntPtr, string> BrowserWindowMap() => _browserWindowSnapshot;

    private bool UpdateRunningItems()
    {
        var cfg = AppConfig.Current;
        var displayed = DockGroups.Build(cfg, _profiles).SelectMany(g => g.Items).ToList();
        var visible = displayed.Where(i => i.Profile is not null).Select(i => i.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = cfg.DockShowRunningApplications
            ? DockRunningItems.Build(_applicationWindows, displayed.Where(i => i.Application is not null).Select(i => i.Application!).ToList(), BrowserWindowMap(), _profiles, visible, _runningItems)
            : new List<DockItem>();
        bool changed = !_runningItems.Select(i => i.Key).SequenceEqual(next.Select(i => i.Key));
        _runningItems = next;
        return changed;
    }

    private List<DockApplicationRuntime.Window> ApplicationMatches(DockApplication app, bool runningOnly,
        IEnumerable<DockApplicationRuntime.Window> windows)
    {
        var matches = DockApplicationRuntime.Match(app, windows);
        if (runningOnly)
        {
            var mapped = BrowserWindowMap();
            // Browser windows have their own profile buttons; don't include them
            // in the unresolved executable entry's picker or foreground state.
            var known = _profiles.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            matches.RemoveAll(w => mapped.TryGetValue(w.Handle, out var key) && known.Contains(key));
        }
        return matches;
    }

    private IReadOnlyList<(string? Name, IReadOnlyList<DockItem> Items)> BuildItems()
    {
        var groups = DockGroups.Build(AppConfig.Current, _profiles);
        if (_runningItems.Count > 0) groups.Add(("正在运行", _runningItems));
        return groups;
    }

    private void UpdateApplicationTracking()
    {
        if (!AppConfig.Current.DockShowRunningApplications &&
            !DockGroups.Build(AppConfig.Current, _profiles).SelectMany(g => g.Items).Any())
        {
            _applicationTimer.Stop();
            _applicationWindows.Clear();
            return;
        }
        if (!_applicationTimer.IsEnabled)
        {
            _applicationTimer.Start();
            RefreshApplicationWindows();
        }
    }

    private async void RefreshApplicationWindows()
    {
        if (_disposed || _windows.Count == 0 || _applicationScanPending) return;
        _applicationScanPending = true;
        try
        {
            var windows = await ScanApplicationsAsync();
            if (_disposed || _windows.Count == 0) return;
            if (App.BrowserBadges is { } browsers)
                await browsers.RefreshDockWindowProfilesAsync(windows.Select(w => (w.Handle, w.ProcessId, w.ExecutablePath)));
            if (_disposed || _windows.Count == 0) return;
            _applicationWindows = windows;
            OnWindowsChanged();
        }
        catch (Exception ex) { DiagnosticLog.Write($"DOCK-APPS scan failed: {ex.GetType().Name}"); }
        finally { _applicationScanPending = false; }
    }

    private Task<List<DockApplicationRuntime.Window>> ScanApplicationsAsync()
    {
        var apps = AppConfig.Current.DockApplications.Select(a => (a.LaunchPath, a.ExecutablePath)).ToArray();
        return Task.Run(() =>
        {
            DockApplicationRuntime.RefreshShortcutInstances(apps);
            return DockApplicationRuntime.Scan();
        });
    }

    private void OnItemClicked(DockItem item, Button anchor)
    {
        if (item.Profile is { } profile) OnProfileClicked(profile, anchor, item.RunningOnly);
        else if (item.Application is { } application) OnApplicationClicked(application, anchor, item.RunningOnly);
    }

    internal int ApplicationWindowCount(DockItem item) => item.Application is { } app
        ? ApplicationMatches(app, item.RunningOnly, _applicationWindows).Count : 0;

    internal List<DockApplicationRuntime.Window> PreviewWindows(DockItem item) => item.Profile is { } profile
        ? _applicationWindows.Where(w => BrowserWindowMap().TryGetValue(w.Handle, out var key) &&
            string.Equals(key, profile.Key, StringComparison.OrdinalIgnoreCase)).ToList()
        : ApplicationMatches(item.Application!, item.RunningOnly, _applicationWindows);

    internal static void ActivatePreview(DockApplicationRuntime.Window window)
    {
        if (NativeMethods.IsWindow(window.Handle) &&
            NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid) != 0 && pid == window.ProcessId)
            FocusWindow(window.Handle);
    }

    internal async Task LaunchAnotherAsync(DockItem item)
    {
        if (_disposed || !_clicksInProgress.Add(item.Key)) return;
        try
        {
            if (item.Application is { } app) await Task.Run(() => DockApplicationRuntime.Launch(app));
            else if (item.Profile is { } profile)
            {
                var before = new HashSet<IntPtr>(App.BrowserBadges?.EnumerateAllBrowserHwnds() ?? Enumerable.Empty<IntPtr>());
                string extra = (AppConfig.Current.BrowserLaunchArguments.GetValueOrDefault(profile.Browser.Id) ?? "") + " --new-window";
                if (!await Task.Run(() => ChromeLauncher.Launch(profile.Browser, profile.Directory, extra)))
                    throw new InvalidOperationException("无法启动浏览器");
                AssociateNewHwndWithProfile(before, profile.Key);
            }
        }
        finally { _clicksInProgress.Remove(item.Key); }
    }

    internal async Task CloseApplicationWindowsAsync(DockItem item)
    {
        if (_disposed || item.Application is not { } app) return;
        var windows = await ScanApplicationsAsync();
        if (_disposed) return;
        foreach (var window in ApplicationMatches(app, item.RunningOnly, windows))
        {
            // Ask the application to close normally, allowing its unsaved-work prompt.
            if (!NativeMethods.IsWindow(window.Handle) ||
                NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid) == 0 || pid != window.ProcessId) continue;
            if (!NativeMethods.PostMessage(window.Handle, NativeConstants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                DiagnosticLog.Write("DOCK-APPS close request failed");
        }
    }

    private async void OnApplicationClicked(DockApplication app, Button anchor, bool runningOnly)
    {
        string key = DockItem.ApplicationKey(app.Id);
        if (_disposed || !_clicksInProgress.Add(key)) return;
        try
        {
            if (_windowPicker is not null) _windowPicker.IsOpen = false;
            var foreground = NativeMethods.GetForegroundWindow();
            var windows = await ScanApplicationsAsync();
            if (_disposed || _windows.Count == 0 || !anchor.IsVisible ||
                !(runningOnly ? _runningItems.Any(i => i.Key == key) : AppConfig.Current.DockApplications.Any(a => a.Id == app.Id))) return;
            _applicationWindows = windows;
            var matches = ApplicationMatches(app, runningOnly, windows);
            // Reject stale/reused handles before switching or minimizing.
            matches.RemoveAll(w => !NativeMethods.IsWindow(w.Handle) ||
                NativeMethods.GetWindowThreadProcessId(w.Handle, out uint pid) == 0 || pid != w.ProcessId);
            if (NativeMethods.GetForegroundWindow() != foreground) return;
            if (matches.Count == 0)
            {
                if (runningOnly) { OnWindowsChanged(); return; }
                var now = DateTime.UtcNow;
                if (_lastLaunchUtc.TryGetValue(key, out var last) && now - last < LaunchDebounce) return;
                _lastLaunchUtc[key] = now;
                try { await Task.Run(() => DockApplicationRuntime.Launch(app)); }
                catch { _lastLaunchUtc.Remove(key); throw; }
            }
            else
            {
                var handles = matches.Select(w => w.Handle).ToList();
                if (handles.Count > 1) ShowWindowPicker(anchor, handles);
                else if (ShouldMinimize(foreground, handles)) NativeMethods.ShowWindow(foreground, NativeConstants.SW_MINIMIZE);
                else FocusWindow(handles[0]);
            }
            OnWindowsChanged();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"DOCK-APPS click failed: {ex.GetType().Name}");
            if (!_disposed && anchor.IsVisible)
            {
                var menu = new ContextMenu { PlacementTarget = anchor };
                menu.Items.Add(new MenuItem { Header = "无法打开应用，请检查程序或快捷方式是否仍然存在。", IsEnabled = false });
                _windowPicker = menu;
                menu.Closed += (_, _) => { if (ReferenceEquals(_windowPicker, menu)) _windowPicker = null; };
                menu.IsOpen = true;
            }
        }
        finally { _clicksInProgress.Remove(key); }
    }
}
