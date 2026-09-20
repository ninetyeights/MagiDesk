namespace MagiDesk.Features.DesktopFences;

/// <summary>Visibility ownership is scoped to one observed Explorer window lifetime.</summary>
internal sealed class DesktopVisibilityState
{
    private IntPtr _window;
    private uint _pid;
    private bool _present;
    private bool _restoreShown;
    private bool? _initialPreference;

    internal bool Observe(IntPtr window, uint pid, bool visible, bool? preference)
    {
        if (window == IntPtr.Zero) { _present = false; return false; }
        if (_present && window == _window && pid == _pid) return false;
        _window = window;
        _pid = pid;
        _present = true;
        _restoreShown = visible;
        _initialPreference = preference;
        return true;
    }

    internal bool Matches(IntPtr window, uint pid) => _present && window == _window && pid == _pid;

    internal bool RestoreShown(bool? preference) => preference is { } show
        && _initialPreference is { } initial && show != initial ? show : _restoreShown;
}
