using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightBrowser;

// Code-only dialog: name, group, colour, notes and the pages opened by "Open home pages".
// Two separate suggestion rows: common sites (shared by every group) and sites other profiles in the
// typed group already use.
public class ProfileDialog : Window
{
    static readonly (string Name, string Hex)[] Colors =
    {
        ("Blue", "#4F8EF7"), ("Green", "#3DBE7A"), ("Orange", "#F5A442"), ("Red", "#E5575B"),
        ("Purple", "#A06CF0"), ("Teal", "#2BB5B5"), ("Pink", "#E873B0"),
    };

    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
    static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));

    readonly TextBox _name = NewField();
    readonly TextBox _group = NewField();
    readonly TextBox _notes = NewField();
    readonly TextBox _urls = NewField();
    readonly List<Border> _swatches = new();
    readonly StackPanel _groupChips = new();
    readonly StackPanel _nameChips = new();
    readonly List<string> _existingNames;
    readonly Func<string, IReadOnlyList<string>>? _groupSites;
    int _selected;

    public ProfileDialog(Profile? existing = null, IReadOnlyList<string>? suggestions = null,
                         Func<string, IReadOnlyList<string>>? groupSites = null,
                         IEnumerable<string>? existingNames = null)
    {
        _groupSites = groupSites;
        _existingNames = (existingNames ?? new List<string>()).ToList();
        Title = existing == null ? "New profile" : "Edit profile";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        MakeMultiline(_urls, 80);
        MakeMultiline(_notes, 56);
        _name.Text = existing?.Name ?? "";
        _group.Text = existing?.Group ?? "";
        _notes.Text = existing?.Notes ?? "";
        _urls.Text = string.Join(Environment.NewLine, existing?.HomeUrls ?? new List<string>());

        var swatchRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < Colors.Length; i++)
        {
            var index = i;
            var swatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0),
                Background = (Brush)new BrushConverter().ConvertFromString(Colors[i].Hex)!,
                BorderThickness = new Thickness(2), Cursor = Cursors.Hand, ToolTip = Colors[i].Name,
            };
            swatch.MouseLeftButtonDown += (_, _) => Select(index);
            _swatches.Add(swatch);
            swatchRow.Children.Add(swatch);
        }
        Select(existing == null ? 0 : Math.Max(0, Array.FindIndex(Colors, c => c.Hex == existing.Color)));

        var ok = NewButton("Save");
        ok.IsDefault = true;
        ok.Margin = new Thickness(0, 0, 8, 0);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) { _name.Focus(); return; }
            DialogResult = true;
        };
        var cancel = NewButton("Cancel");
        cancel.IsCancel = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Label("Name (e.g. client or hotel)", 0));
        panel.Children.Add(_name);
        panel.Children.Add(_nameChips);
        panel.Children.Add(Label("Group (optional, e.g. hotel chain)", 12));
        panel.Children.Add(_group);
        panel.Children.Add(Label("Colour", 12));
        panel.Children.Add(swatchRow);
        panel.Children.Add(Label("Home pages (one URL per line)", 12));
        panel.Children.Add(_urls);
        panel.Children.Add(_groupChips);
        if (BuildChips("Suggested sites (worked before, shared by all groups)", (suggestions ?? new List<string>()).Reverse().ToList()) is { } common)
            panel.Children.Add(common);
        panel.Children.Add(Label("Notes (optional, shown on hover)", 12));
        panel.Children.Add(_notes);
        panel.Children.Add(buttons);
        Content = panel;

        _group.TextChanged += (_, _) => { RebuildGroupChips(); RebuildNameChips(); };
        _urls.TextChanged += (_, _) => RebuildNameChips();
        RebuildGroupChips();
        RebuildNameChips();
        // A new profile starts with the best suggested name already filled in (selected, so typing replaces it).
        if (existing == null && _name.Text == "" &&
            NameSuggester.Suggest(_group.Text, HomeUrls, _existingNames).FirstOrDefault() is { } first)
            _name.Text = first;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    // Suggested names from the group, the first home page and the names already used.
    void RebuildNameChips()
    {
        _nameChips.Children.Clear();
        var names = NameSuggester.Suggest(_group.Text, HomeUrls, _existingNames);
        if (names.Count == 0) return;
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        wrap.Children.Add(new TextBlock
        {
            Text = "Suggestions:", Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96)),
            Margin = new Thickness(0, 3, 8, 0), FontSize = 11.5,
        });
        foreach (var name in names)
        {
            var chip = new Button
            {
                Content = name, Foreground = Fg, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4),
                Style = (Style)Application.Current.FindResource("Flat"),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)), FontSize = 12,
            };
            chip.Click += (_, _) => { _name.Text = name; _name.Focus(); _name.SelectAll(); };
            wrap.Children.Add(chip);
        }
        _nameChips.Children.Add(wrap);
    }

    void RebuildGroupChips()
    {
        _groupChips.Children.Clear();
        var sites = _groupSites?.Invoke(_group.Text) ?? new List<string>();
        if (BuildChips("Used by this group's other profiles", sites) is { } chips) _groupChips.Children.Add(chips);
    }

    // A labelled row of clickable site chips. Clicking one appends it to Home pages. Sites already
    // listed there are not offered.
    StackPanel? BuildChips(string label, IReadOnlyList<string> sites)
    {
        if (sites.Count == 0) return null;

        var present = new HashSet<string>(
            HomeUrls.Select(SuggestionStore.Normalize).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var box = new StackPanel();
        box.Children.Add(Label(label, 12));
        box.Children.Add(wrap);

        foreach (var site in sites.Where(s => !present.Contains(s)).Take(12))
        {
            var chip = new Button
            {
                Content = new Uri(site).Host, ToolTip = site, Foreground = Fg,
                Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)Application.Current.FindResource("Flat"),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
            };
            chip.Click += (_, _) =>
            {
                var text = _urls.Text;
                _urls.Text = (text.Length == 0 || text.EndsWith('\n') ? text : text + Environment.NewLine) + site;
                wrap.Children.Remove(chip);
                if (wrap.Children.Count == 0) box.Visibility = Visibility.Collapsed;
            };
            wrap.Children.Add(chip);
        }
        return wrap.Children.Count == 0 ? null : box;
    }

    static TextBox NewField() => new()
    {
        Background = Field, Foreground = Fg, BorderBrush = FieldBorder, CaretBrush = Fg,
        Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0),
    };

    static void MakeMultiline(TextBox box, double height)
    {
        box.AcceptsReturn = true;
        box.Height = height;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.VerticalContentAlignment = VerticalAlignment.Top;
    }

    static TextBlock Label(string text, double top) =>
        new() { Text = text, Foreground = Fg, Margin = new Thickness(0, top, 0, 0) };

    static Button NewButton(string text)
    {
        var b = new Button { Content = text, Width = 84, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg };
        b.Style = (Style)Application.Current.FindResource("Flat");
        b.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
        return b;
    }

    void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _swatches.Count; i++)
            _swatches[i].BorderBrush = i == index ? Brushes.White : Brushes.Transparent;
    }

    public string ProfileName => _name.Text.Trim();

    public string GroupName => _group.Text.Trim();

    public string Notes => _notes.Text.Trim();

    public string ColorHex => Colors[_selected].Hex;

    public List<string> HomeUrls => _urls.Text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(u => u.Contains("://") ? u : "https://" + u)
        .ToList();
}
