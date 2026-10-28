using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightBrowser;

// Shows one profile's local history. Double-click or Enter opens a page; the caller reads OpenUrl.
public class HistoryDialog : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96));

    readonly HistoryStore _store;
    readonly string _profileId;
    readonly TextBox _search = new();
    readonly ListBox _list = new();
    readonly TextBlock _count = new();

    public HistoryDialog(string profileName, string profileId, HistoryStore store)
    {
        _store = store;
        _profileId = profileId;
        Title = $"History - {profileName}";
        Width = 640;
        Height = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        _search.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
        _search.Foreground = Fg;
        _search.CaretBrush = Fg;
        _search.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
        _search.Padding = new Thickness(8, 6, 8, 6);
        _search.TextChanged += (_, _) => Reload();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus(); }
        };

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Margin = new Thickness(0, 8, 0, 0);
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("Item");
        _list.ItemTemplate = BuildRowTemplate();
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelected(); };

        _count.Foreground = Dim;
        _count.VerticalAlignment = VerticalAlignment.Center;

        Button Make(string text, double width)
        {
            return new Button
            {
                Content = text, Width = width, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg,
                Style = (Style)Application.Current.FindResource("Flat"),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
            };
        }
        var clear = Make("Clear history...", 120);
        clear.Click += (_, _) =>
        {
            var ok = MessageBox.Show(this, $"Delete all history of \"{profileName}\"? Logins and passwords are not affected.",
                "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
            _store.Clear(_profileId);
            Reload();
        };
        var open = Make("Open", 80);
        open.IsDefault = false;
        open.Margin = new Thickness(0, 0, 8, 0);
        open.Click += (_, _) => OpenSelected();
        var close = Make("Close", 80);
        close.IsCancel = true;

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(open);
        right.Children.Add(close);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(clear, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(clear);
        bottom.Children.Add(right);
        bottom.Children.Add(_count);
        _count.Margin = new Thickness(12, 0, 0, 0);

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Search this profile's history", Foreground = Dim, Margin = new Thickness(0, 0, 0, 4) });
        top.Children.Add(_search);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Reload();
        Loaded += (_, _) => _search.Focus();
    }

    public string? OpenUrl { get; private set; }

    void Reload()
    {
        var rows = _store.Search(_profileId, _search.Text, 500);
        _list.ItemsSource = rows;
        _count.Text = rows.Count == 0
            ? (_search.Text.Trim() == "" ? "No history yet" : "No matches")
            : $"{rows.Count} shown (newest first)";
    }

    void OpenSelected()
    {
        if (_list.SelectedItem is not HistoryEntry entry) return;
        OpenUrl = entry.Url;
        DialogResult = true;
    }

    static DataTemplate BuildRowTemplate()
    {
        var template = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayTitle"));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(TextBlock.ForegroundProperty, Fg);
        var url = new FrameworkElementFactory(typeof(TextBlock));
        url.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Url"));
        url.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        url.SetValue(TextBlock.ForegroundProperty, Dim);
        url.SetValue(TextBlock.FontSizeProperty, 11.0);
        var when = new FrameworkElementFactory(typeof(TextBlock));
        when.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("When"));
        when.SetValue(TextBlock.ForegroundProperty, Dim);
        when.SetValue(TextBlock.FontSizeProperty, 10.5);
        stack.AppendChild(title);
        stack.AppendChild(url);
        stack.AppendChild(when);
        template.VisualTree = stack;
        return template;
    }
}
