using System.IO;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Infrastructure;

namespace MagiDesk.Features.DesktopFences;

public sealed partial class DesktopFenceService
{
    private bool _unifiedSurface;
    private FenceBoxWindow? _desktopSurface;
    private DesktopSurfaceLease? _desktopLease;
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private DispatcherTimer? _desktopRefreshTimer;

    internal void KeepDesktopSurfaceBehind() => _desktopSurface?.PlaceOnDesktop();

    private void RenderDesktopSurface(IReadOnlyList<DesktopItem> items)
    {
        var desktop = Boxes.First(b => b.IsUnsorted);
        try
        {
            if (_desktopSurface is null)
            {
                _desktopSurface = new FenceBoxWindow(desktop, this, desktopSurface: true);
                _desktopSurface.Show();
                _desktopSurface.PlaceOnDesktop();
                if (DesktopShellMenu.CaptureSettings() is { } settings) _desktopSurface.ApplyDesktopSettings(settings);
            }
            _desktopSurface.Render(ResolveBoxItems(desktop, items));
            // Hide Explorer only after the replacement window and data exist.
            _desktopLease ??= DesktopSurfaceLease.Acquire();
            // The surface must stay below every box, including after a click.
            _desktopSurface.PlaceOnDesktop();
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
        _desktopRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        { Interval = TimeSpan.FromMilliseconds(400) };
        _desktopRefreshTimer.Tick += (_, _) =>
        {
            _desktopRefreshTimer?.Stop();
            if (_active && _unifiedSurface) Render();
        };
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
                watcher.Error += (_, _) => QueueDesktopSurfaceRefresh();
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
        QueueDesktopSurfaceRefresh();
    }

    private void QueueDesktopSurfaceRefresh() => _ui.BeginInvoke(new Action(() =>
    {
        if (!_active || !_unifiedSurface) return;
        _desktopRefreshTimer?.Stop();
        _desktopRefreshTimer?.Start();
    }));

    private void StopDesktopSurface()
    {
        _desktopRefreshTimer?.Stop();
        _desktopRefreshTimer = null;
        foreach (var watcher in _desktopWatchers) watcher.Dispose();
        _desktopWatchers.Clear();
        _desktopLease?.Dispose();
        _desktopLease = null;
        _desktopSurface?.Close();
        _desktopSurface = null;
    }
}
