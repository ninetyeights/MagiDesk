using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Features.BrowserBadges;
using MagiDesk.Native;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Persistent floating strip of Chrome-profile avatar buttons. Click launches
/// or focuses the profile; drag on the background moves the whole dock.
/// Window is topmost + no-activate + no-taskbar so it behaves like the
/// Windows taskbar without stealing focus.
/// </summary>
public partial class ProfileDockWindow : Window
{
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TOOLWINDOW  = 0x80;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    // Drag state.
    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private int _dragStartWinX, _dragStartWinY;

    // Per-profile-directory reference to the state-indicator rectangle below
    // each button, so we can update width/opacity without rebuilding buttons.
    private readonly Dictionary<string, System.Windows.Shapes.Rectangle> _indicators = new();
    // Per-profile border wrapper — its Background is lit with a translucent
    // accent tint when the profile's window is in the foreground.
    private readonly Dictionary<string, Border> _buttonWrappers = new();

    /// <summary>Fired when the user clicks a profile button.</summary>
    public event Action<ChromeProfile>? ProfileClicked;

    public ProfileDockWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        // NOACTIVATE keeps the dock from stealing focus when clicked;
        // TOOLWINDOW hides it from Alt-Tab.
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Rebuild the buttons from the current profile catalog. Safe to
    /// call repeatedly — each call wipes the panel and reconstructs it.
    /// <paramref name="separator"/> picks between: Gap (empty space), Line
    /// (thin divider), Label (group name above each group), Bordered
    /// (tinted rounded container per group).</summary>
    public void SetProfiles(IReadOnlyList<(string? Name, IReadOnlyList<ChromeProfile> Profiles)> groups,
                            int buttonSize,
                            DockGroupSeparator separator)
    {
        ButtonPanel.Children.Clear();
        _indicators.Clear();
        _buttonWrappers.Clear();
        var cfg = AppConfig.Current;

        bool first = true;
        foreach (var (name, members) in groups)
        {
            if (members.Count == 0) continue;
            if (!first) AddSeparator(separator);
            first = false;

            var groupContainer = BuildGroupContainer(name, members, buttonSize, separator, cfg);
            ButtonPanel.Children.Add(groupContainer);
        }
    }

    private void AddSeparator(DockGroupSeparator style)
    {
        switch (style)
        {
            case DockGroupSeparator.Gap:
                ButtonPanel.Children.Add(new Border { Width = 14 });
                break;
            case DockGroupSeparator.Line:
                ButtonPanel.Children.Add(new Border
                {
                    Width  = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                });
                break;
            case DockGroupSeparator.Label:
            case DockGroupSeparator.Bordered:
                // Both visually communicate group boundaries via the group
                // container itself (label text / border); between groups just
                // need a small gap to breathe.
                ButtonPanel.Children.Add(new Border { Width = 8 });
                break;
        }
    }

    private FrameworkElement BuildGroupContainer(
        string? groupName, IReadOnlyList<ChromeProfile> members,
        int buttonSize, DockGroupSeparator separator, AppConfig cfg)
    {
        // Horizontal row of profile buttons — shared by all separator styles.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in members)
        {
            var settings = cfg.BrowserProfiles.TryGetValue(p.Directory, out var s)
                ? s : new BrowserProfileSettings();
            row.Children.Add(BuildProfileButton(p, settings, buttonSize));
        }

        // Wrap according to style.
        if (separator == DockGroupSeparator.Label && !string.IsNullOrEmpty(groupName))
        {
            // Name text + buttons, wrapped in a subtle rounded border so the
            // label is clearly tied to its group rather than floating.
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(new TextBlock
            {
                Text = groupName,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
            });
            stack.Children.Add(row);
            return new Border
            {
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(4, 2, 4, 2),
                Background      = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child           = stack,
            };
        }
        if (separator == DockGroupSeparator.Bordered)
        {
            return new Border
            {
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(4, 2, 4, 2),
                Background      = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child           = row,
            };
        }
        return row;
    }

    /// <summary>Apply per-profile state indicators. <paramref name="states"/>
    /// maps profile directory → (hasOpenWindows, isForegroundProfile).
    /// Profiles missing from the map render no indicator.</summary>
    public void UpdateStates(IReadOnlyDictionary<string, (bool HasWindows, bool IsForeground)> states)
    {
        var accent   = GetAccentColor();
        var tintFg   = new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B));
        var strokeFg = new SolidColorBrush(Color.FromArgb(0xFF, accent.R, accent.G, accent.B));
        var strokeRun= new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B));

        foreach (var (dir, rect) in _indicators)
        {
            var wrapper = _buttonWrappers[dir];
            if (!states.TryGetValue(dir, out var s) || !s.HasWindows)
            {
                rect.Visibility   = Visibility.Hidden;
                wrapper.Background = System.Windows.Media.Brushes.Transparent;
                continue;
            }
            rect.Visibility = Visibility.Visible;
            // Height stays 4 in both states (space is already reserved even
            // when Hidden) — only width and color change.
            if (s.IsForeground)
            {
                rect.Width   = rect.Tag is double full ? full : double.NaN;
                rect.Fill    = strokeFg;
                wrapper.Background = tintFg;
            }
            else
            {
                rect.Width   = rect.Tag is double full2 ? full2 * 0.3 : double.NaN;
                rect.Fill    = strokeRun;
                wrapper.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    private static Color GetAccentColor()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int raw)
            {
                uint v = (uint)raw;
                byte a = (byte)((v >> 24) & 0xFF);
                return Color.FromArgb(a == 0 ? (byte)0xFF : a,
                    (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private Button BuildProfileButton(ChromeProfile p, BrowserProfileSettings s, int size)
    {
        // Avatar visual — reuses the badge's rendering so dock icons match
        // whatever the user customised in the profile editor.
        var host = new Border
        {
            Width  = size,
            Height = size,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var overlayCanvas = new Canvas { IsHitTestVisible = false };
        var text = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var innerGrid = new Grid();
        innerGrid.Children.Add(overlayCanvas);
        innerGrid.Children.Add(text);
        host.Child = innerGrid;
        ApplyAvatarVisual(host, text, overlayCanvas, p, s, size);

        // Taskbar-style state indicator pinned beneath the avatar. Fixed
        // height (4px) even when hidden — the Rectangle always reserves its
        // vertical slot so the overall dock height stays constant across
        // state transitions. Differentiation between "running" and
        // "foreground" uses width + color, not height.
        var indicator = new System.Windows.Shapes.Rectangle
        {
            Height = 4,
            Width  = size * 0.3,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 2, 0, 0),
            RadiusX    = 2,
            RadiusY    = 2,
            Visibility = Visibility.Hidden,
            Tag        = (double)size,  // stash full bar width for foreground state
        };
        _indicators[p.Directory] = indicator;

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(host);
        stack.Children.Add(indicator);

        // Inner wrapper Border: background tinted when this profile is the
        // foreground window, so the whole button area lights up (stronger
        // cue than the thin indicator bar alone).
        var wrapper = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(4, 3, 4, 2),
            Background   = System.Windows.Media.Brushes.Transparent,
            Child        = stack,
        };
        _buttonWrappers[p.Directory] = wrapper;

        var btn = new Button
        {
            Content = wrapper,
            Padding = new Thickness(0),
            Margin  = new Thickness(2, 0, 2, 0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = p.Name,
            VerticalContentAlignment = VerticalAlignment.Top,
            ContextMenu = BuildProfileContextMenu(p, s),
            AllowDrop = true,
        };
        btn.Click += (_, _) => ProfileClicked?.Invoke(p);
        AttachDragDrop(btn, p.Directory);
        return btn;
    }

    /// <summary>Wire WPF drag-drop on a profile button so the user can
    /// reorder profiles by dragging directly on the dock. Drop position
    /// (cursor X relative to the target button's center) decides
    /// before/after; cross-group drops move the profile into the target's
    /// group at the drop position.</summary>
    private void AttachDragDrop(Button btn, string sourceDir)
    {
        Point? dragOrigin = null;
        btn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Record start point — we only initiate drag once the cursor
            // moves past the system-defined drag threshold, so a regular
            // click still goes through to btn.Click.
            dragOrigin = e.GetPosition(this);
        };
        btn.PreviewMouseMove += (s2, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragOrigin is null) return;
            // Lock check is on drag-start, not button construction, so toggling
            // the lock takes effect immediately for the next drag without
            // needing to rebuild the dock.
            if (AppConfig.Current.BrowserDockLocked) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - dragOrigin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragOrigin.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragOrigin = null;
            DragDrop.DoDragDrop(btn, new DataObject("MagiDesk.ProfileDir", sourceDir), DragDropEffects.Move);
        };
        btn.PreviewMouseLeftButtonUp += (_, _) => dragOrigin = null;

        btn.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent("MagiDesk.ProfileDir") && !AppConfig.Current.BrowserDockLocked
                      ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        btn.Drop += (_, e) =>
        {
            if (AppConfig.Current.BrowserDockLocked) return;
            if (!e.Data.GetDataPresent("MagiDesk.ProfileDir")) return;
            var src = (string?)e.Data.GetData("MagiDesk.ProfileDir");
            if (string.IsNullOrEmpty(src) || string.Equals(src, sourceDir, StringComparison.OrdinalIgnoreCase)) return;
            var dropPos = e.GetPosition(btn);
            bool insertAfter = dropPos.X > btn.ActualWidth / 2;
            ReorderProfile(src, sourceDir, insertAfter);
            e.Handled = true;
        };
    }

    /// <summary>Move <paramref name="sourceDir"/> next to <paramref name="targetDir"/>
    /// (before or after based on <paramref name="insertAfter"/>). Handles all
    /// four cases — within group, across groups, group → ungrouped,
    /// ungrouped → ungrouped — by adjusting either the target group's
    /// <c>ProfileDirs</c> or <see cref="AppConfig.BrowserDockUngroupedOrder"/>.</summary>
    private static void ReorderProfile(string sourceDir, string targetDir, bool insertAfter)
    {
        var cfg = AppConfig.Current;

        // Where is the target? (null = ungrouped section)
        var targetGroup = cfg.BrowserDockGroups.FirstOrDefault(
            g => g.ProfileDirs.Any(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase)));

        // Detach source from its current location.
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));
        cfg.BrowserDockUngroupedOrder.RemoveAll(d => d.Equals(sourceDir, StringComparison.OrdinalIgnoreCase));

        if (targetGroup is not null)
        {
            int idx = targetGroup.ProfileDirs.FindIndex(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) targetGroup.ProfileDirs.Add(sourceDir);
            else targetGroup.ProfileDirs.Insert(insertAfter ? idx + 1 : idx, sourceDir);
        }
        else
        {
            // Target is ungrouped. Lazily seed the explicit order from the
            // current catalog the first time the user reorders, so dropping
            // before/after a profile that hasn't been touched yet still
            // produces a stable result.
            var order = cfg.BrowserDockUngroupedOrder;
            if (!order.Any(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase)))
            {
                var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in cfg.BrowserDockGroups)
                    foreach (var d in g.ProfileDirs) allocated.Add(d);
                foreach (var pp in App.BrowserBadges?.Profiles ?? Enumerable.Empty<ChromeProfile>())
                {
                    if (allocated.Contains(pp.Directory)) continue;
                    if (order.Any(d => d.Equals(pp.Directory, StringComparison.OrdinalIgnoreCase))) continue;
                    order.Add(pp.Directory);
                }
            }
            int idx = order.FindIndex(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { order.Add(targetDir); idx = order.Count - 1; }
            order.Insert(insertAfter ? idx + 1 : idx, sourceDir);
        }

        // Sweep empty groups left behind by the move.
        cfg.BrowserDockGroups.RemoveAll(g => g.ProfileDirs.Count == 0);
        cfg.Save();
    }

    /// <summary>Right-click menu on a dock profile button. Actions are local
    /// (toggle visibility, edit avatar, close windows, group membership) — no
    /// service callback needed because <see cref="AppConfig.Save"/> and
    /// <c>App.BrowserBadges</c> already cover the side effects.</summary>
    private static ContextMenu BuildProfileContextMenu(ChromeProfile p, BrowserProfileSettings s)
    {
        var menu = new ContextMenu();
        var cfg = AppConfig.Current;
        var currentGroup = cfg.BrowserDockGroups.FirstOrDefault(
            g => g.ProfileDirs.Any(d => d.Equals(p.Directory, StringComparison.OrdinalIgnoreCase)));
        bool locked = cfg.BrowserDockLocked;

        var lockItem = new MenuItem
        {
            Header      = "锁定排序",
            IsCheckable = true,
            IsChecked   = locked,
        };
        // Toggle on Click rather than Checked/Unchecked so the persisted
        // value flips even when WPF's checked-state restoration races the
        // user's click after a dock rebuild.
        lockItem.Click += (_, _) =>
        {
            AppConfig.Current.BrowserDockLocked = !AppConfig.Current.BrowserDockLocked;
            AppConfig.Current.Save();
        };
        menu.Items.Add(lockItem);
        menu.Items.Add(new Separator());

        var disable = new MenuItem { Header = "禁用 (从 dock 隐藏)" };
        disable.Click += (_, _) =>
        {
            // Persist via the same field BrowserBadgesPage uses — toggles both
            // the floating badge and the dock button via Changed → RefreshDock.
            s.Visible = false;
            AppConfig.Current.BrowserProfiles[p.Directory] = s;
            AppConfig.Current.Save();
        };
        menu.Items.Add(disable);

        var edit = new MenuItem { Header = "编辑徽标..." };
        edit.Click += (_, _) =>
        {
            var dlg = new AvatarTextEditorWindow(p, s) { Owner = null };
            if (dlg.ShowDialog() == true)
            {
                AppConfig.Current.BrowserProfiles[p.Directory] = s;
                AppConfig.Current.Save();
            }
        };
        menu.Items.Add(edit);

        menu.Items.Add(new Separator());

        // Group membership submenu — disabled while the dock is locked.
        var moveTo = new MenuItem { Header = "移动到分组", IsEnabled = !locked };
        foreach (var g in cfg.BrowserDockGroups)
        {
            var captured = g;
            var item = new MenuItem
            {
                Header     = string.IsNullOrEmpty(g.Name) ? "(未命名)" : g.Name,
                IsCheckable = true,
                IsChecked  = ReferenceEquals(currentGroup, g),
            };
            item.Click += (_, _) => MoveProfileToGroup(p.Directory, captured);
            moveTo.Items.Add(item);
        }
        if (cfg.BrowserDockGroups.Count > 0) moveTo.Items.Add(new Separator());
        var newGroup = new MenuItem { Header = "新建分组..." };
        newGroup.Click += (_, _) =>
        {
            var name = PromptForText("新建分组", "分组名称：", string.Empty);
            if (string.IsNullOrWhiteSpace(name)) return;
            var grp = new BrowserDockGroup { Name = name.Trim() };
            AppConfig.Current.BrowserDockGroups.Add(grp);
            MoveProfileToGroup(p.Directory, grp);
        };
        moveTo.Items.Add(newGroup);
        menu.Items.Add(moveTo);

        var removeFromGroup = new MenuItem
        {
            Header   = "从分组中移除",
            IsEnabled = currentGroup is not null && !locked,
        };
        removeFromGroup.Click += (_, _) => RemoveProfileFromAllGroups(p.Directory);
        menu.Items.Add(removeFromGroup);

        menu.Items.Add(new Separator());

        var closeAll = new MenuItem { Header = "关闭该 profile 的所有窗口" };
        closeAll.Click += (_, _) =>
        {
            // Find all top-level Chrome HWNDs for this profile (incl. iconic)
            // and post WM_CLOSE so Chrome runs its own clean-shutdown path
            // (saves session, prompts on unsaved tabs).
            var hwnds = App.BrowserBadges?.FindWindowsForProfile(p.Directory) ?? new List<IntPtr>();
            foreach (var h in hwnds)
                NativeMethods.PostMessage(h, NativeConstants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        };
        menu.Items.Add(closeAll);

        return menu;
    }

    /// <summary>Move <paramref name="profileDir"/> into <paramref name="target"/>,
    /// removing it from any other group it currently belongs to. Saves config
    /// (triggers dock rebuild via <see cref="AppConfig.Changed"/>).</summary>
    private static void MoveProfileToGroup(string profileDir, BrowserDockGroup target)
    {
        var cfg = AppConfig.Current;
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(profileDir, StringComparison.OrdinalIgnoreCase));
        if (!target.ProfileDirs.Any(d => d.Equals(profileDir, StringComparison.OrdinalIgnoreCase)))
            target.ProfileDirs.Add(profileDir);
        cfg.Save();
    }

    private static void RemoveProfileFromAllGroups(string profileDir)
    {
        var cfg = AppConfig.Current;
        foreach (var g in cfg.BrowserDockGroups)
            g.ProfileDirs.RemoveAll(d => d.Equals(profileDir, StringComparison.OrdinalIgnoreCase));
        // Drop now-empty groups so the BrowserBadges settings page doesn't
        // accumulate stale entries — same cleanup the settings UI does.
        cfg.BrowserDockGroups.RemoveAll(g => g.ProfileDirs.Count == 0);
        cfg.Save();
    }

    /// <summary>Tiny inline modal: TextBox + OK/Cancel. Returns null on cancel
    /// or empty input. Used for "新建分组" — too small to justify its own
    /// xaml file, and avoids pulling in Microsoft.VisualBasic.</summary>
    private static string? PromptForText(string title, string prompt, string defaultValue)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 360, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Manual,
        };
        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
        var input = new TextBox { Text = defaultValue, MinWidth = 300 };
        var okBtn     = new Button { Content = "确定",  Width = 80, IsDefault = true,  Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "取消",  Width = 80, IsCancel  = true };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(label);
        root.Children.Add(input);
        root.Children.Add(btnRow);
        dlg.Content = root;
        string? result = null;
        okBtn.Click += (_, _) => { result = input.Text; dlg.DialogResult = true; };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dlg.ShowDialog() == true ? result : null;
    }

    private static void ApplyAvatarVisual(Border host, TextBlock text, Canvas overlay,
        ChromeProfile p, BrowserProfileSettings s, double size)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "magidesk.log"),
                $"{DateTime.Now:HH:mm:ss.fff} DOCK-AVATAR dir='{p.Directory}' name='{p.Name}' text='{s.AvatarText ?? "<null>"}' bg='{s.AvatarBgHex ?? "<null>"}' shape={s.AvatarShape}\n");
        }
        catch { }
        // Shape (inset or edge-to-edge).
        bool edgeToEdge = s.AvatarShape == AvatarShape.Rectangle || s.AvatarShape == AvatarShape.Square;
        if (edgeToEdge)
        {
            host.CornerRadius = s.AvatarShape == AvatarShape.Square
                ? new CornerRadius(0) : new CornerRadius(2);
            host.Clip = null;
        }
        else
        {
            BadgeWindow.ApplyInsetShape(host, s.AvatarShape, size);
        }
        text.FontSize = size * 0.5;

        // Color resolution mirrors BadgeWindow.ApplyProfile so the dock icon
        // and the floating badge render identically for the same profile:
        //   CustomAvatarPath > AvatarText (with AvatarBgHex / profile theme
        //   fallback for the brush primary) > GAIA image > Chrome-theme-color
        //   default with first-letter glyph.
        // Fill: custom image > text+bg > GAIA image > Chrome-theme initial.
        if (s.CustomAvatarPath is { Length: > 0 } cp && System.IO.File.Exists(cp))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(cp);
                bmp.EndInit();
                bmp.Freeze();
                host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                text.Text = string.Empty;
                BadgeWindow.RenderOverlay(overlay, s, BadgeWindow.DefaultAvatarBg, size, edgeToEdge);
                return;
            }
            catch { }
        }

        // AvatarText or default fallback — primary color prefers user's
        // AvatarBgHex, then the Chrome profile highlight color, then the
        // built-in yellow. Matches BadgeWindow exactly.
        var primary = ParseHex(s.AvatarBgHex)
                      ?? (p.ThemeColorRgb is int themeRgb
                          ? Color.FromRgb((byte)((themeRgb >> 16) & 0xFF), (byte)((themeRgb >> 8) & 0xFF), (byte)(themeRgb & 0xFF))
                          : BadgeWindow.DefaultAvatarBg);
        if (!string.IsNullOrEmpty(s.AvatarText))
        {
            host.Background = BadgeWindow.BuildAvatarBrush(primary, s);
            var fg = ParseHex(s.AvatarTextColorHex)
                     ?? (Luminance(primary) > 0.6 ? Colors.Black : Colors.White);
            text.Foreground = new SolidColorBrush(fg);
            text.Text = s.AvatarText!.Length > 3 ? s.AvatarText![..3] : s.AvatarText!;
            BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
            return;
        }
        if (p.GaiaPicturePath is not null && System.IO.File.Exists(p.GaiaPicturePath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(p.GaiaPicturePath);
                bmp.EndInit();
                bmp.Freeze();
                host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                text.Text = string.Empty;
                BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
                return;
            }
            catch { }
        }
        // Default fallback: Chrome's profile highlight color + initial.
        host.Background = BadgeWindow.BuildAvatarBrush(primary, s);
        text.Foreground = new SolidColorBrush(Luminance(primary) > 0.6 ? Colors.Black : Colors.White);
        text.Text = string.IsNullOrEmpty(p.Name) ? "?" : p.Name[..1].ToUpperInvariant();
        BadgeWindow.RenderOverlay(overlay, s, primary, size, edgeToEdge);
    }

    // ---------------------------------------------------------- drag to reposition

    private void Dock_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Don't hijack clicks on child buttons.
        if (e.OriginalSource is not Border) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        _dragStartWinX = wr.Left;
        _dragStartWinY = wr.Top;
        _dragging = true;
        DockRoot.CaptureMouse();
        e.Handled = true;
    }

    private void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        int dx = pt.X - _dragStartCursor.X;
        int dy = pt.Y - _dragStartCursor.Y;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            _dragStartWinX + dx, _dragStartWinY + dy, 0, 0,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
    }

    private void Dock_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DockRoot.ReleaseMouseCapture();

        // Save new position (DIPs) so the dock comes back here on restart.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        var src = PresentationSource.FromVisual(this);
        double sX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double sY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        AppConfig.Current.BrowserDockX = wr.Left / sX;
        AppConfig.Current.BrowserDockY = wr.Top  / sY;
        AppConfig.Current.Save();
        e.Handled = true;
    }

    // ---------------------------------------------------------- helpers

    private static Color? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return null;
        return Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte b) { double s = b / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
