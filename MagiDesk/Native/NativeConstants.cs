namespace MagiDesk.Native;

internal static class NativeConstants
{
    public const int WH_MOUSE_LL = 14;

    public const int WM_CLOSE         = 0x0010;
    public const int WM_MOUSEMOVE     = 0x0200;
    public const int WM_LBUTTONDOWN   = 0x0201;
    public const int WM_LBUTTONUP     = 0x0202;
    public const int WM_RBUTTONDOWN   = 0x0204;
    public const int WM_RBUTTONUP     = 0x0205;

    public const uint SWP_NOSIZE         = 0x0001;
    public const uint SWP_NOMOVE         = 0x0002;
    public const uint SWP_NOZORDER       = 0x0004;
    public const uint SWP_NOACTIVATE     = 0x0010;
    public const uint SWP_NOOWNERZORDER  = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int VK_MENU    = 0x12;
    public const int VK_CONTROL = 0x11;
    public const int VK_SHIFT   = 0x10;
    public const int VK_LWIN    = 0x5B;
    public const int VK_RWIN    = 0x5C;

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND    = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND   = 0x000B;
    public const uint EVENT_OBJECT_DESTROY        = 0x8001;
    public const uint EVENT_OBJECT_SHOW           = 0x8002;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT      = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS    = 0x0002;
    public const int  OBJID_WINDOW               = 0;

    // DWM
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED               = 14;

    // GetAncestor flags
    public const uint GA_ROOT = 2;

    // ShowWindow
    public const int SW_MINIMIZE       = 6;
    public const int SW_RESTORE        = 9;

    // GetWindowLong
    public const int  GWL_STYLE        = -16;
    public const int  GWL_EXSTYLE      = -20;
    public const uint WS_CAPTION       = 0x00C00000;
    public const int  WS_EX_TOOLWINDOW = 0x00000080;

    // Hit-test codes used as wParam for WM_NCLBUTTONDOWN
    public const int HTCAPTION    = 2;
    public const int HTLEFT       = 10;
    public const int HTRIGHT      = 11;
    public const int HTTOP        = 12;
    public const int HTTOPLEFT    = 13;
    public const int HTTOPRIGHT   = 14;
    public const int HTBOTTOM     = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
}
