using System.Windows.Interop;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.BrowserBadges;

internal sealed class BadgePositionSession(Action finishDragging) : IDisposable
{
    private HwndSource? _source;
    private const int EscapeId = 0x4241;

    internal bool SetEditing(bool editing)
    {
        if (editing == AppConfig.Current.BrowserBadgeUnlocked) return true;
        if (editing)
        {
            _source = new HwndSource(new HwndSourceParameters("MagiDesk badge position adjustment") { ParentWindow = new IntPtr(-3) });
            _source.AddHook(Hook);
            if (!NativeMethods.RegisterHotKey(_source.Handle, EscapeId, 0x4000, 0x1B))
            {
                _source.Dispose(); _source = null;
                return false;
            }
            AppConfig.Current.BrowserBadgeUnlocked = true;
        }
        else
        {
            AppConfig.Current.BrowserBadgeUnlocked = false;
            ReleaseEscape();
            finishDragging();
        }
        AppConfig.Current.Save();
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == EscapeId)
        {
            handled = true;
            _source?.Dispatcher.BeginInvoke(new Action(() => SetEditing(false)));
        }
        return IntPtr.Zero;
    }

    private void ReleaseEscape()
    {
        if (_source is null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, EscapeId);
        _source.Dispose(); _source = null;
    }

    public void Dispose()
    {
        AppConfig.Current.BrowserBadgeUnlocked = false;
        ReleaseEscape();
    }
}
