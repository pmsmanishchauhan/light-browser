using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LightBrowser;

// A small dark dialog that asks for one line of text (used for renaming a profile).
public class TextPrompt : Window
{
    readonly TextBox _box = new();

    TextPrompt(string title, string label, string initial)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        var fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        Foreground = fg;
        Ui.DarkTitleBar(this);

        _box.Text = initial;
        _box.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
        _box.Foreground = fg;
        _box.CaretBrush = fg;
        _box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));
        _box.Padding = new Thickness(6, 5, 6, 5);
        _box.Margin = new Thickness(0, 6, 0, 0);

        Button Make(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, Width = 80, Padding = new Thickness(0, 6, 0, 6), Foreground = fg,
                Style = (Style)Application.Current.FindResource("Flat"),
                Background = new SolidColorBrush(primary ? Color.FromRgb(0x4F, 0x8E, 0xF7) : Color.FromRgb(0x3A, 0x3B, 0x42)),
            };
            return b;
        }
        var ok = Make("Save", true);
        ok.IsDefault = true;
        ok.Margin = new Thickness(0, 0, 8, 0);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_box.Text)) { _box.Focus(); return; }
            DialogResult = true;
        };
        var cancel = Make("Cancel", false);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = fg });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    // Returns the entered text, or null if cancelled.
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dlg = new TextPrompt(title, label, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
    }
}
