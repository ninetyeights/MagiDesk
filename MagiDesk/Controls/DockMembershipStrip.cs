using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagiDesk.Features.ProfileDock;

namespace MagiDesk.Controls;

public sealed class DockMembershipStrip : UserControl
{
    public static readonly DependencyProperty MembershipsProperty = DependencyProperty.Register(nameof(Memberships), typeof(IEnumerable), typeof(DockMembershipStrip),
        new PropertyMetadata(null, (d, _) => ((DockMembershipStrip)d).Rebuild()));
    public IEnumerable? Memberships { get => (IEnumerable?)GetValue(MembershipsProperty); set => SetValue(MembershipsProperty, value); }
    public static readonly RoutedEvent RemoveRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(RemoveRequested), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(DockMembershipStrip));
    public static readonly RoutedEvent ManageRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ManageRequested), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(DockMembershipStrip));
    public event RoutedEventHandler RemoveRequested
    {
        add => AddHandler(RemoveRequestedEvent, value);
        remove => RemoveHandler(RemoveRequestedEvent, value);
    }
    public event RoutedEventHandler ManageRequested
    {
        add => AddHandler(ManageRequestedEvent, value);
        remove => RemoveHandler(ManageRequestedEvent, value);
    }
    internal DockProjectTarget? RequestedTarget { get; private set; }
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly List<Border> _tags = new();
    private readonly List<TextBlock> _separators = new();
    private readonly Button _more = new() { Padding = new Thickness(5, 2, 5, 2), Height = 28 };
    private bool _arranging;

    public DockMembershipStrip()
    {
        Height = 32;
        ClipToBounds = true;
        Content = _row;
        _more.SetResourceReference(StyleProperty, "InlineAction");
        _more.Click += (_, e) => { RaiseEvent(new RoutedEventArgs(ManageRequestedEvent, this)); e.Handled = true; };
        SizeChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
    }

    private void Rebuild()
    {
        _row.Children.Clear(); _tags.Clear(); _separators.Clear();
        RequestedTarget = null;
        foreach (var target in Memberships?.Cast<DockProjectTarget>() ?? Enumerable.Empty<DockProjectTarget>())
        {
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock
            {
                Text = ((char)0xE8B7).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                IsHitTestVisible = false
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var label = new TextBlock { Text = target.Collection.Name, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var remove = new Button { Content = "×", Width = 20, Height = 24, Padding = new Thickness(2, 0, 2, 0), Opacity = 0, ToolTip = "移除此归属" };
            remove.SetResourceReference(StyleProperty, "InlineAction");
            body.Children.Add(icon); body.Children.Add(label); body.Children.Add(remove);
            var tag = new Border { Child = body, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 2, 2), Margin = new Thickness(0, 2, 0, 2), ToolTip = target.Label, Background = Brushes.Transparent };
            void UpdateRemove()
            {
                bool active = tag.IsMouseOver || tag.IsKeyboardFocusWithin;
                remove.Opacity = active ? 1 : 0;
                remove.IsHitTestVisible = active;
                if (active) tag.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                else tag.Background = Brushes.Transparent;
            }
            remove.IsHitTestVisible = false;
            tag.MouseEnter += (_, _) => UpdateRemove(); tag.MouseLeave += (_, _) => UpdateRemove();
            tag.IsKeyboardFocusWithinChanged += (_, _) => UpdateRemove();
            remove.Click += (_, e) => { RequestedTarget = target; RaiseEvent(new RoutedEventArgs(RemoveRequestedEvent, this)); e.Handled = true; };
            var separator = new TextBlock { Text = "·", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), Opacity = 0.6, IsHitTestVisible = false };
            separator.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _separators.Add(separator); _row.Children.Add(separator);
            _tags.Add(tag); _row.Children.Add(tag);
        }
        if (_tags.Count == 0)
        {
            var empty = new TextBlock { Text = "未加入集合", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _row.Children.Add(empty);
        }
        _row.Children.Add(_more);
        Fit();
    }

    private void Fit()
    {
        if (_arranging) return;
        _arranging = true;
        try
        {
            var widths = new List<double>();
            for (int i = 0; i < _tags.Count; i++)
            {
                var tag = _tags[i];
                tag.Visibility = Visibility.Visible;
                tag.Measure(new Size(double.PositiveInfinity, 32));
                _separators[i].Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
                _separators[i].Measure(new Size(double.PositiveInfinity, 32));
                widths.Add(tag.DesiredSize.Width + _separators[i].DesiredSize.Width);
            }
            int visible = _tags.Count;
            double width = widths.Sum();
            _more.Visibility = Visibility.Visible;
            while (visible >= 0)
            {
                int hidden = _tags.Count - visible;
                _more.Content = $"还有 {hidden} 个集合";
                _more.Measure(new Size(double.PositiveInfinity, 32));
                if (width + (hidden == 0 ? 0 : _more.DesiredSize.Width) <= ActualWidth || visible == 0) break;
                width -= widths[--visible];
            }
            for (int i = 0; i < _tags.Count; i++)
            {
                _tags[i].Visibility = i < visible ? Visibility.Visible : Visibility.Collapsed;
                _separators[i].Visibility = i > 0 && i < visible ? Visibility.Visible : Visibility.Collapsed;
            }
            _more.Visibility = visible == _tags.Count ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _arranging = false; }
    }
}
