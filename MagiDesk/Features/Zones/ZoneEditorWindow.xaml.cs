using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MagiDesk.Config;
using MagiDesk.Native;

namespace MagiDesk.Features.Zones;

/// <summary>
/// Tree-based zone editor. The layout is a <see cref="LayoutTree"/> of
/// nested splits. Structural ops (split/merge/delete) stay local to one
/// subtree, but dragging a divider moves every same-orientation divider that
/// lies on the same grid line in lockstep (<see cref="AttachDividerDrag"/>),
/// so a full row/column line behaves globally even though the tree nests one
/// axis under the other. Dividers that don't share a line stay independent.
///
/// Visuals are built once per structural change (<see cref="Rebuild"/>)
/// and only repositioned during continuous operations (<see cref="Layout"/>).
/// Divider drag + marquee select go through Layout, not Rebuild, to keep
/// the interaction cheap.
/// </summary>
public partial class ZoneEditorWindow : Window
{
    private const double DividerThickness = 6;
    private const double CellMargin       = 4;
    private const double MinFraction      = 0.02;
    private const double DragThresholdPx  = 6;
    private const double SnapPx           = 20;
    /// <summary>Don't allow a new cut within this many px of the hovered zone's
    /// own edge (an existing divider) — stops stacking near-duplicate lines on
    /// the same divider.</summary>
    private const double SplitInhibitPx   = 12;

    private readonly NativeMethods.RECT _workArea;
    private readonly LayoutProfile? _profile;
    private LayoutTree _tree;
    private readonly HashSet<int> _selectedIds = new();

    // --- visuals ---------------------------------------------------------
    private sealed class LeafVis
    {
        public required Rectangle  Shape;
        public required StackPanel Label;
        public required TextBlock  IndexText;
        public required TextBlock  SizeText;
    }
    private readonly Dictionary<int, LeafVis> _leafVis = new();

    private sealed class DividerVis
    {
        public required Rectangle Rect;
        public required SplitNode Parent;
        public int ChildIdx;
        public Rect ParentBounds;  // fractional 0..1, updated every Layout()
    }
    private readonly List<DividerVis> _dividers = new();

    private Line?      _previewLine;
    private Rectangle? _marquee;

    // --- hover / drag state ---------------------------------------------
    private Point _cursorPos;
    private int?  _hoverLeafId;

    private Point _lmbDownPos;
    private bool  _lmbDown;
    private bool  _isMarqueeSelecting;

    // When true, a click-cut spans the WHOLE layout (splits every zone the line
    // crosses) instead of only the hovered zone. Toggled by the "切割: 局部/全局"
    // button. Dragging a divider is unaffected (still tree-local).
    private bool  _globalSplit;

    // ---------------------------------------------------------------------

    internal ZoneEditorWindow(NativeMethods.RECT workArea, LayoutProfile? profile = null)
    {
        InitializeComponent();
        _workArea = workArea;
        _profile  = profile;

        // AllowsTransparency=True makes this a layered window, which pins the
        // editor's DPI context to the primary monitor. We therefore set WPF's
        // Width/Height/Left/Top in the primary-DPI DIP space (treating the
        // physical workArea values as DIPs — correct when primary is at 100%,
        // which is the common setup) AND call SetWindowPos in
        // OnSourceInitialized to realign the HWND physical bounds on monitors
        // with a non-primary DPI scale.
        Left   = workArea.Left;
        Top    = workArea.Top;
        Width  = workArea.Width;
        Height = workArea.Height;

        // Work on a clone so Esc/cancel leaves the stored profile untouched.
        _tree = profile is not null
            ? profile.ToTreeClone()
            : LayoutTree.UniformGrid(
                Math.Max(1, AppConfig.Current.ZonesRows),
                Math.Max(1, AppConfig.Current.ZonesColumns));

        Loaded += (_, _) => { Rebuild(); Root.Focus(); };
        KeyDown += OnKeyDown;
        KeyUp   += OnKeyUp;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Pin HWND to target monitor's physical-pixel bounds as a belt-and-
        // suspenders measure for non-primary high-DPI displays.
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            _workArea.Left, _workArea.Top, _workArea.Width, _workArea.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ===================================================== rebuild / layout

    private void ZoneCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    /// <summary>Rebuild all WPF visuals to match the current tree structure.</summary>
    private void Rebuild()
    {
        ZoneCanvas.Children.Clear();
        _leafVis.Clear();
        _dividers.Clear();

        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry => CreateLeafVisual(entry.Leaf));
        _tree.EnumerateSplits(new Rect(0, 0, 1, 1), entry =>
        {
            var s = entry.Split;
            for (int i = 0; i < s.Children.Count - 1; i++)
                CreateDividerVisual(s, i);
        });

        _previewLine = new Line
        {
            Stroke          = PreviewBrush,
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            IsHitTestVisible = false,
            Visibility      = Visibility.Collapsed,
        };
        Panel.SetZIndex(_previewLine, 100);
        ZoneCanvas.Children.Add(_previewLine);

        _marquee = new Rectangle
        {
            Stroke = MarqueeStroke, StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = MarqueeFill, IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_marquee, 99);
        ZoneCanvas.Children.Add(_marquee);

        Layout();
    }

    private void CreateLeafVisual(ZoneLeaf leaf)
    {
        var shape = new Rectangle
        {
            Fill = CellFill, Stroke = CellStroke,
            StrokeThickness = 1.2, RadiusX = 6, RadiusY = 6,
        };
        ZoneCanvas.Children.Add(shape);

        var idx = new TextBlock
        {
            Foreground    = LabelPrimary,
            FontSize      = 44,
            FontWeight    = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
        };
        var size = new TextBlock
        {
            Foreground    = LabelSecondary,
            FontSize      = 13,
            TextAlignment = TextAlignment.Center,
        };
        var panel = new StackPanel { IsHitTestVisible = false };
        panel.Children.Add(idx);
        panel.Children.Add(size);
        ZoneCanvas.Children.Add(panel);

        _leafVis[leaf.Id] = new LeafVis { Shape = shape, Label = panel, IndexText = idx, SizeText = size };
    }

    private void CreateDividerVisual(SplitNode parent, int childIdx)
    {
        var rect = new Rectangle
        {
            Fill   = DividerBrush,
            Cursor = parent.Orientation == SplitOrientation.Vertical ? Cursors.SizeWE : Cursors.SizeNS,
        };
        Panel.SetZIndex(rect, 10);
        ZoneCanvas.Children.Add(rect);

        var dv = new DividerVis { Rect = rect, Parent = parent, ChildIdx = childIdx };
        _dividers.Add(dv);
        AttachDividerDrag(dv);
    }

    /// <summary>A divider dragged in lockstep with the grabbed one because it
    /// lies on the same grid line. Captured at drag-start.</summary>
    private readonly struct LinkedDivider
    {
        public required DividerVis Div         { get; init; }
        public required double FracA           { get; init; } // parent.Fractions[ChildIdx] at drag start
        public required double FracB           { get; init; } // parent.Fractions[ChildIdx+1] at drag start
        public required double StartLinePx     { get; init; } // absolute divider line position at drag start
        public required double ExtentPx        { get; init; } // parent's extent along the drag axis (px)
    }

    private void AttachDividerDrag(DividerVis dv)
    {
        double startPx        = 0;
        double parentExtentPx = 0;
        // Snapped at drag-start: absolute canvas pixel positions of edges from
        // leaves NOT affected by this drag. Edges inside the children being
        // resized move with the drag, so excluding them prevents self-snap.
        var snapTargets = new List<double>();

        double startDividerPx = 0; // absolute pixel position of divider center at drag start
        double clickOffsetPx  = 0; // cursor click position minus divider center
        // Every same-orientation divider collinear with the grabbed one — moved
        // together so a full grid line drags as a unit. This is what makes row
        // ("横") dividers behave as globally as column ("竖") dividers even
        // though the tree nests one axis under the other. Always includes dv.
        var linked = new List<LinkedDivider>();

        dv.Rect.MouseLeftButtonDown += (_, e) =>
        {
            bool vertical = dv.Parent.Orientation == SplitOrientation.Vertical;
            double canvasExtent = vertical ? ZoneCanvas.ActualWidth : ZoneCanvas.ActualHeight;

            startDividerPx = DividerLinePx(dv, vertical);
            startPx        = vertical ? e.GetPosition(ZoneCanvas).X : e.GetPosition(ZoneCanvas).Y;
            parentExtentPx = (vertical ? dv.ParentBounds.Width : dv.ParentBounds.Height) * canvasExtent;
            clickOffsetPx  = startPx - startDividerPx;

            // Collect all same-orientation dividers sitting on this same line
            // (within ~1px). For a uniform grid these are the per-column row
            // dividers, so dragging one row boundary moves the whole row.
            linked.Clear();
            foreach (var d2 in _dividers)
            {
                if (d2.Parent.Orientation != dv.Parent.Orientation) continue;
                double linePx = DividerLinePx(d2, vertical);
                if (Math.Abs(linePx - startDividerPx) > 1.5) continue;
                double extentPx = (vertical ? d2.ParentBounds.Width : d2.ParentBounds.Height) * canvasExtent;
                linked.Add(new LinkedDivider
                {
                    Div         = d2,
                    FracA       = d2.Parent.Fractions[d2.ChildIdx],
                    FracB       = d2.Parent.Fractions[d2.ChildIdx + 1],
                    StartLinePx = linePx,
                    ExtentPx    = extentPx,
                });
            }

            snapTargets.Clear();
            var movingIds = new HashSet<int>();
            foreach (var l in linked)
            {
                CollectLeafIds(l.Div.Parent.Children[l.Div.ChildIdx],     movingIds);
                CollectLeafIds(l.Div.Parent.Children[l.Div.ChildIdx + 1], movingIds);
            }
            _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
            {
                if (movingIds.Contains(entry.Leaf.Id)) return;
                var b = entry.Bounds;
                double a = vertical ? b.X        * canvasExtent : b.Y        * canvasExtent;
                double c = vertical ? (b.X + b.Width) * canvasExtent
                                    : (b.Y + b.Height) * canvasExtent;
                foreach (var ep in new[] { a, c })
                {
                    if (ep < 2 || ep > canvasExtent - 2) continue;
                    snapTargets.Add(ep);
                }
            });

            dv.Rect.CaptureMouse();
            e.Handled = true;
        };
        dv.Rect.MouseMove += (_, e) =>
        {
            if (!dv.Rect.IsMouseCaptured) return;
            if (parentExtentPx < 1) return;

            bool vertical = dv.Parent.Orientation == SplitOrientation.Vertical;
            double cur = vertical
                ? e.GetPosition(ZoneCanvas).X
                : e.GetPosition(ZoneCanvas).Y;

            // Divider follows cursor, keeping the click offset constant.
            // Snap compares the DIVIDER'S new position (not the cursor's)
            // to static snap targets — previously we snapped the cursor,
            // so the divider would land clickOffsetPx away from the target.
            double origDividerPx = cur - clickOffsetPx;
            double newDividerPx  = origDividerPx;
            double bestDist      = SnapPx;
            foreach (var t in snapTargets)
            {
                double d = Math.Abs(origDividerPx - t);
                if (d < bestDist) { bestDist = d; newDividerPx = t; }
            }

            // Drive every linked divider to the same absolute line position, each
            // converting to its own parent's fractional delta. Bail as a UNIT if
            // any would collapse below the minimum, so the line never desyncs.
            // new line px = StartLinePx + delta*ExtentPx = newDividerPx exactly,
            // so all linked dividers stay perfectly collinear regardless of extent.
            var pending = new List<(DividerVis div, double na, double nb)>(linked.Count);
            foreach (var l in linked)
            {
                if (l.ExtentPx < 1) continue;
                double delta = (newDividerPx - l.StartLinePx) / l.ExtentPx;
                double na = l.FracA + delta;
                double nb = l.FracB - delta;
                if (na < MinFraction || nb < MinFraction) return;
                pending.Add((l.Div, na, nb));
            }
            foreach (var (div, na, nb) in pending)
            {
                div.Parent.Fractions[div.ChildIdx]     = na;
                div.Parent.Fractions[div.ChildIdx + 1] = nb;
            }
            Layout();
        };
        dv.Rect.MouseLeftButtonUp += (_, _) => dv.Rect.ReleaseMouseCapture();
    }

    /// <summary>Absolute canvas-pixel position of a divider's line along its
    /// perpendicular axis, from its parent's current bounds + fractions.</summary>
    private double DividerLinePx(DividerVis d, bool vertical)
    {
        double cum = 0;
        for (int k = 0; k <= d.ChildIdx; k++) cum += d.Parent.Fractions[k];
        return vertical
            ? (d.ParentBounds.X + cum * d.ParentBounds.Width)  * ZoneCanvas.ActualWidth
            : (d.ParentBounds.Y + cum * d.ParentBounds.Height) * ZoneCanvas.ActualHeight;
    }

    private static void CollectLeafIds(LayoutNode node, HashSet<int> ids)
    {
        if (node is ZoneLeaf leaf) ids.Add(leaf.Id);
        else if (node is SplitNode split)
            foreach (var c in split.Children) CollectLeafIds(c, ids);
    }

    /// <summary>Reposition all existing visuals; no structural changes.</summary>
    private void Layout()
    {
        if (ZoneCanvas.ActualWidth <= 0 || ZoneCanvas.ActualHeight <= 0) return;

        double W = ZoneCanvas.ActualWidth;
        double H = ZoneCanvas.ActualHeight;
        int waW = _workArea.Width, waH = _workArea.Height;

        // ---- leaves ----
        // Assign display numbers column-major (by X then Y) regardless of
        // the tree's internal enumeration order — matches the overlay.
        var orderedEntries = new List<LayoutTree.LeafEntry>();
        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), e => orderedEntries.Add(e));
        orderedEntries.Sort((a, b) =>
        {
            int c = a.Bounds.X.CompareTo(b.Bounds.X);
            return c != 0 ? c : a.Bounds.Y.CompareTo(b.Bounds.Y);
        });
        var indexByLeaf = new Dictionary<int, int>();
        for (int i = 0; i < orderedEntries.Count; i++)
            indexByLeaf[orderedEntries[i].Leaf.Id] = i + 1;

        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            if (!_leafVis.TryGetValue(entry.Leaf.Id, out var vis)) return;
            var b = entry.Bounds;
            double px = b.X * W, py = b.Y * H;
            double pw = b.Width * W, ph = b.Height * H;
            double cw = Math.Max(0, pw - CellMargin * 2);
            double ch = Math.Max(0, ph - CellMargin * 2);

            vis.Shape.Width  = cw;
            vis.Shape.Height = ch;
            Canvas.SetLeft(vis.Shape, px + CellMargin);
            Canvas.SetTop (vis.Shape, py + CellMargin);

            bool selected = _selectedIds.Contains(entry.Leaf.Id);
            vis.Shape.Stroke = selected ? SelectedStroke : CellStroke;
            vis.Shape.Fill   = selected ? SelectedFill   : CellFill;
            vis.Shape.StrokeThickness = selected ? 3 : 1.2;

            int pxW = (int)Math.Round(b.Width  * waW);
            int pxH = (int)Math.Round(b.Height * waH);
            // Each digit on its own line via Inlines — vertical stack.
            string numStr = indexByLeaf[entry.Leaf.Id].ToString();
            vis.IndexText.Inlines.Clear();
            for (int i = 0; i < numStr.Length; i++)
            {
                if (i > 0) vis.IndexText.Inlines.Add(new System.Windows.Documents.LineBreak());
                vis.IndexText.Inlines.Add(new System.Windows.Documents.Run(numStr[i].ToString()));
            }
            vis.SizeText .Text = $"{pxW} × {pxH}";

            double fontScale = Math.Clamp(Math.Min(cw, ch) / 200.0, 0.5, 1.8);
            vis.IndexText.FontSize = 44 * fontScale;
            vis.IndexText.LineHeight = 48 * fontScale;
            vis.IndexText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            vis.SizeText .FontSize = 13 * fontScale;
            vis.IndexText.Visibility = (cw > 60 && ch > 60) ? Visibility.Visible : Visibility.Collapsed;
            vis.SizeText .Visibility = (cw > 90 && ch > 90) ? Visibility.Visible : Visibility.Collapsed;

            vis.Label.Measure(new Size(cw, ch));
            double lw = vis.Label.DesiredSize.Width;
            double lh = vis.Label.DesiredSize.Height;
            Canvas.SetLeft(vis.Label, px + (pw - lw) / 2);
            Canvas.SetTop (vis.Label, py + (ph - lh) / 2);
        });

        // ---- dividers ----
        // Cache split bounds by reference so the per-divider update is O(1).
        var splitBounds = new Dictionary<SplitNode, Rect>();
        _tree.EnumerateSplits(new Rect(0, 0, 1, 1), e => splitBounds[e.Split] = e.Bounds);

        foreach (var dv in _dividers)
        {
            if (!splitBounds.TryGetValue(dv.Parent, out var pb)) continue;
            dv.ParentBounds = pb;

            double ppx = pb.X * W, ppy = pb.Y * H;
            double ppw = pb.Width * W, pph = pb.Height * H;

            double cum = 0;
            for (int k = 0; k <= dv.ChildIdx; k++) cum += dv.Parent.Fractions[k];

            if (dv.Parent.Orientation == SplitOrientation.Vertical)
            {
                dv.Rect.Width  = DividerThickness;
                dv.Rect.Height = pph;
                Canvas.SetLeft(dv.Rect, ppx + cum * ppw - DividerThickness / 2);
                Canvas.SetTop (dv.Rect, ppy);
            }
            else
            {
                dv.Rect.Width  = ppw;
                dv.Rect.Height = DividerThickness;
                Canvas.SetLeft(dv.Rect, ppx);
                Canvas.SetTop (dv.Rect, ppy + cum * pph - DividerThickness / 2);
            }
        }

        UpdatePreview();
    }

    // ========================================================== mouse

    private void ZoneCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        _cursorPos = e.GetPosition(ZoneCanvas);

        if (_lmbDown)
        {
            if (!_isMarqueeSelecting)
            {
                double dx = _cursorPos.X - _lmbDownPos.X;
                double dy = _cursorPos.Y - _lmbDownPos.Y;
                if (dx * dx + dy * dy > DragThresholdPx * DragThresholdPx)
                    _isMarqueeSelecting = true;
            }
            if (_isMarqueeSelecting) RecomputeMarqueeSelection();
        }

        _hoverLeafId = FindLeafAtPixel(_cursorPos);
        UpdatePreview();
    }

    private void ZoneCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverLeafId = null;
        UpdatePreview();
    }

    private void ZoneCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lmbDownPos = e.GetPosition(ZoneCanvas);
        _lmbDown = true;
        _isMarqueeSelecting = false;
        ZoneCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void ZoneCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_lmbDown) return;
        ZoneCanvas.ReleaseMouseCapture();
        _lmbDown = false;

        if (_isMarqueeSelecting)
        {
            _isMarqueeSelecting = false;
            if (_marquee is not null) _marquee.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        // No drag → click → split.
        var pt = e.GetPosition(ZoneCanvas);
        double W = ZoneCanvas.ActualWidth, H = ZoneCanvas.ActualHeight;
        if (W <= 0 || H <= 0) { e.Handled = true; return; }

        var hit = _tree.HitTest(new Rect(0, 0, 1, 1), pt.X / W, pt.Y / H);
        if (hit is null) { e.Handled = true; return; }

        var h = hit.Value;
        var dir = IsShiftDown() ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        // Snap returns the target edge as a FRACTION (not a re-scaled pixel)
        // so the new cut's fractional position matches the neighbour's exactly.
        double snapAbsFrac = dir == SplitOrientation.Vertical
            ? SnapVerticalFrac  (pt.X, h.Bounds, h.Leaf.Id, W)
            : SnapHorizontalFrac(pt.Y, h.Bounds, h.Leaf.Id, H);

        // Suppress cuts sitting on the hovered zone's own divider.
        if (CutSuppressed(snapAbsFrac, dir, h.Bounds, dir == SplitOrientation.Vertical ? W : H))
        { e.Handled = true; return; }

        if (GlobalActive)
        {
            SplitGlobal(snapAbsFrac, dir);
            // Hoist the just-made cut (and any existing aligned cuts it lined up
            // with) into a single full-span divider that drags as one. Prefer
            // the cut's own direction so a horizontal cut becomes the global
            // horizontal line (not demoted under a vertical split).
            _tree.Canonicalize(dir);
        }
        else
        {
            double fracInLeaf = dir == SplitOrientation.Vertical
                ? (snapAbsFrac - h.Bounds.X) / Math.Max(1e-9, h.Bounds.Width)
                : (snapAbsFrac - h.Bounds.Y) / Math.Max(1e-9, h.Bounds.Height);
            _tree.SplitLeaf(h.Leaf.Id, dir, fracInLeaf);
        }
        _selectedIds.Clear();
        Rebuild();
        e.Handled = true;
    }

    /// <summary>Cut every leaf the line at <paramref name="absFrac"/> (a [0,1]
    /// position along the perpendicular axis) passes through, so the split runs
    /// edge-to-edge across the whole layout — a "global" cut. Each crossed leaf
    /// is split at its own local fraction; the cuts line up at the same absolute
    /// position and read as one continuous divider.</summary>
    private void SplitGlobal(double absFrac, SplitOrientation dir)
    {
        var targets = new List<(int id, double frac)>();
        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            var b = entry.Bounds;
            if (dir == SplitOrientation.Vertical)
            {
                if (absFrac > b.X + 1e-6 && absFrac < b.X + b.Width - 1e-6)
                    targets.Add((entry.Leaf.Id, (absFrac - b.X) / b.Width));
            }
            else
            {
                if (absFrac > b.Y + 1e-6 && absFrac < b.Y + b.Height - 1e-6)
                    targets.Add((entry.Leaf.Id, (absFrac - b.Y) / b.Height));
            }
        });
        // Splitting one leaf never changes another leaf's bounds, so the
        // fractions collected above stay correct as we apply them.
        foreach (var (id, frac) in targets)
            _tree.SplitLeaf(id, dir, frac);
    }

    private void ZoneCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ExecuteMerge();
        e.Handled = true;
    }

    private int? FindLeafAtPixel(Point p)
    {
        if (ZoneCanvas.ActualWidth <= 0 || ZoneCanvas.ActualHeight <= 0) return null;
        double fx = p.X / ZoneCanvas.ActualWidth;
        double fy = p.Y / ZoneCanvas.ActualHeight;
        if (fx < 0 || fx >= 1 || fy < 0 || fy >= 1) return null;
        return _tree.HitTest(new Rect(0, 0, 1, 1), fx, fy)?.Leaf.Id;
    }

    private void RecomputeMarqueeSelection()
    {
        double x0 = Math.Min(_lmbDownPos.X, _cursorPos.X);
        double y0 = Math.Min(_lmbDownPos.Y, _cursorPos.Y);
        double x1 = Math.Max(_lmbDownPos.X, _cursorPos.X);
        double y1 = Math.Max(_lmbDownPos.Y, _cursorPos.Y);

        if (_marquee is not null)
        {
            Canvas.SetLeft(_marquee, x0);
            Canvas.SetTop (_marquee, y0);
            _marquee.Width  = Math.Max(0, x1 - x0);
            _marquee.Height = Math.Max(0, y1 - y0);
            _marquee.Visibility = Visibility.Visible;
        }

        double W = ZoneCanvas.ActualWidth, H = ZoneCanvas.ActualHeight;
        if (W <= 0 || H <= 0) return;
        double fx0 = x0 / W, fy0 = y0 / H, fx1 = x1 / W, fy1 = y1 / H;

        var next = new HashSet<int>();
        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            var b = entry.Bounds;
            bool overlaps = !(b.X + b.Width < fx0 || b.X > fx1 || b.Y + b.Height < fy0 || b.Y > fy1);
            if (overlaps) next.Add(entry.Leaf.Id);
        });

        if (!next.SetEquals(_selectedIds))
        {
            _selectedIds.Clear();
            foreach (var id in next) _selectedIds.Add(id);
            Layout();
        }
    }

    private void UpdatePreview()
    {
        if (_previewLine is null) return;
        if (_hoverLeafId is null || Mouse.Captured is not null)
        {
            _previewLine.Visibility = Visibility.Collapsed;
            return;
        }

        Rect? leafBounds = null;
        int   target     = _hoverLeafId.Value;
        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            if (entry.Leaf.Id == target) leafBounds = entry.Bounds;
        });
        if (leafBounds is null) { _previewLine.Visibility = Visibility.Collapsed; return; }

        double W = ZoneCanvas.ActualWidth, H = ZoneCanvas.ActualHeight;
        var b = leafBounds.Value;
        double px = b.X * W, py = b.Y * H, pw = b.Width * W, ph = b.Height * H;

        // Global cut spans the whole canvas (edge to edge); local stays inside
        // the hovered zone. Either way, hide the preview when the cut would sit
        // on the zone's own divider (so you can't stack near-duplicate lines).
        bool global = GlobalActive;

        if (IsShiftDown())
        {
            double y = Math.Clamp(_cursorPos.Y, global ? 2 : py + 2, global ? H - 2 : py + ph - 2);
            y = SnapHorizontal(y, b, target, H);
            if (CutSuppressed(y / H, SplitOrientation.Horizontal, b, H))
            { _previewLine.Visibility = Visibility.Collapsed; return; }
            _previewLine.X1 = global ? 6 : px + 6;
            _previewLine.X2 = global ? W - 6 : px + pw - 6;
            _previewLine.Y1 = _previewLine.Y2 = y;
        }
        else
        {
            double x = Math.Clamp(_cursorPos.X, global ? 2 : px + 2, global ? W - 2 : px + pw - 2);
            x = SnapVertical(x, b, target, W);
            if (CutSuppressed(x / W, SplitOrientation.Vertical, b, W))
            { _previewLine.Visibility = Visibility.Collapsed; return; }
            _previewLine.Y1 = global ? 6 : py + 6;
            _previewLine.Y2 = global ? H - 6 : py + ph - 6;
            _previewLine.X1 = _previewLine.X2 = x;
        }
        _previewLine.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Snap a vertical cut's X to any other leaf's vertical edge inside the
    /// hover leaf's horizontal span. We deliberately ignore whether those
    /// edges vertically overlap the hover leaf — the whole point of snap is
    /// to let the user align a new cut with a divider above/below.
    /// </summary>
    private double SnapVertical(double cursorXpx, Rect hoverBounds, int hoverId, double W)
        => SnapVerticalFrac(cursorXpx, hoverBounds, hoverId, W) * W;

    /// <summary>Variant that returns the target as a [0,1] fraction — avoids
    /// a px→frac round trip when the caller needs a fractional cut position.</summary>
    private double SnapVerticalFrac(double cursorXpx, Rect hoverBounds, int hoverId, double W)
    {
        double bestFrac = cursorXpx / W;
        double bestDist = SnapPx;
        double hoverLeftPx  = hoverBounds.X * W;
        double hoverRightPx = (hoverBounds.X + hoverBounds.Width) * W;

        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            if (entry.Leaf.Id == hoverId) return;
            var b = entry.Bounds;
            foreach (double edgeFrac in new[] { b.X, b.X + b.Width })
            {
                double edgePx = edgeFrac * W;
                if (edgePx <= hoverLeftPx + 1 || edgePx >= hoverRightPx - 1) continue;
                double d = Math.Abs(edgePx - cursorXpx);
                if (d < bestDist) { bestDist = d; bestFrac = edgeFrac; }
            }
        });
        return bestFrac;
    }

    private double SnapHorizontal(double cursorYpx, Rect hoverBounds, int hoverId, double H)
        => SnapHorizontalFrac(cursorYpx, hoverBounds, hoverId, H) * H;

    private double SnapHorizontalFrac(double cursorYpx, Rect hoverBounds, int hoverId, double H)
    {
        double bestFrac = cursorYpx / H;
        double bestDist = SnapPx;
        double hoverTopPx = hoverBounds.Y * H;
        double hoverBotPx = (hoverBounds.Y + hoverBounds.Height) * H;

        _tree.EnumerateLeaves(new Rect(0, 0, 1, 1), entry =>
        {
            if (entry.Leaf.Id == hoverId) return;
            var b = entry.Bounds;
            foreach (double edgeFrac in new[] { b.Y, b.Y + b.Height })
            {
                double edgePx = edgeFrac * H;
                if (edgePx <= hoverTopPx + 1 || edgePx >= hoverBotPx - 1) continue;
                double d = Math.Abs(edgePx - cursorYpx);
                if (d < bestDist) { bestDist = d; bestFrac = edgeFrac; }
            }
        });
        return bestFrac;
    }

    // ========================================================== commands

    private void BtnMerge_Click (object s, RoutedEventArgs e) => ExecuteMerge();
    private void BtnDelete_Click(object s, RoutedEventArgs e) => ExecuteDelete();
    private void BtnEven_Click  (object s, RoutedEventArgs e) => ExecuteEvenDistribute();
    private void BtnSave_Click  (object s, RoutedEventArgs e) => SaveAndClose();
    private void BtnCancel_Click(object s, RoutedEventArgs e) => Close();

    private void BtnResetGrid_Click(object sender, RoutedEventArgs e)
    {
        _tree = LayoutTree.UniformGrid(2, 2);
        _selectedIds.Clear();
        Rebuild();
    }

    private void BtnSplitScope_Click(object sender, RoutedEventArgs e)
    {
        _globalSplit = !_globalSplit;
        BtnSplitScope.Content    = _globalSplit ? "切割: 全局" : "切割: 局部";
        BtnSplitScope.Appearance = _globalSplit
            ? Wpf.Ui.Controls.ControlAppearance.Primary
            : Wpf.Ui.Controls.ControlAppearance.Secondary;
        Root.Focus();
        UpdatePreview();
    }

    private void ExecuteMerge()
    {
        if (_selectedIds.Count < 2) return;
        int merged = _tree.Merge(_selectedIds.ToList());
        if (merged < 0) return;
        _selectedIds.Clear();
        Rebuild();
    }

    private void ExecuteDelete()
    {
        if (_selectedIds.Count == 0) return;
        _tree.Delete(_selectedIds.ToList());
        _selectedIds.Clear();
        Rebuild();
    }

    private void ExecuteEvenDistribute()
    {
        if (!_tree.EvenDistribute(_selectedIds.ToList())) return;
        _selectedIds.Clear();
        Layout();
    }

    // ========================================================== keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:  SaveAndClose(); e.Handled = true; break;
            case Key.Escape:
                if (_selectedIds.Count > 0) { _selectedIds.Clear(); Layout(); }
                else                        Close();
                e.Handled = true; break;
            case Key.Delete: ExecuteDelete(); e.Handled = true; break;
            case Key.LeftShift:
            case Key.RightShift:
            case Key.LeftCtrl:
            case Key.RightCtrl: UpdatePreview(); break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) UpdatePreview();
    }

    private static bool IsShiftDown() => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
    private static bool IsCtrlDown()  => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    /// <summary>Effective cut scope this moment: the toggle button's state,
    /// inverted while Ctrl is held (hold-to-switch, like Shift for direction).</summary>
    private bool GlobalActive => _globalSplit ^ IsCtrlDown();

    /// <summary>True if a cut at <paramref name="absFrac"/> would land within
    /// <see cref="SplitInhibitPx"/> of the hovered zone's own boundary in the
    /// cut direction — i.e. the cursor is sitting on/next to an existing
    /// divider. Suppresses the cut so lines can't be stacked on the same edge.</summary>
    private static bool CutSuppressed(double absFrac, SplitOrientation dir, Rect hoverBounds, double extentPx)
    {
        double cutPx = absFrac * extentPx;
        double e0, e1;
        if (dir == SplitOrientation.Vertical)
        {
            e0 = hoverBounds.X * extentPx;
            e1 = (hoverBounds.X + hoverBounds.Width) * extentPx;
        }
        else
        {
            e0 = hoverBounds.Y * extentPx;
            e1 = (hoverBounds.Y + hoverBounds.Height) * extentPx;
        }
        return Math.Abs(cutPx - e0) < SplitInhibitPx || Math.Abs(cutPx - e1) < SplitInhibitPx;
    }

    // ========================================================== save

    private void SaveAndClose()
    {
        // Only mutate the caller's profile on explicit save. Persistence and
        // Layouts-list membership are the caller's responsibility — signalled
        // via DialogResult=true.
        _profile?.WriteFrom(_tree);
        DialogResult = true;
        Close();
    }

    // ========================================================== brushes

    private static readonly Brush CellFill       = new SolidColorBrush(Color.FromArgb(0x40, 0xB0, 0xB0, 0xB0));
    private static readonly Brush CellStroke     = new SolidColorBrush(Color.FromArgb(0xB0, 0xD0, 0xD0, 0xD0));
    private static readonly Brush SelectedFill   = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xC1, 0x07));
    private static readonly Brush SelectedStroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
    private static readonly Brush DividerBrush   = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PreviewBrush   = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MarqueeStroke  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
    private static readonly Brush MarqueeFill    = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xC1, 0x07));
    private static readonly Brush LabelPrimary   = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LabelSecondary = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
}
