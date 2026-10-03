using System.IO;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Infrastructure;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

public sealed partial class DesktopFenceService
{
    private bool _unifiedSurface;
    private readonly Dictionary<string, FenceBoxWindow> _desktopSurfaces = new();
    private IReadOnlyList<DesktopItem>? _surfaceItems;
    private IReadOnlyList<DesktopMonitor> _desktopMonitors = [];
    private DispatcherTimer? _desktopTopologyTimer;
    private DesktopShellMenu.ViewSettings? _desktopViewSettings;
    private bool _desktopDistributionQueued;
    internal DesktopIconDrag? ActiveDesktopDrag { get; set; }
    internal string? DesktopDragToken { get; set; }
    private DesktopSurfaceLease? _desktopLease;
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private DispatcherTimer? _desktopRefreshTimer;
    private DispatcherTimer? _desktopRecoveryTimer;
    private int _desktopRecoveryRetries;
    private DispatcherTimer? _membershipProbeTimer;
    private bool _membershipProbeRunning;

    internal void KeepDesktopSurfaceBehind()
    {
        foreach (var surface in _desktopSurfaces.Values) surface.PlaceOnDesktop();
    }

    internal void RedistributeDesktopIcons()
    {
        if (!_active || !_unifiedSurface || _desktopDistributionQueued) return;
        _desktopDistributionQueued = true;
        _ui.BeginInvoke(new Action(() =>
        {
            _desktopDistributionQueued = false;
            if (_active && _unifiedSurface && _surfaceItems is { } items) RenderDesktopSurface(items);
        }));
    }

    internal void QueueDesktopTopologyRefresh()
    {
        if (!_active || !_unifiedSurface) return;
        _desktopTopologyTimer?.Stop();
        _desktopTopologyTimer?.Start();
    }

    private void OnDesktopDisplaysChanged(object? sender, EventArgs e) => _ui.BeginInvoke(new Action(QueueDesktopTopologyRefresh));

    private void OnDesktopSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        => _ui.BeginInvoke(new Action(() =>
        {
            DiagnosticLog.Write($"DESKTOP-SESSION reason={e.Reason}\n");
            QueueDesktopTopologyRefresh();
        }));

    internal void SyncDesktopSurfaceSettings(DesktopShellMenu.ViewSettings settings)
    {
        _desktopViewSettings = settings;
        foreach (var surface in _desktopSurfaces.Values) surface.ApplyDesktopSettings(settings);
    }

    internal void ReorderDesktopSurfaces()
    {
        foreach (var surface in _desktopSurfaces.Values) surface.ReorderDesktopIcons();
    }

    private void RenderDesktopSurface(IReadOnlyList<DesktopItem> items, Dictionary<string, System.Windows.Point>? positions = null)
    {
        using var trace = StartupTrace.Measure("surface.render-ui", $"count={items.Count}");
        items = DesktopShellState.Merge(items, _latestShellItems);
        var desktop = Boxes.First(b => b.IsUnsorted);
        try
        {
            _surfaceItems = items;
            var monitors = DesktopMonitors.Capture();
            bool topologyChanged = !_desktopMonitors.SequenceEqual(monitors);
            _desktopMonitors = monitors;
            if (_desktopMonitors.Count == 0) return;
            var primary = DesktopMonitorLayout.Primary(_desktopMonitors);
            var config = AppConfig.Current;
            bool changed = DesktopMonitorLayout.BindLegacy(config.DesktopIconPositions.Values, primary.Id);
            foreach (var id in _desktopSurfaces.Keys.Where(id => !_desktopMonitors.Any(m => m.Id == id)).ToArray())
            {
                _desktopSurfaces[id].Close(); _desktopSurfaces.Remove(id);
            }
            var settings = _desktopViewSettings ??= DesktopShellMenu.CaptureSettings();
            foreach (var monitor in _desktopMonitors)
            {
                if (!_desktopSurfaces.TryGetValue(monitor.Id, out var surface))
                {
                    surface = StartupTrace.Run("surface.construct", () => new FenceBoxWindow(desktop, this, desktopSurface: true, desktopMonitor: monitor));
                    _desktopSurfaces.Add(monitor.Id, surface);
                    using (StartupTrace.Measure("surface.show")) surface.Show();
                    using (StartupTrace.Measure("surface.place")) surface.PlaceOnDesktop();
                }
                surface.UpdateDesktopMonitor(monitor);
                if (settings is { } current) surface.ApplyDesktopSettings(current);
                if (positions is { Count: > 0 }) surface.ImportDesktopPositions(positions);
            }
            if (positions is { Count: > 0 } && !config.DesktopMultiMonitorImported)
            { config.DesktopMultiMonitorImported = config.DesktopIconPositionsImported = true; changed = true; }
            var loose = ResolveBoxItems(desktop, items);
            foreach (var item in loose)
            {
                string key = DesktopPositionKey(item.Path);
                if (config.DesktopIconPositions.ContainsKey(key)) continue;
                config.DesktopIconPositions[key] = new() { MonitorId = primary.Id, HasPosition = false };
                changed = true;
            }
            if (changed) config.Save();
            var groups = DesktopMonitorLayout.Partition(loose,
                item => config.DesktopIconPositions.GetValueOrDefault(DesktopPositionKey(item.Path)), _desktopMonitors);
            foreach (var monitor in _desktopMonitors)
            {
                var surface = _desktopSurfaces[monitor.Id];
                surface.InvalidateDesktopPositions();
                surface.Render(groups[monitor.Id]);
            }
            if (topologyChanged)
                foreach (var box in _windows.Values) box.RecoverDesktopBoxBounds(_desktopMonitors);
            // Hide Explorer only after the replacement window and data exist.
            _desktopLease ??= DesktopSurfaceLease.Acquire();
            // The surface must stay below every box, including after a click.
            KeepDesktopSurfaceBehind();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"DESKTOP-SURFACE failed: {ex}\n");
            AppConfig.Current.DesktopUnifiedSurface = false;
            AppConfig.Current.Save();
        }
    }

    private void StartDesktopSurfaceWatching()
    {
        StartLiveRecovery();
        _membershipProbeTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        { Interval = TimeSpan.FromSeconds(30) };
        _membershipProbeTimer.Tick += async (_, _) =>
        {
            if (!_active || _membershipProbeRunning) return;
            _membershipProbeRunning = true;
            long version = _renderVersion.Current;
            try
            {
                var snapshot = await System.Threading.Tasks.Task.Run(() =>
                    DesktopMembershipSnapshot.Capture(DesktopItems.DesktopFolders()));
                if (!_active || !_renderVersion.IsCurrent(version)) return;
                // Fallback for silently lost watcher events. Stable desktops do
                // not rebuild controls, call Shell for names or write config.
                if (_membershipSnapshot is null || !snapshot.SameContents(_membershipSnapshot)) Render();
            }
            catch (Exception ex) { DiagnosticLog.Write($"DESKTOP-MEMBERSHIP probe: {ex.GetType().Name}\n"); }
            finally { _membershipProbeRunning = false; }
        };
        _membershipProbeTimer.Start();
        _desktopRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        { Interval = TimeSpan.FromMilliseconds(400) };
        _desktopRefreshTimer.Tick += (_, _) =>
        {
            _desktopRefreshTimer?.Stop();
            if (_active) Render();
        };
        if (_unifiedSurface)
        {
            _desktopTopologyTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            { Interval = TimeSpan.FromMilliseconds(1000) };
            _desktopTopologyTimer.Tick += (_, _) =>
            {
                _desktopTopologyTimer?.Stop();
                if (!_active || !_unifiedSurface) return;
                if (_surfaceItems is { } items) RenderDesktopSurface(items);
                foreach (var box in _windows.Values) box.RecoverDesktopBoxBounds(_desktopMonitors);
                DiagnosticLog.Write($"DESKTOP-MONITORS topology screens={_desktopMonitors.Count}\n");
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDesktopDisplaysChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch += OnDesktopSessionSwitch;
            // The guard discovers HWND changes off the UI thread. No repeated COM
            // desktop enumeration here: only consume its recovery notification.
            _desktopRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
            { Interval = TimeSpan.FromSeconds(1) };
            _desktopRecoveryTimer.Tick += (_, _) =>
            {
                if (!_active || !_unifiedSurface) return;
                if (_desktopLease?.ConsumeRecovery() == true) _desktopRecoveryRetries = 5;
                if (_desktopRecoveryRetries <= 0) return;
                _desktopRecoveryRetries--;
                if (DesktopShellMenu.CaptureSettings() is { } settings)
                {
                    SyncDesktopSurfaceSettings(settings);
                    _desktopRecoveryRetries = 0;
                }
                KeepDesktopSurfaceBehind();
                if (!IsPeeking)
                    foreach (var window in _windows.Values) window.PlaceOnDesktop();
                KeepDesktopSurfaceBehind();
                Render();
                DiagnosticLog.Write("DESKTOP-SURFACE recovered Explorer view\n");
            };
            _desktopRecoveryTimer.Start();
        }
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(folder);
            if (!Directory.Exists(path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false };
                watcher.Created += DesktopSurfaceChanged;
                watcher.Deleted += DesktopSurfaceChanged;
                watcher.Changed += DesktopSurfaceChanged;
                watcher.Renamed += DesktopSurfaceChanged;
                watcher.Error += (_, _) =>
                {
                    DiagnosticLog.Write("DESKTOP-SURFACE watcher overflow/error; scheduling identity reconciliation\n");
                    QueueDesktopSurfaceRefresh();
                };
                _desktopWatchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { DiagnosticLog.Write($"DESKTOP-SURFACE watcher: {ex.Message}\n"); }
        }
    }

    private void DesktopSurfaceChanged(object sender, FileSystemEventArgs e)
    {
        ThumbnailLoader.Invalidate(e.FullPath);
        if (e is RenamedEventArgs renamed) ThumbnailLoader.Invalidate(renamed.OldFullPath);
        _ui.BeginInvoke(new Action(() =>
        {
            // A disposed watcher can already have callbacks queued across disable/enable.
            if (!_active || sender is not FileSystemWatcher watcher || !_desktopWatchers.Contains(watcher)) return;
            if (e is RenamedEventArgs rename)
            {
                if (ApplyPathRename(rename.OldFullPath, rename.FullPath)) AppConfig.Current.Save();
                DiagnosticLog.Write("DESKTOP-SURFACE external rename applied\n");
            }
            _renderVersion.Next(); // Reject an enumeration begun before this change.
            _desktopRefreshTimer?.Stop();
            _desktopRefreshTimer?.Start();
        }));
    }

    private void QueueDesktopSurfaceRefresh() => _ui.BeginInvoke(new Action(() =>
    {
        if (!_active) return;
        _renderVersion.Next();
        _desktopRefreshTimer?.Stop();
        _desktopRefreshTimer?.Start();
    }));

    private void StopDesktopSurface()
    {
        StopLiveRecovery();
        _desktopRefreshTimer?.Stop();
        _desktopRefreshTimer = null;
        _membershipProbeTimer?.Stop();
        _membershipProbeTimer = null;
        _desktopRecoveryTimer?.Stop();
        _desktopRecoveryTimer = null;
        _desktopRecoveryRetries = 0;
        _membershipSnapshot = null;
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        _desktopWatchers.Clear();
        _desktopLease?.Dispose();
        _desktopLease = null;
        _desktopTopologyTimer?.Stop();
        _desktopTopologyTimer = null;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDesktopDisplaysChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnDesktopSessionSwitch;
        foreach (var surface in _desktopSurfaces.Values) surface.Close();
        _desktopSurfaces.Clear();
        _surfaceItems = null;
        _allDesktopItems = null;
        _desktopMonitors = [];
        _desktopViewSettings = null;
        _desktopDistributionQueued = false;
        ActiveDesktopDrag = null;
        DesktopDragToken = null;
    }
}
