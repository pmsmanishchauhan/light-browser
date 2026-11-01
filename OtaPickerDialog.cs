using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LightBrowser;

// Pick OTA extranet and channel-manager portals from the built-in list (OtaCatalog) to add as home pages.
public class OtaPickerDialog : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
    static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));

    readonly TextBox _filter = new();
    readonly List<(OtaPortal Portal, CheckBox Box, FrameworkElement Row)> _rows = new();
    readonly List<(string Kind, TextBlock Header)> _headers = new();
    readonly TextBlock _count = new();

    public OtaPickerDialog(IEnumerable<string> alreadyAdded, bool single = false)
    {
        Title = single ? "Choose an OTA portal" : "Add OTA portals";
        Width = 480; Height = 620; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        var present = new HashSet<string>(alreadyAdded.Select(u => SuggestionStore.Normalize(u) ?? u), StringComparer.OrdinalIgnoreCase);
        _filter.Background = Field; _filter.Foreground = Fg; _filter.BorderBrush = FieldBorder; _filter.CaretBrush = Fg;
        _filter.Padding = new Thickness(6, 4, 6, 4);
        _count.Foreground = Dim; _count.VerticalAlignment = VerticalAlignment.Center;

        var list = new StackPanel();
        foreach (var group in OtaCatalog.All.GroupBy(p => p.Kind))
        {
            var header = new TextBlock { Text = group.Key, Foreground = Dim, FontSize = 12, Margin = new Thickness(6, 12, 0, 4) };
            _headers.Add((group.Key, header));
            list.Children.Add(header);
            foreach (var portal in group)
            {
                var box = new CheckBox { Foreground = Fg, Margin = new Thickness(0, 3, 0, 3), IsEnabled = !present.Contains(SuggestionStore.Normalize(portal.Url) ?? portal.Url) };
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = portal.Name, Foreground = box.IsEnabled ? Fg : Dim });
                text.Children.Add(new TextBlock { Text = box.IsEnabled ? portal.Url : portal.Url + "  (already added)", Foreground = Dim, FontSize = 11 });
                box.Content = text;
                box.ToolTip = portal.Note ?? portal.Url;
                var row = new Border { Child = box, Padding = new Thickness(8, 0, 0, 0) };
                if (single) box.Checked += (_, _) => { foreach (var r in _rows) if (r.Box != box) r.Box.IsChecked = false; };
                box.Checked += (_, _) => UpdateCount();
                box.Unchecked += (_, _) => UpdateCount();
                _rows.Add((portal, box, row));
                list.Children.Add(row);
            }
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listBorder = new Border { Child = scroll, BorderBrush = FieldBorder, BorderThickness = new Thickness(1), Background = Field, Margin = new Thickness(0, 8, 0, 0) };

        _filter.TextChanged += (_, _) =>
        {
            var q = _filter.Text.Trim();
            foreach (var (portal, _, row) in _rows)
                row.Visibility = q == "" || portal.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 portal.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 portal.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (kind, header) in _headers)
                header.Visibility = _rows.Any(r => r.Portal.Kind == kind && r.Row.Visibility == Visibility.Visible)
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        var ok = new Button
        {
            Content = single ? "Use" : "Add selected", Width = 110, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg,
            Style = (Style)Application.Current.FindResource("Flat"), Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x8E, 0xF7)),
            FontWeight = FontWeights.SemiBold, IsDefault = true,
        };
        ok.Click += (_, _) => { if (Selected.Count > 0) DialogResult = true; };
        var cancel = new Button
        {
            Content = "Cancel", Width = 80, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg, IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Flat"), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
        };
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_count);

        var hint = new TextBlock
        {
            Text = "Logins are never stored here. Each portal opens its normal sign-in page in the profile you add it to.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
        };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Search portals", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        top.Children.Add(_filter);
        var bottom = new StackPanel();
        bottom.Children.Add(hint);
        bottom.Children.Add(buttons);
        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(listBorder);
        Content = root;

        UpdateCount();
        Loaded += (_, _) => _filter.Focus();
    }

    public List<OtaPortal> Selected => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Portal).ToList();

    void UpdateCount() => _count.Text = $"{Selected.Count} selected";
}
