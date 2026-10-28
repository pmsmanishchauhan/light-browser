using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LightBrowser;

public enum LaunchAction { Open, Close }

// Batch dialog: open one website in several profiles at once, or close several profiles.
public class LaunchDialog : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
    static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));

    readonly TextBox _url = new();
    readonly TextBox _filter = new();
    readonly TextBlock _count = new();
    readonly List<(Profile Profile, CheckBox Box)> _rows = new();

    public LaunchDialog(IEnumerable<Profile> profiles, IReadOnlyList<string> commonSites, Profile? preselect)
    {
        Title = "Launch site in profiles";
        Width = 460;
        Height = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        StyleField(_url);
        StyleField(_filter);
        _count.Foreground = Fg;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(8, 0, 0, 0);

        var list = new StackPanel();
        foreach (var p in profiles)
        {
            var box = new CheckBox
            {
                Content = p.Group.Trim() == "" ? p.Name : $"{p.Name}    ({p.Group})",
                Foreground = Fg, Margin = new Thickness(4, 3, 4, 3), IsChecked = p == preselect,
            };
            box.Checked += (_, _) => UpdateCount();
            box.Unchecked += (_, _) => UpdateCount();
            _rows.Add((p, box));
            list.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listBorder = new Border
        {
            Child = scroll, BorderBrush = FieldBorder, BorderThickness = new Thickness(1), Background = Field,
            Margin = new Thickness(0, 6, 0, 0),
        };

        _filter.TextChanged += (_, _) =>
        {
            var q = _filter.Text.Trim();
            foreach (var (p, box) in _rows)
                box.Visibility = q == "" || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || p.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var site in commonSites.Reverse().Take(10))
        {
            var chip = Button(new Uri(site).Host, 0);
            chip.Padding = new Thickness(8, 3, 8, 3);
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.ToolTip = site;
            chip.Click += (_, _) => _url.Text = site;
            chips.Children.Add(chip);
        }

        var selectShown = Button("Select shown", 110);
        selectShown.Click += (_, _) => SetShown(true);
        var clear = Button("Clear", 70);
        clear.Margin = new Thickness(6, 0, 0, 0);
        clear.Click += (_, _) => SetShown(false);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        tools.Children.Add(selectShown);
        tools.Children.Add(clear);
        tools.Children.Add(_count);

        var open = Button("Open site", 90);
        open.IsDefault = true;
        open.Click += (_, _) => Finish(LaunchAction.Open);
        var close = Button("Close selected", 110);
        close.Margin = new Thickness(8, 0, 0, 0);
        close.ToolTip = "Closes the selected profiles' tabs to free memory. Logins stay saved.";
        close.Click += (_, _) => Finish(LaunchAction.Close);
        var cancel = Button("Cancel", 80);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.IsCancel = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(open);
        buttons.Children.Add(close);
        buttons.Children.Add(cancel);

        var hint = new TextBlock
        {
            Text = "Opens as a new tab in each profile. Profiles that are not on screen get a tab that loads when you open them, which saves memory.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96)), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var top = new StackPanel();
        top.Children.Add(Label("Website to open", 0));
        top.Children.Add(_url);
        top.Children.Add(chips);
        top.Children.Add(Label("Profiles", 6));
        top.Children.Add(_filter);

        var bottom = new StackPanel();
        bottom.Children.Add(tools);
        bottom.Children.Add(hint);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(listBorder);
        Content = root;

        UpdateCount();
        Loaded += (_, _) => _url.Focus();
    }

    public LaunchAction Action { get; private set; }

    public List<Profile> Selected => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Profile).ToList();

    // The address to open, with https:// added when missing. Empty if it is not a usable web address.
    public string Url
    {
        get
        {
            var text = _url.Text.Trim();
            if (text == "") return "";
            var full = text.Contains("://") ? text : "https://" + text;
            return SuggestionStore.Normalize(full) == null || text.Contains(' ') ? "" : full;
        }
    }

    void Finish(LaunchAction action)
    {
        if (Selected.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one profile.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (action == LaunchAction.Open && Url == "")
        {
            MessageBox.Show(this, "Enter a valid web address, for example booking.com.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            _url.Focus();
            return;
        }
        Action = action;
        DialogResult = true;
    }

    void SetShown(bool on)
    {
        foreach (var (_, box) in _rows)
            if (box.Visibility == Visibility.Visible) box.IsChecked = on;
    }

    void UpdateCount() => _count.Text = $"{_rows.Count(r => r.Box.IsChecked == true)} selected";

    static void StyleField(TextBox box)
    {
        box.Background = Field;
        box.Foreground = Fg;
        box.BorderBrush = FieldBorder;
        box.CaretBrush = Fg;
        box.Padding = new Thickness(6, 4, 6, 4);
        box.Margin = new Thickness(0, 4, 0, 0);
    }

    static TextBlock Label(string text, double top) =>
        new() { Text = text, Foreground = Fg, Margin = new Thickness(0, top, 0, 0) };

    static Button Button(string text, double width)
    {
        var b = new Button
        {
            Content = text, Foreground = Fg, Padding = new Thickness(0, 6, 0, 6),
            Style = (Style)Application.Current.FindResource("Flat"),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
        };
        if (width > 0) b.Width = width;
        return b;
    }
}
