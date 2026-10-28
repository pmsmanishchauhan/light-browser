using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightBrowser;

// Code-only dialog: name, group, colour, notes and the pages opened by "Open home pages".
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
    int _selected;

    public ProfileDialog(Profile? existing = null)
    {
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
        panel.Children.Add(Label("Group (optional, e.g. hotel chain)", 12));
        panel.Children.Add(_group);
        panel.Children.Add(Label("Colour", 12));
        panel.Children.Add(swatchRow);
        panel.Children.Add(Label("Home pages (one URL per line)", 12));
        panel.Children.Add(_urls);
        panel.Children.Add(Label("Notes (optional, shown on hover)", 12));
        panel.Children.Add(_notes);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
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
