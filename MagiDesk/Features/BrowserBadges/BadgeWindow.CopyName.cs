using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.BrowserBadges;

public partial class BadgeWindow
{
    private static readonly ConcurrentDictionary<IntPtr, BadgeWindow> CopyWindows = new();
    private static DispatcherTimer? _copyTimer;
    private string _copyName = "";
    private bool _copyPressed, _copyBusy;
    private bool? _lastCopyMode;
    private DateTime _feedbackUntil;
    private TextBlock? _copyFeedback;

    // Used by the mouse-hook thread; no WPF access or synchronous UI dispatch.
    internal static bool IsBadgeHandle(IntPtr hwnd) => CopyWindows.ContainsKey(hwnd);
    internal static bool IsCopyChord(bool control, bool alt, bool shift, bool windows)
        => control && !alt && !shift && !windows;
    private static bool CopyModifiersDown() => IsCopyChord(
        Down(0x11), Down(0x12), Down(0x10), Down(0x5B) || Down(0x5C));
    private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private void StartCopyInteraction(IntPtr hwnd)
    {
        CopyWindows[hwnd] = this;
        if (_copyTimer is null)
        {
            _copyTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
            _copyTimer.Tick += (_, _) =>
            {
                bool mode = CopyModifiersDown();
                bool hasCursor = NativeMethods.GetCursorPos(out var cursor);
                foreach (var window in CopyWindows.Values)
                {
                    if (window._lastCopyMode != mode)
                    {
                        window._lastCopyMode = mode;
                        window.ApplyUnlockState(AppConfig.Current.BrowserBadgeUnlocked);
                    }
                    window.UpdateHoverOpacity(mode, hasCursor, cursor);
                    if (window._copyFeedback is { } feedback && DateTime.UtcNow >= window._feedbackUntil)
                        feedback.Visibility = Visibility.Collapsed;
                }
            };
        }
        _copyTimer.Start();
        Pill.LostMouseCapture += (_, _) =>
        {
            if (!_copyPressed) return;
            _copyPressed = false;
            ApplyUnlockState(AppConfig.Current.BrowserBadgeUnlocked);
        };
        Closed += (_, _) =>
        {
            CopyWindows.TryRemove(hwnd, out _);
            if (CopyWindows.IsEmpty) _copyTimer.Stop();
        };
    }

    // Click-through windows do not receive MouseEnter/Leave. Reuse the shared
    // copy-interaction tick and physical HWND bounds, including on mixed-DPI screens.
    // Keep the HWND visible so a transparent badge can detect the cursor leaving.
    private void UpdateHoverOpacity(bool copyMode, bool hasCursor, NativeMethods.POINT cursor)
    {
        if (!IsVisible) return;
        bool interactive = CanAdjustPosition || copyMode || _copyPressed || _dragging;
        bool hover = !interactive && hasCursor
            && NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect)
            && cursor.X >= rect.Left && cursor.X < rect.Right
            && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        double opacity = hover ? 0 : copyMode ? 0.85 : 1;
        if (Pill.Opacity != opacity) Pill.Opacity = opacity;
    }

    private async void CopyProfileName()
    {
        if (_copyBusy || string.IsNullOrEmpty(_copyName)) return;
        _copyBusy = true;
        string name = _copyName;
        bool copied = false;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { Clipboard.SetText(name); copied = true; break; }
                catch (System.Runtime.InteropServices.COMException) { await Task.Delay(50); }
            }
            if (!IsVisible) return;
            if (_copyFeedback is null && Pill.Child is Grid grid)
            {
                _copyFeedback = new TextBlock { IsHitTestVisible = false, Background = Brushes.DimGray,
                    Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                Grid.SetColumnSpan(_copyFeedback, 2);
                grid.Children.Add(_copyFeedback);
            }
            if (_copyFeedback is not null)
            {
                _copyFeedback.Text = copied ? "已复制" : "复制失败";
                _copyFeedback.Visibility = Visibility.Visible;
                _feedbackUntil = DateTime.UtcNow.AddMilliseconds(800);
            }
        }
        finally { _copyBusy = false; }
    }
}
