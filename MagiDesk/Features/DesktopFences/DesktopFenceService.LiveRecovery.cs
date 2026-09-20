using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Infrastructure;
using MagiDesk.Native;

namespace MagiDesk.Features.DesktopFences;

public sealed partial class DesktopFenceService
{
    private DesktopShellChanges? _shellChanges;
    private DispatcherTimer? _liveTimer;
    private int _liveGeneration;
    private bool _liveBusy;
    private bool _shellDirty;
    private string? _shellState;
    private DateTime _nextLiveCheck;
    private IReadOnlyList<DesktopItem>? _latestShellItems;
    private readonly Dictionary<string, bool> _mappedRootAvailability = new();

    private void StartLiveRecovery()
    {
        _liveGeneration++;
        _liveBusy = false; _shellDirty = true; _nextLiveCheck = DateTime.MinValue;
        if (_unifiedSurface)
        {
            try { _shellChanges = new DesktopShellChanges(() => _shellDirty = true); }
            catch (Exception ex) { DiagnosticLog.Write($"DESKTOP-SHELL monitor: {ex.GetType().Name}\n"); }
        }
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += async (_, _) => await ProbeLiveRecovery();
        _liveTimer.Start();
    }

    private async Task ProbeLiveRecovery()
    {
        if (!_active || _liveBusy) return;
        bool fallback = DateTime.UtcNow >= _nextLiveCheck;
        if (!fallback && !_shellDirty) return;
        if (fallback) _nextLiveCheck = DateTime.UtcNow.AddSeconds(5);
        bool dirty = _shellDirty; _shellDirty = false;
        int generation = _liveGeneration;
        bool unified = _unifiedSurface;
        string? previousState = _shellState;
        var ids = (_surfaceItems ?? []).Where(i => i.IsShellItem).Select(i => i.Path).ToArray();
        var roots = fallback ? Boxes.Where(b => b.FolderPath is not null)
            .Select(b => new MappedFolderRecovery.Request(b.Id, b.FolderPath!, b.FolderIdentity)).ToArray() : [];
        _liveBusy = true;
        try
        {
            var result = await Task.Run(() =>
            {
                var recovered = roots.Select(r => MappedFolderRecovery.Probe(r)).ToArray();
                string? state = unified ? DesktopShellState.Capture(ids) : null;
                IReadOnlyList<DesktopItem>? icons = null;
                DesktopShellMenu.ViewSettings? settings = null;
                if (unified && (dirty || (state is not null && state != previousState)))
                {
                    var captured = DesktopShellMenu.CaptureNamespaceItems(out bool success);
                    if (success) { icons = captured; settings = DesktopShellMenu.CaptureSettings(); }
                }
                return (recovered, state, icons, settings);
            });
            if (!_active || generation != _liveGeneration) return;
            bool save = false, moved = false;
            foreach (var recovered in result.recovered)
            {
                var box = Boxes.FirstOrDefault(b => b.Id == recovered.Original.BoxId);
                if (box is null || !MappedFolderRecovery.IsCurrent(recovered, box.FolderPath, box.FolderIdentity)) continue;
                bool available = recovered.RootMatches || recovered.RecoveredPath is not null;
                if ((!_mappedRootAvailability.TryGetValue(box.Id, out bool oldAvailable) && !available)
                    || (_mappedRootAvailability.ContainsKey(box.Id) && oldAvailable != available))
                    if (_windows.TryGetValue(box.Id, out var window)) window.RefreshRecoveredFolder();
                _mappedRootAvailability[box.Id] = available;
                if (box.FolderIdentity != recovered.Identity) { box.FolderIdentity = recovered.Identity; save = true; }
                if (recovered.RecoveredPath is { } path && !string.Equals(path, box.FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyPathRename(box.FolderPath!, path);
                    save = moved = true;
                    DiagnosticLog.Write($"FENCE-ROOT recovered box={box.Id}\n");
                }
            }
            if (save) AppConfig.Current.Save();
            if (moved) Render();
            if (result.icons is { } icons)
            {
                // Registry/UI changes can precede Explorer's view update; perform one bounded follow-up.
                if (previousState is not null && result.state != previousState) _shellDirty = true;
                _shellState = result.state;
                foreach (var id in ids.Concat(icons.Select(i => i.Path)).Distinct()) ThumbnailLoader.Invalidate(id);
                bool structureChanged = !DesktopShellState.SameItems(
                    _latestShellItems ?? (_surfaceItems ?? []).Where(i => i.IsShellItem).ToArray(), icons);
                _latestShellItems = icons;
                if (result.settings is { } settings) SyncDesktopSurfaceSettings(settings);
                if (structureChanged && _surfaceItems is { } current) RenderDesktopSurface(current);
                else foreach (var surface in _desktopSurfaces.Values) surface.RefreshShellThumbnails();
                DiagnosticLog.Write($"DESKTOP-SHELL refreshed namespace count={icons.Count}\n");
            }
            else if (unified && (dirty || result.state != previousState)) _shellDirty = true; // Retry transient Explorer failure.
        }
        catch (Exception ex) { DiagnosticLog.Write($"DESKTOP-LIVE probe: {ex.GetType().Name}\n"); }
        finally { if (generation == _liveGeneration) _liveBusy = false; }
    }

    private void StopLiveRecovery()
    {
        _liveGeneration++;
        _liveTimer?.Stop(); _liveTimer = null;
        _shellChanges?.Dispose(); _shellChanges = null;
        _latestShellItems = null; _shellState = null; _liveBusy = false; _shellDirty = false;
        _mappedRootAvailability.Clear();
    }
}
